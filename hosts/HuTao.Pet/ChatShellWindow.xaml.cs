using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HuTao.Dialogue.Runtime;
using HuTao.Dialogue.Sessions;
using HuTao.Dialogue.Storage;
using HuTao.Foundation;
using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;
using HuTao.Persona;
using HuTao.Pet.ChatShell;
using HuTao.Voice;

namespace HuTao.Pet;

/// <summary>
/// QQNT 形态的聊天主窗：左侧会话列表（单聊与群聊平级）、右侧消息区与输入区。
///
/// 分工：**调度、存储、上下文切换全在 Core 的 <c>Sessions/</c> 里**，
/// 这个窗口只做三件事——把会话画成侧栏一行、把每一轮画成气泡、把语音播出来。
/// 与旧聊天室窗口同一套取舍，只是"一个房间"变成了"一张会话表"。
///
/// 生命周期成员（<c>_startup</c> / <c>_activeTurn</c> / <c>_audioGate</c> / <c>_pendingCleanup</c>）
/// 是刻意保留的：既有 WPF 生命周期回归用反射盯着这四个，名字对上，那些回归就能继续守着新窗口
/// （它们原先盯的是已退休的 `ChatRoomWindow`）。
/// </summary>
public partial class ChatShellWindow : Window, IChatSurface
{
    private readonly AgentRuntimeFactory? _factory;
    private readonly ITtsEngine? _sharedTts;
    private readonly string? _repositoryRoot;
    // 非 readonly：LoadSidebarOnlyAsync 会在"未装配"的窗口上补建它（离线 UI 冒烟用）。
    private ChatShellViewModel? _viewModel;

    /// <summary>建视图模型并把"回到 UI 线程"的通道接上（运行时会在线程池上通知）。</summary>
    private ChatShellViewModel CreateViewModel()
    {
        var viewModel = new ChatShellViewModel(new SessionStore(_repositoryRoot!), new SessionCatalog([]))
        {
            // 处理器本身是同步的，所以这里只调度、不等待——避免在工作线程上等 UI 线程。
            Dispatch = action => Dispatcher.InvokeAsync(action),
        };
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        // 消息列表一有增删就重绘。
        //
        // 为什么必须订阅：乐观追加是**直接往集合里插一条**，不会有属性变化通知，
        // 也没人调用 RebuildMessages。结果就是"用户自己那句话插进去了但屏幕上看不到"，
        // 而"正在输入"占位是直接改界面元素、立刻就出现——于是观感变成
        // "先显示正在输入，用户的话要等角色说完才出来"，正好是反的。
        ((System.Collections.Specialized.INotifyCollectionChanged)viewModel.ActiveTurns)
            .CollectionChanged += (_, _) => RebuildMessages();
        viewModel.UseRuntimeFactory((descriptor, ct) => Task.FromResult(CreateRuntime(descriptor)));
        return viewModel;
    }
    private readonly MessageVisualFactory _messageFactory;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private readonly PendingRuntimeCleanup _pendingCleanup = new();
    private readonly List<AgentPool> _pools = [];
    private readonly string _preferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HuTaoCompanion", "chat-shell.json");

    // 反射测试（tests/HuTao.Pet.LifecycleChecks）盯着的四个成员。
    private Task _startup = Task.CompletedTask;
    private Task _activeTurn = Task.CompletedTask;

    private TaskCompletionSource<bool>? _closeRequest;
    private bool _closing;
    private bool _closeReady;
    private string? _contextSessionId;
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private bool _isSwitching;
    private string _statusEpochText = "";

    /// <param name="factory">Agent 运行时工厂。为 null 表示"只开窗不装配"（生命周期检查就是这么用的）。</param>
    /// <param name="sharedTts">共享 TTS 引擎；常驻服务只该有一个实例。</param>
    /// <param name="repositoryRoot">仓库根。为 null 时自动定位。</param>
    public ChatShellWindow(
        AgentRuntimeFactory? factory,
        ITtsEngine? sharedTts,
        string? repositoryRoot = null,
        string? preferredCharacterId = null)
    {
        InitializeComponent();
        _factory = factory;
        _sharedTts = sharedTts;
        _repositoryRoot = repositoryRoot ?? LocateRepositoryRoot();
        _messageFactory = new MessageVisualFactory(PlayAudio);

        LoadWindowPreferences();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += (_, _) =>
        {
            // 侧栏是虚拟化的：容器实体化/回收时补画头像与角标。
            SessionList.ItemContainerGenerator.StatusChanged += SessionList_StatusChanged;
            // 尺寸/坐标读回来之后再夹一次，保证窗口一定看得见。
            KeepWindowOnScreen();
            RenderSessions();
            InitializeRail();
        };
        Closed += (_, _) => SessionList.ItemContainerGenerator.StatusChanged -= SessionList_StatusChanged;

        if (factory is null || _repositoryRoot is null)
        {
            // 未装配：窗口仍然可用（能拖、能缩、能关），只是没有会话。
            SetStatus("聊天客户端未装配：缺少 Agent 运行时工厂。");
            return;
        }

        _viewModel = CreateViewModel();
        SessionList.ItemsSource = _viewModel.Sessions;
        _startup = InitializeAsync(preferredCharacterId);
    }

