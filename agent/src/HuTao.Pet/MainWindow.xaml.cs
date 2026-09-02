using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Core;
using HuTao.Agent.Core.Llm;
using HuTao.Agent.Core.Persona;
using HuTao.Agent.Core.Rag;
using HuTao.Agent.Core.Storage;
using HuTao.Agent.Core.Tools;
using HuTao.Agent.Core.Tts;

namespace HuTao.Pet;

/// <summary>一个可切换的角色主题。</summary>
internal sealed record CharacterTheme(
    Color Shell, Color Accent, Color AccentDark, Color Header,
    Color Border, Color Text, Color AssistantBubble, Color Replay,
    Color Thinking, Color ThinkingText);

internal sealed record CharacterConfig(
    string Id, string Name, string Title, string Emoji,
    string PersonaDir, string RefAudio, string RefText,
    string EmotionCatalog, string Greeting, string BusyText, CharacterTheme Theme);

public partial class MainWindow : Window
{
    private ReactAgent? _agent;
    private ITtsEngine? _tts;
    private ProactiveScheduler? _scheduler;
    private ChatLogStore _store = null!;
    private readonly List<ChatEntry> _entries = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _audioPlaybackGate = new(1, 1);
    private readonly Random _random = new();
    private string? _repoRoot;
    private bool _talking;
    private bool _allowAppAwareness;
    private DateTime _lastUserReply = DateTime.UtcNow;
    private double _nextIgnoreMin = 10;
    private int _currentIndex;

    private static readonly CharacterConfig[] Characters =
    [
        new("hutao", "胡桃", "往生堂堂主", "🍑",
            "data/persona/hutao",
            "data/voice/hutao/wav/a4eedbf833d51d47.wav",
            "哼哼，切勿质疑我的业务能力！",
            "data/persona/hutao/emotion-references.json",
            "本堂主来啦！有什么想聊的，尽管说~",
            "胡桃正在忙，可能还没有看到消息哦~",
            new CharacterTheme(
                Color.FromArgb(248, 255, 255, 255),
                Color.FromRgb(224, 138, 60), Color.FromRgb(122, 63, 22),
                Color.FromArgb(64, 176, 106, 48), Color.FromArgb(224, 176, 106, 48),
                Color.FromRgb(70, 45, 20), Colors.White, Color.FromRgb(240, 224, 200),
                Color.FromRgb(240, 238, 234), Color.FromRgb(158, 138, 118))),
        new("furina", "芙宁娜", "枫丹水神", "💧",
            "data/persona/furina",
            "data/voice/furina/wav/4e4c5b22eb6c9354.wav",
            "不要把舞台演出和剧团里的关系混为一谈行吗？",
            "data/persona/furina/emotion-references.json",
            "欢迎来到本水神的剧场，好戏开场~",
            "芙宁娜正在准备下一幕，请稍候片刻~",
            new CharacterTheme(
                Color.FromArgb(248, 247, 251, 255),
                Color.FromRgb(75, 130, 190), Color.FromRgb(31, 73, 116),
                Color.FromArgb(64, 79, 134, 198), Color.FromArgb(224, 79, 134, 198),
                Color.FromRgb(37, 63, 91), Colors.White, Color.FromRgb(221, 234, 248),
                Color.FromRgb(238, 244, 251), Color.FromRgb(109, 135, 165))),
    ];

