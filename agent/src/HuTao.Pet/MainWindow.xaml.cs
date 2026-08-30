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
using HuTao.Agent.Core.Storage;
using HuTao.Agent.Core.Tools;
using HuTao.Agent.Core.Tts;

namespace HuTao.Pet;

public partial class MainWindow : Window
{
    private ReactAgent? _agent;
    private ProactiveScheduler? _scheduler;
    private ChatLogStore _store = null!;
    private readonly List<ChatEntry> _entries = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _random = new();
    private string? _repoRoot;
    private bool _talking;
    private DateTime _lastUserReply = DateTime.UtcNow;
    private double _nextIgnoreMin = 10;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) => _cts.Cancel();
    }

    private async Task InitAsync()
    {
        try
        {
            LoadEnvFile();
            _repoRoot = FindRepoRoot() ?? AppContext.BaseDirectory;
            var persona = PersonaLoader.Load(Path.Combine(_repoRoot, "data", "persona"));

            IAgentTool[] tools = [new TimeTool(), new ActiveWindowTool(), new IdleTool()];
            var llm = BuildLlm(persona);
            var tts = BuildTts();

            _agent = new ReactAgent(
                persona, llm, tts, tools,
                refAudio: Path.Combine(_repoRoot, "data", "voice", "hutao", "wav", "a4eedbf833d51d47.wav"),
                refText: "哼哼，切勿质疑我的业务能力！");

            // 记忆区：加载历史聊天记录（语音可重播）
            _store = new ChatLogStore(
                Path.Combine(_repoRoot, "data", "chat_log.json"),
                audioSizeLimitBytes: 512L * 1024 * 1024);
            var history = _store.Load();
            _entries.AddRange(history);

            // 重放历史气泡（不重复记录）
            foreach (var e in _entries)
                AddBubble(e.Text, e.Role == "user", e.Audio, record: false);

            // 恢复 LLM 记忆：只喂最近 20 条，避免 token 爆炸
            var recent = _entries.TakeLast(20).Select(e => new ChatMessage(e.Role, e.Text));
            _agent.RestoreHistory(recent);

            if (_entries.Count == 0)
                AddBubble("本堂主来啦！有什么想聊的，尽管说~", isUser: false);

            // 主动调度器
            _scheduler = new ProactiveScheduler(
                intervalSeconds: 60, cooldownSeconds: 120, minIdleSeconds: 30);
            _ = _scheduler.RunLoopAsync(ct => SayAsync(proactive: true, userInput: null, ct), _cts.Token);

            // 休息提醒 + 冷落别扭
            _ = RestReminderLoopAsync(_cts.Token);
            _ = IgnoreLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            AddBubble($"初始化出错了：{ex.Message}", isUser: false);
        }
    }

    /// <summary>一次说话：先「正在忙」占位，然后按多段短句逐条出（文字+语音同出）。</summary>
    private async Task SayAsync(bool proactive, string? userInput, CancellationToken ct)
    {
        if (_agent is null || _talking)
            return;
        _talking = true;

        Border? thinking = null;
        Dispatcher.Invoke(() => thinking = AddThinkingBubble());

        try
        {
            var segments = await _agent.GenerateSegmentsAsync(userInput, proactive, ct);
            if (segments.Count == 0)
                segments = new[] { "……" };

            var first = true;
            foreach (var seg in segments)
            {
                ct.ThrowIfCancellationRequested();
                var audio = await _agent.SynthesizeAsync(seg, ct);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (thinking is not null && BubblePanel.Children.Contains(thinking))
                    {
                        BubblePanel.Children.Remove(thinking);
                        thinking = null;
                    }
                    AddBubble(seg, isUser: false, audio?.AudioPath);
                    if (audio is not null && File.Exists(audio.AudioPath))
                        PlayAudio(audio.AudioPath);
                });

                if (!first)
                    await Task.Delay(TimeSpan.FromSeconds(1), ct); // 连续气泡间的小停顿，像真人连续发消息
                first = false;
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

    /// <summary>灰色斜体的「正在忙」占位气泡，返回引用供后续移除。</summary>
    private Border AddThinkingBubble()
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(240, 238, 234)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(4, 3, 4, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 270,
        };
        border.Child = new TextBlock
        {
            Text = "胡桃正在忙，可能还没有看到消息哦~",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(Color.FromRgb(158, 138, 118)),
        };
        BubblePanel.Children.Add(border);
        BubbleScroll.ScrollToEnd();
        return border;
    }

    private static void PlayAudio(string path)
    {
        try
        {
            new SoundPlayer(path).Play(); // 异步播放
        }
        catch
        {
            // 播放失败不影响气泡显示
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
                var msg = IgnoreMessage(silentMin);
                await SayReminderAsync(msg, ct);

                // 重新计时；下次阈值不固定（10~15 分钟随机）
                _lastUserReply = DateTime.UtcNow;
                _nextIgnoreMin = 10 + _random.Next(0, 6);
            }
        }
    }

    private static string IgnoreMessage(double silentMin)
    {
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
            var audio = await _agent.SynthesizeAsync(text, ct);
            await Dispatcher.InvokeAsync(() =>
            {
                AddBubble(text, isUser: false, audio?.AudioPath);
                if (audio is not null && File.Exists(audio.AudioPath))
                    PlayAudio(audio.AudioPath);
            });
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

    /// <summary>加一条气泡（胡桃的带「重播」按钮），并写入记忆区。</summary>
    private void AddBubble(string text, bool isUser, string? audio = null, bool record = true)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 20,
            Foreground = isUser ? Brushes.White : new SolidColorBrush(Color.FromRgb(70, 45, 20)),
        });

        // 胡桃的语音气泡：加 galgame 式「重播」按钮
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
                Background = new SolidColorBrush(Color.FromRgb(240, 224, 200)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = path,
            };
            btn.Click += (_, _) => PlayAudio(path);
            panel.Children.Add(btn);
        }

        var border = new Border
        {
            Background = isUser
                ? new SolidColorBrush(Color.FromRgb(224, 138, 60))
                : new SolidColorBrush(Color.FromRgb(255, 255, 255)),
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

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Send();
    }

    private void Send()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0 || _agent is null)
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
        AddBubble("记录已清空~ 本堂主可都忘光啦，嘿嘿。", isUser: false);
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

        return new GptSovitsTtsEngine(
            python,
            Path.Combine(_repoRoot!, "voice", "infer", "few_shot_infer.py"),
            Path.Combine(_repoRoot!, "voice", "GPT-SoVITS-main", "GPT_SoVITS", "pretrained_models", "s1v3.ckpt"),
            Path.Combine(_repoRoot!, "voice", "GPT-SoVITS-main", "GPT_SoVITS", "pretrained_models", "s2Gv3.pth"),
            Path.Combine(_repoRoot!, "data", "voice", "hutao"));
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

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CORE.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "voice")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