    /// <summary>
    /// 只加载会话目录（不激活任何会话）。给离线 UI 冒烟检查用：它要验证
    /// 侧栏真的渲染出了会话行，而完整装配会把 TTS 引擎与网络拖进来。
    /// </summary>
    internal Task LoadSidebarOnlyAsync(
        Func<SessionDescriptor, CancellationToken, Task<ChatSessionRuntime>>? runtimeFactory = null)
    {
        if (_viewModel is not null || _repositoryRoot is null)
            return _startup;
        _viewModel = CreateViewModel();
        if (runtimeFactory is not null)
            _viewModel.UseRuntimeFactory(runtimeFactory);
        SessionList.ItemsSource = _viewModel.Sessions;
        _startup = _viewModel.InitializeAsync(_repositoryRoot, CharacterCatalog.All,
            CharacterThemeCatalog.AccentFor);
        return _startup;
    }

    internal ListBox SessionListForCheck => SessionList;

    internal double SidebarWidthForCheck => SidebarColumn.Width.Value;

    /// <summary>
    /// 无头检查用的窗口标记。置位后**不写窗口位置偏好**——检查把窗口放在
    /// (-30000,-30000) 以避免打扰用户，那个坐标一旦被存成"用户偏好"，
    /// 下一次真实启动就会开在屏幕外（表现为点了 💬 什么都没发生）。
    /// 这是本项目的自动化检查曾经污染过真实配置的地方。
    /// </summary>
    internal bool IsTestWindow { get; set; }

    public Task<bool> RequestCloseAsync()
    {
        if (_closeReady)
            return Task.FromResult(true);
        _closeRequest ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = _closeRequest.Task;
        Close();
        return task;
    }

    /// <summary>已打开时把窗口带到前面，而不是再开一间（同时只能有一份 Agent 活着）。</summary>
    public void BringToFront()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// 仓库根。显式传入 &gt; <c>HU_TAO_REPO_ROOT</c> 环境变量 &gt; 自动定位。
    /// 环境变量这一档是给**离线 UI 冒烟检查**用的：它要在一份临时仓库上把窗口真的渲染出来，
    /// 而自动定位只会找到真实仓库、顺带把 TTS 引擎和网络拖进来。
    /// </summary>
    private static string? LocateRepositoryRoot()
    {
        var configured = Environment.GetEnvironmentVariable("HU_TAO_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);
        return ProjectEnvironment.FindRepositoryRoot(Environment.CurrentDirectory, AppContext.BaseDirectory);
    }

    // ── 启动 ─────────────────────────────────────────────────────────────

    private async Task InitializeAsync(string? preferredCharacterId)
    {
        try
        {
            ProjectEnvironment.LoadDotEnv(log: Console.WriteLine);
            await _viewModel!.InitializeAsync(
                _repositoryRoot!,
                CharacterCatalog.All,
                CharacterThemeCatalog.AccentFor,
                preferredCharacterId).ConfigureAwait(true);
            RenderSessions();
            SetStatus(_viewModel.Sessions.Count == 0
                ? "还没有会话。点「＋ 发起群聊」建一个。"
                : "");
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("chat_shell.startup", ex);
            SetStatus($"聊天客户端初始化失败：{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 为某个会话装配运行时。每个会话一个 <see cref="AgentPool"/> 是**刻意的**：
    /// 池子的不变量是"一个角色一个实例"，跨会话共享同一个池会让两条会话同时
    /// 需要同一角色时无法互斥（而那正是要避免的"两份 runtime 抢同一个记忆文件"）。
    /// </summary>
    private ChatSessionRuntime CreateRuntime(SessionDescriptor descriptor)
    {
        var pool = new AgentPool(_factory!, _sharedTts);
        _pools.Add(pool);
        return new ChatSessionRuntime(
            new SessionStore(_repositoryRoot!),
            pool,
            descriptor,
            new SessionRuntimeOptions
            {
                Director = ChatSessionRuntime.CreateDefaultDirector(
                    PersonaLoaderFallback(descriptor)),
            });
    }

    private static PersonaProfile PersonaLoaderFallback(SessionDescriptor descriptor)
    {
        var id = descriptor.MemberIds.FirstOrDefault() ?? CharacterCatalog.Default.Id;
        var definition = CharacterCatalog.Find(id) ?? CharacterCatalog.Default;
        try
        {
            var root = ProjectEnvironment.FindRepositoryRoot(
                Environment.CurrentDirectory, AppContext.BaseDirectory);
            if (root is not null)
                return PersonaLoader.Load(Path.Combine(root, definition.PersonaDirectory));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            LocalDiagnosticLog.Default.Write("chat_shell.persona", ex);
        }
        return new PersonaProfile
        {
            Root = Path.GetTempPath(), Name = definition.Name,
            SystemPrompt = $"你是{definition.Name}", Catchphrases = new(), Lore = "", Quotes = [],
        };
    }

}