    private CharacterConfig CurrentCharacter => Characters[_currentIndex];
    private CharacterTheme CurrentTheme => CurrentCharacter.Theme;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) =>
        {
            _cts.Cancel();
            if (_tts is IAsyncDisposable disposable)
                _ = disposable.DisposeAsync();
        };
    }

    private async Task InitAsync()
    {
        LoadEnvFile();
        _repoRoot = FindRepoRoot() ?? AppContext.BaseDirectory;
        _allowAppAwareness = ReadBooleanEnvironment("HU_TAO_ALLOW_APP_AWARENESS");
        UpdateAwarenessButton();
        await LoadCharacterAsync(0);

        // 主动调度器（只启动一次）
        _scheduler = new ProactiveScheduler(
            intervalSeconds: 60, cooldownSeconds: 120, minIdleSeconds: 30);
        _ = _scheduler.RunLoopAsync(ct => SayAsync(proactive: true, userInput: null, ct), _cts.Token);

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
            var persona = PersonaLoader.Load(Path.Combine(_repoRoot!, ch.PersonaDir));

            var tools = new List<IAgentTool>
            {
                new TimeTool(),
                new ActiveWindowTool(() => _allowAppAwareness),
                new IdleTool(),
            };
            // 剧情档案是胡桃的第四面墙能力，其他角色不共享该工具。
            if (ch.Id == "hutao")
            {
                var storyIndex = Path.Combine(_repoRoot!, "data", "story", "index.json");
                var dialogueRoot = Path.Combine(_repoRoot!, "data", "story", "dialogue");
                tools.Add(new StoryKnowledgeTool(
                    StoryVectorStore.Load(storyIndex),
                    new StoryDialogueStore(dialogueRoot)));
            }
            var llm = BuildLlm(persona);
            // 角色切换只替换人设和参考音频，复用同一个 TTS 引擎/常驻 Python 进程。
            _tts ??= BuildTts();

            var importantMemory = ch.Id == "hutao"
                ? new ImportantMemoryStore(Path.Combine(
                    _repoRoot!, "data", "important_memories_hutao.json"))
                : null;
            var emotionReferences = EmotionReferenceCatalog.Load(
                Path.Combine(_repoRoot!, ch.EmotionCatalog),
                Path.Combine(_repoRoot!, ch.RefAudio), ch.RefText);
            _agent = new ReactAgent(
                persona, llm, _tts, tools,
                refAudio: Path.Combine(_repoRoot!, ch.RefAudio),
                refText: ch.RefText,
                importantMemory: importantMemory,
                emotionReferences: emotionReferences);

            Title = $"{ch.Name}桌宠";
            TitleText.Text = $"{ch.Emoji} {ch.Name} · {ch.Title}";

            // 每个角色独立的记忆文件，避免记忆串味
            _store = new ChatLogStore(
                Path.Combine(_repoRoot!, "data", $"chat_log_{ch.Id}.json"),
                audioSizeLimitBytes: 512L * 1024 * 1024);
            _entries.Clear();
            _entries.AddRange(_store.Load());

            BubblePanel.Children.Clear();
            foreach (var e in _entries)
                AddBubble(e.Text, e.Role == "user", e.Audio, record: false);

            var recent = _entries.TakeLast(20).Select(e => new ChatMessage(e.Role, e.Text));
            _agent.RestoreHistory(recent);

            _lastUserReply = DateTime.UtcNow;
            _nextIgnoreMin = 10;
            await SayGreetingAsync(ch, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LoadCharacter] 异常: {ex}");
            AddBubble($"切换角色出错了：{ex.Message}", isUser: false, audio: null, record: false);
        }

    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (_talking)
            return;
        _currentIndex = (_currentIndex + 1) % Characters.Length;
        await LoadCharacterAsync(_currentIndex);
    }

    /// <summary>角色加载完成后的有声开场白；失败时保留文字降级。</summary>
    private async Task SayGreetingAsync(CharacterConfig character, CancellationToken ct)
    {
        if (_agent is null)
            return;

        _talking = true;
        var thinking = AddThinkingBubble();
        try
        {
            var audio = await _agent.SynthesizeAsync(
                character.Greeting, "cheerful", 0.7, ct);
            if (BubblePanel.Children.Contains(thinking))
                BubblePanel.Children.Remove(thinking);
            AddBubble(character.Greeting, isUser: false, audio?.AudioPath, record: false);
            if (audio is not null && File.Exists(audio.AudioPath))
                await PlayAudioAsync(audio.AudioPath, ct);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (BubblePanel.Children.Contains(thinking))
                BubblePanel.Children.Remove(thinking);
            AddBubble(character.Greeting, isUser: false, audio: null, record: false);
        }
        finally
        {
            _talking = false;
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

        try
        {
            var segments = await _agent.GenerateSpeechSegmentsAsync(userInput, proactive, ct);
            if (segments.Count == 0)
                segments = [new SpeechSegment("……", "neutral", 0.3)];

            foreach (var segment in segments)
            {
                ct.ThrowIfCancellationRequested();
                var seg = segment.Text;
                var isAction = IsActionSegment(seg);
                var audio = isAction
                    ? null
                    : await _agent.SynthesizeAsync(
                        seg, segment.Emotion, segment.Intensity, ct);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (thinking is not null && BubblePanel.Children.Contains(thinking))
                    {
                        BubblePanel.Children.Remove(thinking);
                        thinking = null;
                    }
                    AddBubble(seg, isUser: false, audio?.AudioPath);
                });

                // 等本段实际播放完再继续下一段，避免连续气泡的语音重叠。
                if (audio is not null && File.Exists(audio.AudioPath))
                    await PlayAudioAsync(audio.AudioPath, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (thinking is not null)
                    BubblePanel.Children.Remove(thinking);
                AddBubble($"（说话卡住了：{ex.Message}）", isUser: false);
            });
        }
        finally
        {
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
        return border;
    }

    /// <summary>整段括号内容视为动作气泡，不送入 TTS。</summary>
    private static bool IsActionSegment(string text)
    {
        var value = text.Trim();
        return value.Length >= 2 &&
               ((value.StartsWith('（') && value.EndsWith('）')) ||
                (value.StartsWith('(') && value.EndsWith(')')));
    }

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
        catch
        {
            // 播放失败不影响气泡显示
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
            var isAction = IsActionSegment(text);
            var audio = isAction
                ? null
                : await _agent.SynthesizeAsync(text, "concerned", 0.65, ct);
            await Dispatcher.InvokeAsync(() =>
            {
                AddBubble(text, isUser: false, audio?.AudioPath);
            });

            if (audio is not null && File.Exists(audio.AudioPath))
                await PlayAudioAsync(audio.AudioPath, ct);
        }
        catch
        {
            // 提醒失败不影响主流程
        }
        finally
        {
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
                Content = "▶ 重播",
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
        _store.Save(_entries);
    }

    // ── UI 事件 ──
    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Awareness_Click(object sender, RoutedEventArgs e)
    {
        _allowAppAwareness = !_allowAppAwareness;
        UpdateAwarenessButton();
        var message = _allowAppAwareness
            ? "（已允许桌宠查看当前前台应用的进程名；不会读取标题或内容）"
            : "（已关闭应用状态感知）";
        AddBubble(message, isUser: false, audio: null, record: false);
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
        var message = CurrentCharacter.Id == "hutao"
            ? "聊天记录清空啦，重新聊点什么吧~"
            : "旧剧本已经收好，下一幕重新开始！";
        AddBubble(message, isUser: false);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
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

        foreach (var button in new[] { CloseButton, ClearButton, SwitchButton, AwarenessButton })
            button.Foreground = new SolidColorBrush(theme.AccentDark);
        UpdateAwarenessButton();
    }

    private void UpdateAwarenessButton()
    {
        AwarenessButton.Content = _allowAppAwareness ? "◉" : "○";
        AwarenessButton.ToolTip = _allowAppAwareness
            ? "应用状态感知已开启：只读取当前前台应用进程名；点击关闭"
            : "应用状态感知已关闭；点击授权最小范围感知";
    }

    // ── 装配辅助（与 Host 同源）──
    private static ILLMProvider BuildLlm(PersonaProfile persona)
    {
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
            return new DeepSeekLlmProvider(key);
        return new MockLlmProvider(persona);
    }

    private ITtsEngine? BuildTts()
    {
        var python = Environment.GetEnvironmentVariable("HU_TAO_TTS_PYTHON")
                     ?? Path.Combine(_repoRoot!, "voice", ".venv", "Scripts", "python.exe");
        if (!File.Exists(python))
            return null;

        var serverScript = Path.Combine(_repoRoot!, "voice", "infer", "resident_server.py");
        var gptModel = Path.Combine(_repoRoot!, "voice", "GPT-SoVITS-main", "GPT_SoVITS", "pretrained_models", "s1v3.ckpt");
        var sovitsModel = Path.Combine(_repoRoot!, "voice", "GPT-SoVITS-main", "GPT_SoVITS", "pretrained_models", "s2Gv3.pth");
        var outputDir = Path.Combine(_repoRoot!, "data", "voice", "hutao");

        var onDemand = new GptSovitsTtsEngine(
            python,
            Path.Combine(_repoRoot!, "voice", "infer", "few_shot_infer.py"),
            gptModel,
            sovitsModel,
            outputDir);

        var mode = Environment.GetEnvironmentVariable("HU_TAO_TTS_MODE")
            ?.Trim().ToLowerInvariant();
        if (mode is "on-demand" or "ondemand" or "process")
            return onDemand;

        var url = Environment.GetEnvironmentVariable("HU_TAO_TTS_URL")
                   ?? "http://127.0.0.1:9881/";
        var resident = new ResidentGptSovitsTtsEngine(
            new Uri(url), outputDir, python, serverScript, gptModel, sovitsModel,
            device: Environment.GetEnvironmentVariable("HU_TAO_TTS_DEVICE") ?? "cuda",
            half: !string.Equals(
                Environment.GetEnvironmentVariable("HU_TAO_TTS_HALF"), "false",
                StringComparison.OrdinalIgnoreCase));

        // 后台预启动：桌宠界面先显示，模型加载完成后首句即可直接推理。
        resident.StartInBackground();
        return new FallbackTtsEngine(resident, onDemand);
    }

    private static void LoadEnvFile()
    {
        foreach (var candidate in new[] { ".env", "../.env", "../../../../.env", "../../../../../.env" })
        {
            if (!File.Exists(candidate))
                continue;
            foreach (var line in File.ReadAllLines(candidate))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#') || !t.Contains('='))
                    continue;
                var kv = t.Split('=', 2);
                if (Environment.GetEnvironmentVariable(kv[0].Trim()) is null && kv[1].Trim().Length > 0)
                    Environment.SetEnvironmentVariable(kv[0].Trim(), kv[1].Trim());
            }
            return;
        }
    }

    private static bool ReadBooleanEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return value is not null &&
               (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindRepoRoot()
    {
        // 从「工作目录」和「exe 所在目录」两个起点向上回溯，定位项目根。
        // 锚点用 voice/ + agent/ 目录（稳定存在，不像 CORE.md 可能被移到 notes/）。
        var starts = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "voice")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "agent")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }
}
