using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HuTao.Foundation.Abstractions;
using HuTao.Persona;
using HuTao.Foundation;
using HuTao.Voice;
using HuTao.Dialogue.Core;
using HuTao.Knowledge.DocumentReading;
using HuTao.Knowledge.Memory;
using HuTao.Dialogue.Runtime;
using HuTao.Dialogue.Storage;
using HuTao.Dialogue.Tools;
using HuTao.Foundation.Diagnostics;

namespace HuTao.Pet;

/// <summary>一个可切换的角色主题。</summary>
internal sealed record CharacterTheme(
    Color Shell, Color Accent, Color AccentDark, Color Header,
    Color Border, Color Text, Color AssistantBubble, Color Replay,
    Color Thinking, Color ThinkingText);

internal sealed record CharacterConfig(CharacterDefinition Definition, CharacterTheme Theme)
{
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Title => Definition.Title;
    public string Emoji => Definition.Emoji;
    public string PersonaDir => Definition.PersonaDirectory;
    public string RefAudio => Definition.ReferenceAudio;
    public string RefText => Definition.ReferenceText;
    public string EmotionCatalog => Definition.EmotionCatalog;
    public string Greeting => Definition.Greeting;
    public string GreetingAudio => Definition.GreetingAudio;
    public string BusyText => Definition.BusyText;
}

public partial class MainWindow : Window
{
    private IAgentConversation? _agent;
    private ITtsEngine? _tts;
    private DocumentReadingTool? _documentReader;
    private MemoryMaintenance? _memoryMaintenance;
    private ProactiveScheduler? _scheduler;
    private AgentRuntimeFactory? _runtimeFactory;
    private ChatLogStore _store = null!;
    private readonly List<ChatEntry> _entries = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _audioPlaybackGate = new(1, 1);
    private readonly Random _random = new();
    private string? _repoRoot;
    private bool _talking;
    private bool _allowAppAwareness;
    private int _statusEpoch;
    private DateTime _lastUserReply = DateTime.UtcNow;
    private double _nextIgnoreMin = 10;
    private int _currentIndex;
    private int? _pendingCharacterIndex;
    private bool _characterSwitchLoopRunning;

    private static readonly CharacterConfig[] Characters = CharacterCatalog.All
        .Select(character => new CharacterConfig(
            character,
            CharacterThemeCatalog.Get(character.Id)))
        .ToArray();

    private CharacterConfig CurrentCharacter => Characters[_currentIndex];
    private CharacterTheme CurrentTheme => CurrentCharacter.Theme;

