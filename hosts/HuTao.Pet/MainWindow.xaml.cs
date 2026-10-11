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

/// <summary>
/// 一个可切换的角色主题。**public** 是因为聊天客户端的消息气泡工厂要按它着色
/// （<c>ChatShell.MessageVisualFactory.For</c> 的参数），而不一致的可访问性会直接编译失败。
/// </summary>
public sealed record CharacterTheme(
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

    /// <summary>聊天客户端注入的共享运行时（为 null 时自己建一套）。</summary>
    private readonly AgentRuntimeFactory? _injectedFactory;
    private readonly ITtsEngine? _injectedTts;
    private AgentRuntime? _runtime;
    private bool _closingRuntime;
    private bool _runtimeClosed;
    private Task _initialization=Task.CompletedTask;
    private Task _characterSwitchTask=Task.CompletedTask;
    private Task[] _backgroundTasks=[];
    private Task _userTurn=Task.CompletedTask;
    private Task _documentTask=Task.CompletedTask;
    private readonly PendingRuntimeCleanup _pendingRuntimeCleanup=new();
    private ChatLogStore _store = null!;
    /// <summary>
    /// 当前角色的情境台词服务（兜底 / 提醒 / 冷落都说人话，且必须是**她本人**的话）。
    /// 以前这些台词写死在 <c>CurrentCharacter.Id switch</c> 里，而 default 分支是胡桃的口吻，
    /// 于是安魂曲会说"本堂主"。现在一律问它，缺了会中性兜底并后台补生成。
    /// </summary>
    private HuTao.Persona.ScriptedLines _scripted = HuTao.Persona.ScriptedLines.Empty;
    private readonly List<ChatEntry> _entries = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _audioPlaybackGate = new(1, 1);
    private ConversationPrivacyStamp? _chatPrivacy;
    private readonly System.Windows.Threading.DispatcherTimer _privacyTimer=new() {Interval=TimeSpan.FromSeconds(1)};
    private SoundPlayer? _activePlayer;
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

    public MainWindow():this(null,null) { }

    internal MainWindow(Func<Task>? initialize,Action? savePreferences)
        : this(initialize, savePreferences, null, null) { }

    /// <summary>
    /// <paramref name="sharedFactory"/>/<paramref name="sharedTts"/> 是聊天客户端传进来的
    /// 同一套运行时。**必须复用**：进程里只应有一个 TTS 引擎（它内部有参考音频缓存，
    /// 多开会互相踩），LLM 与 AgentPool 同理。为 null 时（<c>--pet</c> 独立启动）
    /// 桌宠自己建一套。
    /// </summary>
    internal MainWindow(
        Func<Task>? initialize,
        Action? savePreferences,
        AgentRuntimeFactory? sharedFactory,
        ITtsEngine? sharedTts)
    {
        InitializeComponent();
        _injectedFactory = sharedFactory;
        _injectedTts = sharedTts;
        _privacyTimer.Tick+=(_,_)=>SynchronizeChatPrivacy();
        _privacyTimer.Start();
        if(initialize is null)
        {
            InitializeWindowPresentation();
            InitializeTrayIcon();
        }
        Loaded += async (_, _) =>
        {
            if(_cts.IsCancellationRequested)return;
            _initialization=initialize is null?InitAsync():initialize();
            try {await _initialization;}
            catch(OperationCanceledException) when(_cts.IsCancellationRequested) { }
            catch(Exception ex)
            {
                LocalDiagnosticLog.Default.Write("runtime.initialize",ex);
                SwitchButton.ToolTip="初始化未完成，请查看本机诊断日志。可以关闭窗口后重试。";
            }
        };
        Closing += async (_, args) =>
        {
            if(_runtimeClosed)return;
            args.Cancel=true;
            if(_closingRuntime)return;
            _closingRuntime=true;
            _cts.Cancel();
            _privacyTimer.Stop();_activePlayer?.Stop();
            try
            {
                if(_chatRoom is not null && !await _chatRoom.RequestCloseAsync())return;
                try {await _initialization;}catch(OperationCanceledException){}
                catch(Exception ex){LocalDiagnosticLog.Default.Write("knowledge.initialization_shutdown",ex);}
                try {await _characterSwitchTask;}
                catch(Exception ex){LocalDiagnosticLog.Default.Write("knowledge.switch_shutdown",ex);}
                try {await Task.WhenAll(_backgroundTasks);}catch(OperationCanceledException) when(_cts.IsCancellationRequested){}
                catch(Exception ex){LocalDiagnosticLog.Default.Write("knowledge.background_shutdown",ex);}
                try {await Task.WhenAll(_userTurn,_documentTask);}catch(OperationCanceledException) when(_cts.IsCancellationRequested){}
                catch(Exception ex){LocalDiagnosticLog.Default.Write("knowledge.user_tasks_shutdown",ex);}
                await _audioPlaybackGate.WaitAsync();
                _audioPlaybackGate.Release();
                if(_runtime is not null)await _runtime.DisposeAsync();
                await _pendingRuntimeCleanup.RetryAsync();
                if(_tts is IAsyncDisposable disposable)await disposable.DisposeAsync();
                if(savePreferences is null)SaveWindowPreferences();else savePreferences();
                DisposeTrayIcon();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                _runtimeClosed=true;Close();
            }
            catch(Exception ex)
            {
                LocalDiagnosticLog.Default.Write("knowledge.shutdown",ex);
                SwitchButton.ToolTip="保存或退出清理尚未完成，请排除文件占用或磁盘问题后再次关闭。";
            }
            finally {_closingRuntime=false;}
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
        // 聊天客户端注入了共享运行时就直接用；否则（--pet 独立启动）自己建一套。
        // 复用的意义不只是省内存：TTS 引擎内部有参考音频缓存，开两份会互相踩。
        _runtimeFactory = _injectedFactory ?? new AgentRuntimeFactory(
            _repoRoot,
            _ => TtsRuntimeFactory.CreateGptSovits(
                _repoRoot,
                preferResident: true,
                log: Console.WriteLine),
            Console.WriteLine,
            approveTool: ConfirmToolAsync);
        if (_injectedTts is not null)
            _tts = _injectedTts;
        // 前台感知默认开启（桌宠要陪伴就得知道你在忙什么）；显式设成 false/0/no 才关闭。
        _allowAppAwareness = ReadBooleanEnvironment("HU_TAO_ALLOW_APP_AWARENESS", defaultValue: true);
        UpdateAwarenessButton();
        await LoadCharacterAsync(0);

        // 主动调度器（只启动一次）
        _scheduler = new ProactiveScheduler(
            intervalSeconds: 600, cooldownSeconds: 1200, minIdleSeconds: 300);
        // 调度回调回到 UI 线程，与 Send/角色切换共享同一个说话门，避免 bool 跨线程竞态。
        var proactiveTask = _scheduler.RunLoopAsync(ct => Dispatcher.InvokeAsync(async () =>
        {
            if (!_talking && !_characterSwitchLoopRunning && _pendingCharacterIndex is null &&
                _scheduler.ShouldTrigger())
                await SayAsync(proactive: true, userInput: null, ct);
        }).Task.Unwrap(), _cts.Token);

        // 休息提醒 + 冷落别扭（只启动一次）
        _backgroundTasks=[proactiveTask,RestReminderLoopAsync(_cts.Token),IgnoreLoopAsync(_cts.Token)];
    }

    private async Task LoadCharacterAsync(int index)
    {
        AgentRuntime? candidate=null;
        try
        {
            await _pendingRuntimeCleanup.RetryAsync();
            _cts.Token.ThrowIfCancellationRequested();
            var ch = Characters[index];
            var runtime = (_runtimeFactory ?? throw new InvalidOperationException(
                    "Agent 运行时工厂尚未初始化。"))
                .Create(
                    ch.Definition,
                    sharedTts: _tts,
                    allowAppAwareness: () => _allowAppAwareness,
                    documentProgress: ReportDocumentProgress);
            candidate=runtime;
            var nextPrivacy=runtime.Agent.PrivacyStamp;
            var nextStore=new ChatLogStore(
                Path.Combine(_repoRoot!, "data", $"chat_log_{ch.Id}.json"),
                audioSizeLimitBytes:512L*1024*1024,
                privacy:()=>runtime.Agent.PrivacyStamp,generatedAudioRoot:Path.Combine(_repoRoot!,"data","voice","generated"));
            var nextEntries=nextStore.Load();
            runtime.Agent.RestoreHistory(nextEntries.Where(e=>!SpeechDeliverySession.IsLegacyFailureBubble(e.Role,e.Text))
                .TakeLast(20).Select(e=>new ChatMessage(e.Role,e.Text)),nextPrivacy);
            _cts.Token.ThrowIfCancellationRequested();
            var previousRuntime=_runtime;
            if(previousRuntime is not null)await previousRuntime.DisposeAsync();
            _runtime=runtime;
            candidate=null;
            _currentIndex=index;
            // 即使某个角色没有声线，也保留已经启动的共享引擎，切回其他角色时可继续复用。
            _tts ??= runtime.Tts;
            _documentReader = runtime.DocumentReader;
            _agent = runtime.Agent;
            _scripted = runtime.Persona.Scripted;
            _chatPrivacy=_agent.PrivacyStamp;
            _memoryMaintenance = runtime.Memory;
            _store = nextStore;
            _entries.Clear();
            _entries.AddRange(nextEntries);
            ApplyTheme(ch);

            Title = $"{ch.Name}桌宠";
            TitleText.Text = $"{ch.Emoji} {ch.Name}";
            TitleText.ToolTip = $"{ch.Name} · {ch.Title}";

            // 每个角色独立的记忆文件，避免记忆串味

            BubblePanel.Children.Clear();
            foreach (var e in _entries)
                if (!SpeechDeliverySession.IsLegacyFailureBubble(e.Role, e.Text))
                    AddBubble(e.Text, e.Role == "user", e.Audio, record: false);

            // 原始历史文件不删除；旧版程序的诊断气泡不再显示，也不注入角色上下文。

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
        finally
        {
            if(candidate is not null)
            {
                try {await _pendingRuntimeCleanup.RetireAsync(candidate);}
                catch(Exception ex){LocalDiagnosticLog.Default.Write("character.candidate_cleanup",ex);}
            }
        }

    }

    /// <summary>一次说话：先「正在忙」占位，再按顺序显示气泡并播放对应语音。</summary>
    private async Task SayAsync(bool proactive, string? userInput, CancellationToken ct)
    {
        if (_agent is null || _talking || _cts.IsCancellationRequested)
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
                segment.DemandDelivery();
                var seg = segment.Text;
                var audioPrivacy=_chatPrivacy;
                var audioPath = await speech.PrepareAudioAsync(segment,
                    async token =>
                    {
                        var result=await _agent.SynthesizeAsync(seg,segment.Emotion,segment.Intensity,token);
                        try {segment.DemandDelivery();return result;}
                        catch(OperationCanceledException)
                        {if(result is not null)_store.DiscardGeneratedAudio(result.AudioPath,audioPrivacy);throw;}
                    }, ct);

                await Dispatcher.InvokeAsync(() =>
                {
                    segment.DemandDelivery();
                    if (thinking is not null && BubblePanel.Children.Contains(thinking))
                    {
                        BubblePanel.Children.Remove(thinking);
                        thinking = null;
                    }
                    AddBubble(seg, isUser: false, audioPath,ownsGeneratedAudio:segment.OriginalAudioPath is null);
                    displayed = true;
                });

                // 等本段实际播放完再继续下一段，避免连续气泡的语音重叠。
                if (audioPath is not null && File.Exists(audioPath))
                {
                    segment.DemandDelivery();
                    await PlayAudioAsync(audioPath, ct,segment.DemandDelivery);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("conversation.delivery", ex);
            await Dispatcher.InvokeAsync(() =>
            {
                // 未获得文字的错误才使用角色兜底；TTS 错误已在段落级吞下，不走这里。
                // 台词来自人设数据，不再按角色 id 写死（否则新角色会说胡桃的话）。
                if (!displayed && !proactive)
                    AddBubble(_scripted.Get(HuTao.Persona.ScriptedLines.Situation.Distracted),
                        isUser: false, record: false);
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
    private async Task PlayAudioAsync(string path, CancellationToken ct = default,Action? demandDelivery=null)
    {
        if (_cts.IsCancellationRequested || !File.Exists(path))
            return;

        await _audioPlaybackGate.WaitAsync(ct);
        try
        {
            if(_cts.IsCancellationRequested)return;
            demandDelivery?.Invoke();
            await Task.Run(() =>
            {
                using var player = new SoundPlayer(path);
                _activePlayer=player;
                try {demandDelivery?.Invoke();player.PlaySync();}
                finally {_activePlayer=null;}
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
                    await SayReminderAsync(
                        _scripted.Get(HuTao.Persona.ScriptedLines.Situation.RestReminder), ct);
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

    /// <summary>
    /// 冷落别扭。四档语气**不再按角色 id 写死**：曾经是
    /// <c>if (characterId == "furina") … if (characterId == "klee") … else 胡桃</c>，
    /// 于是没列到的角色（安魂曲）一律说胡桃的话。现在只挑"哪一档"，
    /// 具体说什么由该角色的 <c>fallbacks.json</c> 决定，缺了会中性兜底并后台补生成。
    /// </summary>
    private string IgnoreMessage(double silentMin)
    {
        var situation = silentMin switch
        {
            < 20 => HuTao.Persona.ScriptedLines.Situation.IgnoredMild,
            < 35 => HuTao.Persona.ScriptedLines.Situation.IgnoredModerate,
            < 50 => HuTao.Persona.ScriptedLines.Situation.IgnoredLong,
            _ => HuTao.Persona.ScriptedLines.Situation.IgnoredVeryLong,
        };
        return _scripted.Get(situation);
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
    private void AddBubble(string text, bool isUser, string? audio = null, bool record = true,bool ownsGeneratedAudio=false)
    {
        if(record)SynchronizeChatPrivacy();
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
            var privacy=_chatPrivacy;
            btn.Click += async (_, _) =>
            {
                SynchronizeChatPrivacy();
                if(privacy!=_agent?.PrivacyStamp)return;
                await PlayAudioAsync(path,demandDelivery:()=>
                {if(privacy!=_agent?.PrivacyStamp)throw new OperationCanceledException("Conversation privacy changed.");});
            };
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
                isUser ? "user" : "assistant", text, audio) {Privacy=_chatPrivacy,OwnsGeneratedAudio=ownsGeneratedAudio});
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

    private void SynchronizeChatPrivacy()
    {
        if(_agent is null || _store is null)return;
        try
        {
            var current=_agent.PrivacyStamp;if(current==_chatPrivacy)return;
            _activePlayer?.Stop();
            _entries.Clear();BubblePanel.Children.Clear();UpdateCompactBubble("");
            _entries.AddRange(_store.Load());_chatPrivacy=current;
            foreach(var entry in _entries)AddBubble(entry.Text,entry.Role=="user",entry.Audio,record:false);
            _agent.RestoreHistory(_entries.TakeLast(20).Select(e=>new ChatMessage(e.Role,e.Text)),_chatPrivacy);
        }
        catch(Exception ex) {LocalDiagnosticLog.Default.Write("chat.privacy",ex);}
    }

    // ── UI 事件 ──
    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Awareness_Click(object sender, RoutedEventArgs e) => ToggleAwareness();

    /// <summary>
    /// 切换应用状态感知。**抽成无参方法**，好让聊天窗侧边栏也能走同一条路径
    /// （见 <see cref="RailToggleAwareness"/>）——两边各写一份迟早会不一致。
    /// </summary>
    private void ToggleAwareness()
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
        _ = HideStatusLaterAsync(epoch);
    }

    private async Task HideStatusLaterAsync(int epoch)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6),_cts.Token);
            if(!_cts.IsCancellationRequested && epoch==_statusEpoch)
                StatusText.Visibility=Visibility.Collapsed;
        }
        catch(OperationCanceledException) when(_cts.IsCancellationRequested) { }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Send();
    }

    private void Send()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0 || _agent is null || _talking || _cts.IsCancellationRequested)
            return;
        AddBubble(text, isUser: true);
        _scheduler?.NotifyConversationActivity();
        InputBox.Clear();
        _lastUserReply = DateTime.UtcNow; // 用户回复了，重置冷落计时
        _userTurn = SayAsync(proactive: false, userInput: text, _cts.Token);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearConversation();

    /// <summary>清空当前角色的聊天记录。抽成无参方法供侧边栏复用。</summary>
    private void ClearConversation()
    {
        if(_cts.IsCancellationRequested || _store is null)return;
        _store.Clear();
        _entries.Clear();
        _agent?.RestoreHistory([]);
        BubblePanel.Children.Clear();
        // 台词来自人设数据：以前这里按角色 id 选，只有胡桃和可莉有分支，
        // 芙宁娜与安魂曲会走到 `_ =>` 那个中性句。现在统一走 ScriptedLines。
        AddBubble(_scripted.Get(HuTao.Persona.ScriptedLines.Situation.HistoryCleared),
            isUser: false);
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
