using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.ChatRoom;
using HuTao.Persona;
using HuTao.Foundation.Diagnostics;
using HuTao.Dialogue.Llm;
using HuTao.Dialogue.Runtime;

namespace HuTao.Pet;

/// <summary>
/// 多人聊天室窗口。
///
/// 分工很清楚：**调度与角色扮演全在 Core 的 <see cref="ChatRoom"/> 里**，
/// 这个窗口只做三件 UI 的事——选谁入场、把每一轮画成气泡、把语音播出来。
/// 这样聊天室既能被 WPF 用，也能被 Host 的控制台演示用，不会把逻辑焊死在界面里。
///
/// 沉浸感的两条设计：
/// 1. 角色的发言走各自 Agent 的**完整链路**（人设 + 记忆 + 沉浸闸门），
///    所以聊天室里也不会出戏——这不是 UI 保证的，是 Core 保证的；
/// 2. 导演的调度理由默认**折叠**。那是作者视角的信息，摆在对话流里会破坏「我在看她们聊天」。
/// </summary>
public partial class ChatRoomWindow : Window
{
    private readonly AgentRuntimeFactory _factory;
    private readonly ITtsEngine? _sharedTts;
    private readonly List<Participant> _cast = [];
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _audioGate = new(1, 1);

    private ChatRoom? _room;
    private bool _busy;
    private bool _autoRunning;

    private sealed record Participant(string Id, string Name, string Emoji, Color Accent);

    public ChatRoomWindow(AgentRuntimeFactory factory, ITtsEngine? sharedTts, string? preselected = null)
    {
        InitializeComponent();
        _factory = factory;
        _sharedTts = sharedTts;

        // 候选名单就是桌宠的角色表，所以新增角色不需要改这个窗口。
        foreach (var definition in CharacterCatalog.All)
            _cast.Add(new Participant(definition.Id, definition.Name, definition.Emoji,
                CharacterThemeCatalog.Get(definition.Id).Accent));

        // 默认阵容：当前桌宠角色 + 一位异环角色（跨作品是这间聊天室最有意思的地方）。
        if (!string.IsNullOrWhiteSpace(preselected) && _cast.Any(c => c.Id == preselected))
            _selected.Add(preselected!);
        foreach (var fallback in new[] { "hutao", "lacrimosa", "tajiduo" })
            if (_selected.Count < 2 && _cast.Any(c => c.Id == fallback))
                _selected.Add(fallback);

        Closing += (_, _) =>
        {
            _cts.Cancel();
            if (_room is null)
                return;
            foreach (var participant in _room.Participants)
                if (participant.Agent is IAsyncDisposable disposable)
                    _ = disposable.DisposeAsync();
        };

        ShowSetup();
    }

    // ── 开始之前：选人 ────────────────────────────────────────────────────

    /// <summary>未开始时窗口处于「搭班子」状态：只有角色开关可用，其余暂时收起来。</summary>
    private void ShowSetup()
    {
        TitleText.Text = "聊天室 · 选人";
        SubtitleText.Text = "至少选两位，建议跨作品";
        InputBox.IsEnabled = false;
        NextButton.IsEnabled = false;
        AutoButton.IsEnabled = false;
        DebugButton.IsEnabled = false;
        StepButton.Content = "开始";

        RosterPanel.Children.Clear();
        foreach (var participant in _cast)
        {
            var chip = MakeCharacterChip(participant, toggle: true);
            RosterPanel.Children.Add(chip);
        }
        SetStatus(_selected.Count < 2
            ? "点上面的角色卡选人；选满两位就能开始。"
            : $"已选：{string.Join('、', _selected.Select(NameOf))}");
        RenderHint("选好角色后点「开始」。" +
                   "用户的默认身份是旁观者，随时可以插话。");
    }