    public MainWindow()
    {
        InitializeComponent();
        InitializeWindowPresentation();
        InitializeTrayIcon();
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) =>
        {
            SaveWindowPreferences();
            DisposeTrayIcon();
            _cts.Cancel();
            if (_tts is IAsyncDisposable disposable)
            {
                try
                {
                    // WPF 进程退出后不能继续完成 fire-and-forget 清理；短暂等待，
                    // 确保由桌宠启动的 GPT-SoVITS 常驻子进程被一并关闭。
                    disposable.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // 退出清理失败不阻止窗口关闭。
                }
            }
        };
    }

    private async Task InitAsync()
    {
        ProjectEnvironment.LoadDotEnv(log: Console.WriteLine);
        _repoRoot = ProjectEnvironment.FindRepositoryRoot(
            Environment.CurrentDirectory,
            AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        // 把可读后端日志的位置打出来。这份日志默认就开着（agent + RAG 的完整回合叙事），
        // 但没人知道路径等于没有——出问题时第一件事就是「去哪儿看」。
        Console.WriteLine(HuTao.Foundation.Diagnostics.BackendTrace.Default.Enabled
            ? $"[trace] 可读后端日志: {HuTao.Foundation.Diagnostics.BackendTrace.Default.FilePath}"
            : "[trace] 已关闭（HU_TAO_TRACE=off）");
        _runtimeFactory = new AgentRuntimeFactory(
            _repoRoot,
            _ => TtsRuntimeFactory.CreateGptSovits(
                _repoRoot,
                preferResident: true,
                log: Console.WriteLine),
            Console.WriteLine,
            approveTool: ConfirmToolAsync);
        // 前台感知默认开启（桌宠要陪伴就得知道你在忙什么）；显式设成 false/0/no 才关闭。
        _allowAppAwareness = ReadBooleanEnvironment("HU_TAO_ALLOW_APP_AWARENESS", defaultValue: true);
        UpdateAwarenessButton();
        await LoadCharacterAsync(0);

        // 主动调度器（只启动一次）
        _scheduler = new ProactiveScheduler(
            intervalSeconds: 600, cooldownSeconds: 1200, minIdleSeconds: 300);
        // 调度回调回到 UI 线程，与 Send/角色切换共享同一个说话门，避免 bool 跨线程竞态。
        _ = _scheduler.RunLoopAsync(ct => Dispatcher.InvokeAsync(async () =>
        {
            if (!_talking && !_characterSwitchLoopRunning && _pendingCharacterIndex is null &&
                _scheduler.ShouldTrigger())
                await SayAsync(proactive: true, userInput: null, ct);
        }).Task.Unwrap(), _cts.Token);

        // 休息提醒 + 冷落别扭（只启动一次）
        _ = RestReminderLoopAsync(_cts.Token);
        _ = IgnoreLoopAsync(_cts.Token);
    }

    private async Task LoadCharacterAsync(int index)
    {
        try
        {
            _currentIndex = index;
            var ch = Characters[index];
            ApplyTheme(ch);
            var runtime = (_runtimeFactory ?? throw new InvalidOperationException(
                    "Agent 运行时工厂尚未初始化。"))
                .Create(
                    ch.Definition,
                    sharedTts: _tts,
                    allowAppAwareness: () => _allowAppAwareness,
                    documentProgress: ReportDocumentProgress);
            // 即使某个角色没有声线，也保留已经启动的共享引擎，切回其他角色时可继续复用。
            _tts ??= runtime.Tts;
            _documentReader = runtime.DocumentReader;
            _agent = runtime.Agent;
            _memoryMaintenance = runtime.Memory;

            Title = $"{ch.Name}桌宠";
            TitleText.Text = $"{ch.Emoji} {ch.Name}";
            TitleText.ToolTip = $"{ch.Name} · {ch.Title}";

            // 每个角色独立的记忆文件，避免记忆串味
            _store = new ChatLogStore(
                Path.Combine(_repoRoot!, "data", $"chat_log_{ch.Id}.json"),
                audioSizeLimitBytes: 512L * 1024 * 1024);
            _entries.Clear();
            _entries.AddRange(_store.Load());

            BubblePanel.Children.Clear();
            foreach (var e in _entries)
                if (!SpeechDeliverySession.IsLegacyFailureBubble(e.Role, e.Text))
                    AddBubble(e.Text, e.Role == "user", e.Audio, record: false);

            // 原始历史文件不删除；旧版程序的诊断气泡不再显示，也不注入角色上下文。
            var recent = _entries.Where(e => !SpeechDeliverySession.IsLegacyFailureBubble(e.Role, e.Text))
                .TakeLast(20).Select(e => new ChatMessage(e.Role, e.Text));
            _agent.RestoreHistory(recent);

            _lastUserReply = DateTime.UtcNow;
            _nextIgnoreMin = 10;
            await SayGreetingAsync(ch, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("character.load", ex);
            // 角色加载属于系统操作，不将技术异常伪装成角色台词。
            SwitchButton.ToolTip = "角色暂时未能加载，请稍后重试。诊断已写入本机日志。";
        }

    }

    /// <summary>一次说话：先「正在忙」占位，再按顺序显示气泡并播放对应语音。</summary>
    private async Task SayAsync(bool proactive, string? userInput, CancellationToken ct)
    {
        if (_agent is null || _talking)
            return;
        _talking = true;

        Border? thinking = null;
        Dispatcher.Invoke(() => thinking = AddThinkingBubble());
        var speech = new SpeechDeliverySession();
        var displayed = false;

        try
        {
            var segments = await _agent.GenerateSpeechSegmentsAsync(userInput, proactive, ct);
            if (segments.Count == 0)
                segments = [new SpeechSegment("……", "neutral", 0.3)];

            foreach (var segment in segments)
            {
                ct.ThrowIfCancellationRequested();
                var seg = segment.Text;
                var audioPath = await speech.PrepareAudioAsync(segment,
                    token => _agent.SynthesizeAsync(seg, segment.Emotion, segment.Intensity, token), ct);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (thinking is not null && BubblePanel.Children.Contains(thinking))
                    {
                        BubblePanel.Children.Remove(thinking);
                        thinking = null;
                    }
                    AddBubble(seg, isUser: false, audioPath);
                    displayed = true;
                });

                // 等本段实际播放完再继续下一段，避免连续气泡的语音重叠。
                if (audioPath is not null && File.Exists(audioPath))
                    await PlayAudioAsync(audioPath, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("conversation.delivery", ex);
            await Dispatcher.InvokeAsync(() =>
            {
                // 未获得文字的错误才使用角色兜底；TTS 错误已在段落级吞下，不走这里。
                if (!displayed && !proactive)
                    AddBubble(CurrentCharacter.Id switch
                    {
                        "klee" => "唔，可莉刚刚没跟上……你再说一次好不好？",
                        "furina" => "容我整理一下思绪……刚才那句，能再说一次吗？",
                        _ => "唔，本堂主刚刚走了下神……你再说一次好不好？"
                    }, isUser: false, record: false);
            });
        }
        finally
        {
            _scheduler?.NotifyConversationActivity();
            await Dispatcher.InvokeAsync(() =>
            {
                if (thinking is not null) BubblePanel.Children.Remove(thinking);
            });
            _talking = false;
        }
    }

    /// <summary>当前角色的「正在忙」占位气泡，返回引用供后续移除。</summary>
    private Border AddThinkingBubble()
    {
        var border = new Border
        {
            Background = new SolidColorBrush(CurrentTheme.Thinking),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(4, 3, 4, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 270,
        };
        border.Child = new TextBlock
        {
            Text = CurrentCharacter.BusyText,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(CurrentTheme.ThinkingText),
        };
        BubblePanel.Children.Add(border);
        BubbleScroll.ScrollToEnd();
        UpdateCompactBubble(CurrentCharacter.BusyText);
        return border;
    }

    /// <summary>整段括号内容视为动作气泡，不送入 TTS。</summary>
    private static bool IsActionSegment(string text) => SpeechText.IsAction(text);

    /// <summary>串行播放语音；同步播放放到后台线程，避免阻塞 WPF 界面。</summary>
    private async Task PlayAudioAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return;

        await _audioPlaybackGate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                using var player = new SoundPlayer(path);
                player.PlaySync();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("speech.playback", ex);
        }
        finally
        {
            _audioPlaybackGate.Release();
        }
    }

    /// <summary>休息提醒循环：空闲 5 分钟提醒一次；连续工作 40 分钟提醒一次。</summary>
    private async Task RestReminderLoopAsync(CancellationToken ct)
    {
        var idleTool = new IdleTool();
        var lastIdleRemind = DateTime.UtcNow;
        var activeSince = DateTime.UtcNow;
        var warned40 = false;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { break; }

            var idleSec = ParseIdleSeconds(await idleTool.ExecuteAsync(ct: ct));

            // 后台整合长期记忆：只在用户确实空闲时跑，不占用对话链路。
            // 复用这个已有的 15 秒轮询，不再单开一个定时器。
            if (_memoryMaintenance is not null)
            {
                try
                {
                    await _memoryMaintenance.RunIfDueAsync(
                        TimeSpan.FromSeconds(idleSec), DateTimeOffset.Now, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { LocalDiagnosticLog.Default.Write("memory.maintain", ex); }
            }

            if (idleSec >= 300)
            {
                if ((DateTime.UtcNow - lastIdleRemind).TotalMinutes >= 5)
                {
                    lastIdleRemind = DateTime.UtcNow;
                    await SayReminderAsync("你都坐了好一会儿啦，起来活动活动筋骨吧~", ct);
                }
                activeSince = DateTime.UtcNow;
            }
            else
            {
                var activeMin = (DateTime.UtcNow - activeSince).TotalMinutes;
                if (activeMin >= 40 && !warned40)
                {
                    warned40 = true;
                    await SayReminderAsync("连着忙四十分钟啦，快喝口水休息一下！", ct);
                }
                if (activeMin < 40)
                    warned40 = false;
            }
        }
    }

    /// <summary>冷落别扭：用户 ≥10 分钟不理，胡桃发小别扭，越久越重、间隔不固定。</summary>
    private async Task IgnoreLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }

            var silentMin = (DateTime.UtcNow - _lastUserReply).TotalMinutes;
            if (silentMin >= _nextIgnoreMin)
            {
                var msg = IgnoreMessage(CurrentCharacter.Id, silentMin);
                await SayReminderAsync(msg, ct);

                // 重新计时；下次阈值不固定（10~15 分钟随机）
                _lastUserReply = DateTime.UtcNow;
                _nextIgnoreMin = 10 + _random.Next(0, 6);
            }
        }
    }

    private static string IgnoreMessage(string characterId, double silentMin)
    {
        if (characterId == "furina")
        {
            return silentMin switch
            {
                < 20 => "观众怎么突然安静了？本水神可还在等你的回应呢。",
                < 35 => "这场独角戏演得也太久了……你该不会忘了我吧？",
                < 50 => "喂！让主演独自等这么久，可不是合格观众的礼仪！",
                _ => "本水神宣布暂停演出！除非你回来好好解释，哼！",
            };
        }

        if (characterId == "klee")
        {
            return silentMin switch
            {
                < 20 => "唔？你去哪儿啦？可莉还在这里等你一起玩呢。",
                < 35 => "已经好久没看到你了，可莉都快把蹦蹦炸弹数完啦……",
                < 50 => "你再不回来，可莉就要去找琴团长问问你去哪儿了！",
                _ => "呜……可莉真的等了好久。回来陪可莉说说话，好不好？",
            };
        }

        return silentMin switch
        {
            < 20 => "喂——怎么不理本堂主啦？哼哼，我可记着呢。",
            < 35 => "都过去好久了，你真的一点都不想本堂主吗？有点小难过……",
            < 50 => "本堂主有点生气啦！你再不回来，往生堂的优惠可就要过期了哦！",
            _ => "哼！本堂主真的要闹脾气了！这么久都不理我，等你回来可要好好补偿！",
        };
    }

    private static int ParseIdleSeconds(string idleStr)
    {
        var m = System.Text.RegularExpressions.Regex.Match(idleStr, @"(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var s) ? s : 0;
    }

    /// <summary>用固定短文案直接合成播放（不走 LLM），用于休息提醒 / 别扭。</summary>
    private async Task SayReminderAsync(string text, CancellationToken ct)
    {
        if (_agent is null || _talking)
            return;
        _talking = true;
        try
        {
            var speech = new SpeechDeliverySession();
            var audioPath = await speech.PrepareAudioAsync(new SpeechSegment(text, "concerned", 0.65),
                token => _agent.SynthesizeAsync(text, "concerned", 0.65, token), ct);
            await Dispatcher.InvokeAsync(() =>
            {
                AddBubble(text, isUser: false, audioPath);
            });

            if (audioPath is not null)
                await PlayAudioAsync(audioPath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("speech.reminder", ex);
        }
        finally
        {
            _scheduler?.NotifyConversationActivity();
            _talking = false;
        }
    }

    /// <summary>加一条气泡（角色语音带「重播」按钮），并写入聊天记录。</summary>
    private void AddBubble(string text, bool isUser, string? audio = null, bool record = true)
    {
        if (IsActionSegment(text))
            audio = null;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 20,
            Foreground = isUser ? Brushes.White : new SolidColorBrush(CurrentTheme.Text),
        });

        // 角色语音气泡：加 galgame 式「重播」按钮
        if (!isUser && audio is not null)
        {
            var path = audio;
            var btn = new Button
            {
                Content = "▶",
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(CurrentTheme.Replay),
                Foreground = new SolidColorBrush(CurrentTheme.AccentDark),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = path,
            };
            btn.Click += async (_, _) => await PlayAudioAsync(path);
            panel.Children.Add(btn);
        }

        var border = new Border
        {
            Background = isUser
                ? new SolidColorBrush(CurrentTheme.Accent)
                : new SolidColorBrush(CurrentTheme.AssistantBubble),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(4, 3, 4, 3),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 270,
            Child = panel,
        };
        BubblePanel.Children.Add(border);
        BubbleScroll.ScrollToEnd();

        if (!isUser)
            UpdateCompactBubble(text);

        if (record)
        {
            _entries.Add(new ChatEntry(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                isUser ? "user" : "assistant", text, audio));
            PruneAndSave();
        }
    }

    private void PruneAndSave()
    {
        if (_store is null)
            return;
        // 语音保存区大小上限：超限删除最旧音频
        foreach (var path in _store.ComputePrunableAudio(_entries))
        {
            try { File.Delete(path); } catch { /* ignore */ }
            var idx = _entries.FindIndex(e => e.Audio == path);
            if (idx >= 0)
                _entries[idx] = _entries[idx] with { Audio = null };
        }
        try { _store.Save(_entries); }
        catch (Exception ex) { LocalDiagnosticLog.Default.Write("chat.save", ex); }
    }

    // ── UI 事件 ──
    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Awareness_Click(object sender, RoutedEventArgs e)
    {
        _allowAppAwareness = !_allowAppAwareness;
        UpdateAwarenessButton();
        ShowStatus(_allowAppAwareness
            ? "应用状态感知已开启：可读取前台进程名与窗口标题，用于判断你在做什么"
            : "应用状态感知已关闭：不再读取任何窗口信息");
    }

    /// <summary>系统提示走独立状态行；角色气泡流里只允许出现角色说的话。</summary>
    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        _statusEpoch++;
        var epoch = _statusEpoch;
        _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ =>
            Dispatcher.InvokeAsync(() =>
            {
                if (epoch == _statusEpoch)
                    StatusText.Visibility = Visibility.Collapsed;
            }), TaskScheduler.Default);
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Send();
    }

    private void Send()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0 || _agent is null || _talking)
            return;
        AddBubble(text, isUser: true);
        _scheduler?.NotifyConversationActivity();
        InputBox.Clear();
        _lastUserReply = DateTime.UtcNow; // 用户回复了，重置冷落计时
        _ = SayAsync(proactive: false, userInput: text, _cts.Token);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _store.Clear();
        _entries.Clear();
        _agent?.RestoreHistory([]);
        BubblePanel.Children.Clear();
        var message = CurrentCharacter.Id switch
        {
            "hutao" => "聊天记录清空啦，重新聊点什么吧~",
            "klee" => "可莉把旧的冒险故事收好啦！我们重新开始新的旅程吧~",
            _ => "旧剧本已经收好，下一幕重新开始！",
        };
        AddBubble(message, isUser: false);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _isExiting = true;
        Close();
    }

    private void ApplyTheme(CharacterConfig character)
    {
        var theme = character.Theme;
        ShellBorder.Background = new SolidColorBrush(theme.Shell);
        ShellBorder.BorderBrush = new SolidColorBrush(theme.Border);
        TitleBarBorder.Background = new SolidColorBrush(theme.Header);
        TitleText.Foreground = new SolidColorBrush(theme.AccentDark);
        SendButton.Background = new SolidColorBrush(theme.Accent);
        InputBox.BorderBrush = new SolidColorBrush(theme.Border);
        InputBox.CaretBrush = new SolidColorBrush(theme.AccentDark);

        foreach (var button in new[]
                 {
                     CloseButton, HideButton, CompactButton, ClearButton,
                     SwitchButton, AwarenessButton, ReadButton,
                 })
            button.Foreground = new SolidColorBrush(theme.AccentDark);
        CharacterPickerBorder.BorderBrush = new SolidColorBrush(theme.Border);
        CharacterPickerTitle.Foreground = new SolidColorBrush(theme.AccentDark);
        CompactAvatarText.Text = character.Emoji;
        CompactCharacterName.Text = character.Name;
        CompactCharacterName.Foreground = new SolidColorBrush(theme.AccentDark);
        CompactBubbleText.Foreground = new SolidColorBrush(theme.Text);
        CompactAvatarBorder.Background = new SolidColorBrush(theme.Shell);
        CompactAvatarBorder.BorderBrush = new SolidColorBrush(theme.Border);
        CompactBubbleBorder.Background = new SolidColorBrush(theme.Shell);
        CompactBubbleBorder.BorderBrush = new SolidColorBrush(theme.Border);
        UpdateAwarenessButton();
    }

    private void UpdateAwarenessButton()
    {
        AwarenessButton.Content = _allowAppAwareness ? "◉" : "○";
        AwarenessButton.ToolTip = _allowAppAwareness
            ? "前台感知已开启：读取当前前台应用的进程名与窗口标题；点击关闭"
            : "前台感知已关闭；点击开启（仅读前台一个窗口，不截图、不读文件）";
    }

    private static bool ReadBooleanEnvironment(string name, bool defaultValue = false)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrEmpty(value))
            return defaultValue;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

}