    private Button MakeCharacterChip(Participant participant, bool toggle)
    {
        var on = _selected.Contains(participant.Id);
        var chip = new Button
        {
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 7, 0),
            Cursor = toggle ? Cursors.Hand : Cursors.Arrow,
            BorderThickness = new Thickness(1.4),
            Background = new SolidColorBrush(on
                ? Color.FromArgb(38, participant.Accent.R, participant.Accent.G, participant.Accent.B)
                : Color.FromArgb(10, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(on
                ? participant.Accent
                : Color.FromArgb(40, 0, 0, 0)),
            Content = new TextBlock
            {
                Text = $"{participant.Emoji} {participant.Name}",
                FontSize = 12.5,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(on
                    ? participant.Accent
                    : Color.FromRgb(120, 110, 100)),
            },
        };
        if (toggle)
        {
            chip.Click += (_, _) => ToggleCharacter(participant.Id);
            chip.ToolTip = "点一下加入 / 退出聊天室";
        }
        return chip;
    }

    private void ToggleCharacter(string id)
    {
        if (_selected.Contains(id))
            _selected.Remove(id);
        else
            _selected.Add(id);
        ShowSetup();
    }

    private async void Step_Click(object sender, RoutedEventArgs e)
    {
        if (_room is null)
        {
            await StartAsync();
            return;
        }
        await RunOneTurnAsync(interject: InputBox.Text);
    }

    private async Task StartAsync()
    {
        if (_busy)
            return;
        if (_selected.Count < 2)
        {
            SetStatus("至少需要两位角色，聊天室才成立。");
            return;
        }

        _busy = true;
        StepButton.IsEnabled = false;
        SetStatus("正在装配角色…");
        try
        {
            var participants = new List<ChatRoomParticipant>();
            foreach (var id in _selected)
            {
                var definition = CharacterCatalog.Find(id);
                if (definition is null)
                    continue;
                // allowAppAwareness: false —— 聊天室里角色不该盯着用户的前台窗口，
                // 那会引出「你在看代码哦」这类破坏场景的话。
                // memoryScope: 聊天室另开一份记忆，避免与桌宠窗口的 store 争抢同一个文件。
                var runtime = await Task.Run(() => _factory.Create(
                    definition,
                    sharedTts: _sharedTts,
                    allowAppAwareness: () => false,
                    memoryScope: "chatroom"));
                participants.Add(new ChatRoomParticipant(
                    definition.Id, definition.Name, runtime.Agent, runtime.Persona));
            }

            var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            ILLMProvider directorLlm = string.IsNullOrWhiteSpace(apiKey)
                ? new MockLlmProvider(participants[0].Persona)
                : new DeepSeekLlmProvider(apiKey);

            _room = new ChatRoom(participants, new LlmChatRoomDirector(directorLlm),
                onSpeakerFailed: (name, error) =>
                {
                    // 模型抖一下不该让聊天室散场：Core 会自动换下一位，这里只把原因说清楚。
                    LocalDiagnosticLog.Default.Write("chatroom.speaker_unavailable", error);
                    Dispatcher.InvokeAsync(() => SetStatus($"{name} 这一轮没能开口（模型暂时不可用），已换下一位。"));
                });

            // 进入对话模式
            TitleText.Text = "聊天室";
            SubtitleText.Text = $"在场：{string.Join('、', participants.Select(p => p.DisplayName))} · 你在旁观";
            InputBox.IsEnabled = true;
            NextButton.IsEnabled = true;
            AutoButton.IsEnabled = true;
            DebugButton.IsEnabled = true;
            StepButton.Content = "插话";
            StepButton.IsEnabled = true;
            RosterPanel.Children.Clear();
            foreach (var participant in _cast.Where(c => _selected.Contains(c.Id)))
                RosterPanel.Children.Add(MakeCharacterChip(participant, toggle: false));

            TranscriptPanel.Children.Clear();
            SetStatus(string.IsNullOrWhiteSpace(apiKey)
                ? "未检测到 DEEPSEEK_API_KEY，当前是 Mock 模式（角色只会念固定台词）。"
                : "点 ▸ 让角色接一句，或打字插话。▶ 自动对谈。");
            RenderHint("（你们围坐在一起，气氛轻松。）");
        }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("chatroom.start", ex);
            SetStatus($"装配失败：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    // ── 对话 ──────────────────────────────────────────────────────────────

    private async void Next_Click(object sender, RoutedEventArgs e)
        => await RunOneTurnAsync(interject: null);

    private async Task RunOneTurnAsync(string? interject)
    {
        if (_room is null || _busy)
            return;

        _busy = true;
        NextButton.IsEnabled = false;
        StepButton.IsEnabled = false;
        try
        {
            var userText = interject?.Trim();
            if (!string.IsNullOrEmpty(userText))
            {
                _room.Interject(userText);
                AppendTurn(new ChatRoomTurn("user", "你", userText, true, DateTimeOffset.Now));
                InputBox.Clear();
            }

            SetStatus("……");
            var turn = await _room.StepAsync(_cts.Token);
            if (turn is null)
            {
                SetStatus($"对话自然收场（共 {_room.TurnCount} 轮）。可以插话重新起话题。");
                _autoRunning = false;
                AutoButton.Content = "▶";
                return;
            }
            AppendTurn(turn);
            ShowDirector();
            SetStatus($"第 {_room.TurnCount} 轮 · {turn.SpeakerName}");
            if (turn.AudioPath is not null)
                await PlayAudioAsync(turn.AudioPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("chatroom.turn", ex);
            SetStatus($"这一轮失败了：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _busy = false;
            if (_room is not null)
            {
                NextButton.IsEnabled = true;
                StepButton.IsEnabled = true;
            }
        }
    }

    private async void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_room is null || _busy)
            return;
        if (_autoRunning)
        {
            _autoRunning = false;
            AutoButton.Content = "▶";
            SetStatus("已暂停自动对谈。");
            return;
        }

        _autoRunning = true;
        AutoButton.Content = "⏸";
        while (_autoRunning && !_cts.IsCancellationRequested)
        {
            await RunOneTurnAsync(interject: null);
            if (_room.TurnCount >= 40)
            {
                SetStatus("自动对谈已达 40 轮，先停一下——再聊下去容易变成互相客套。");
                break;
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(600), _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
        _autoRunning = false;
        AutoButton.Content = "▶";
    }

    // ── 渲染 ──────────────────────────────────────────────────────────────

    private void AppendTurn(ChatRoomTurn turn)
    {
        var accent = _cast.FirstOrDefault(c => c.Id == turn.SpeakerId)?.Accent
                     ?? Color.FromRgb(120, 110, 100);

        var name = new TextBlock
        {
            Text = turn.SpeakerName,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(turn.IsUser ? Color.FromRgb(120, 110, 100) : accent),
            Margin = new Thickness(turn.IsUser ? 0 : 4, 0, 4, 2),
            HorizontalAlignment = turn.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };

        var bubble = new Border
        {
            Padding = new Thickness(11, 8, 11, 8),
            CornerRadius = new CornerRadius(13),
            MaxWidth = 400,
            HorizontalAlignment = turn.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = new SolidColorBrush(turn.IsUser
                ? Color.FromRgb(238, 236, 232)
                : Color.FromArgb(26, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1.2),
            BorderBrush = new SolidColorBrush(turn.IsUser
                ? Color.FromArgb(30, 0, 0, 0)
                : Color.FromArgb(90, accent.R, accent.G, accent.B)),
            Child = new TextBlock
            {
                Text = turn.Text,
                FontSize = 13.5,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
        if (turn.AudioPath is not null)
        {
            // 有语音才给按钮：角色没声线时（例如安魂曲）不该出现一个点了没反应的图标。
            var play = new Button
            {
                Content = "🔊",
                Width = 24,
                Height = 20,
                FontSize = 11,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "重播这一句",
            };
            play.Click += async (_, _) => await PlayAudioAsync(turn.AudioPath);
            DockPanel.SetDock(play, Dock.Right);
            header.Children.Add(play);
        }
        header.Children.Add(name);

        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        block.Children.Add(header);
        block.Children.Add(bubble);
        TranscriptPanel.Children.Add(block);
        TranscriptScroll.ScrollToEnd();
    }

    private void RenderHint(string text)
    {
        TranscriptPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 138, 126)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 10),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });
    }

    private void ShowDirector()
    {
        var decision = _room?.LastDecision;
        if (decision is null)
            return;
        DebugText.Text = $"导演 → {NameOf(decision.NextSpeakerId)}：{decision.Reason}";
    }

    private string NameOf(string id)
        => _cast.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    // ── 语音 ──────────────────────────────────────────────────────────────

    /// <summary>串行播放；同步播放丢到后台线程，不阻塞界面。</summary>
    private async Task PlayAudioAsync(string path)
    {
        if (!File.Exists(path))
            return;
        await _audioGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                using var player = new SoundPlayer(path);
                player.PlaySync();
            });
        }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("chatroom.playback", ex);
        }
        finally
        {
            _audioGate.Release();
        }
    }

    // ── 窗口交互 ──────────────────────────────────────────────────────────

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await RunOneTurnAsync(interject: InputBox.Text);
    }

    private void Debug_Click(object sender, RoutedEventArgs e)
        => DebugBorder.Visibility = DebugBorder.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
