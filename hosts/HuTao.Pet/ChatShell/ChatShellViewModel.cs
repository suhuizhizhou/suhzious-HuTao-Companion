using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HuTao.Dialogue.Sessions;

namespace HuTao.Pet.ChatShell;

/// <summary>侧栏一行。它是 <see cref="SessionDescriptor"/> 的展示投影，不持有任何 Core 状态。</summary>
public sealed class SessionItemViewModel : INotifyPropertyChanged
{
    private SessionDescriptor _descriptor;

    public SessionItemViewModel(SessionDescriptor descriptor) => _descriptor = descriptor;

    public SessionDescriptor Descriptor => _descriptor;

    public string Id => _descriptor.Id;

    public string Title => _descriptor.Title;

    public string Preview => string.IsNullOrWhiteSpace(_descriptor.LastPreview)
        ? "还没有消息"
        : _descriptor.LastPreview;

    public string AccentArgb => _descriptor.AccentArgb;

    public string Emoji => _descriptor.Emoji;

    public bool IsGroup => _descriptor.Kind == SessionKind.Group;

    public int UnreadCount => _descriptor.UnreadCount;

    public bool Muted => _descriptor.Muted;

    public bool Pinned => _descriptor.Pinned;

    /// <summary>
    /// QQ 的时间写法：今天给时刻、昨天给"昨天"、本周给星期、更早给日期。
    /// 只给"12:03"这种粒度是因为侧栏是扫一眼的地方，精确到秒反而更慢读。
    /// </summary>
    public string TimeLabel => FormatTime(_descriptor.LastActiveAt);

    public void Update(SessionDescriptor descriptor)
    {
        _descriptor = descriptor;
        Raise(nameof(Title));
        Raise(nameof(Preview));
        Raise(nameof(TimeLabel));
        Raise(nameof(UnreadCount));
        Raise(nameof(Muted));
        Raise(nameof(Pinned));
        Raise(nameof(Emoji));
        Raise(nameof(AccentArgb));
    }

    internal static string FormatTime(DateTimeOffset at)
    {
        var now = DateTimeOffset.Now;
        if (at.Date == now.Date)
            return at.ToString("HH:mm");
        if (at.Date == now.Date.AddDays(-1))
            return "昨天";
        // 本周以内（含今天往前 6 天）给星期，用中文习惯的"周一"。
        if (at.Date > now.Date.AddDays(-7))
            return at.DayOfWeek switch
            {
                DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二",
                DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四",
                DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六",
                _ => "周日",
            };
        return at.Year == now.Year ? at.ToString("M/d") : at.ToString("yyyy/M/d");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 聊天客户端的视图模型：会话目录 + 当前激活会话的编排。
///
/// 它是**唯一**同时知道"目录表"与"运行时"的地方。UI 只绑它、只调它，
/// 不许自己 new SessionStore 或直接动 AgentPool —— 那样会让"切会话"的
/// 顺序（结束旧会话 → 清空 → 激活新的 → 重建消息）在几处各写一遍。
///
/// 一律不引用 Window / UIElement：窗口类型只出现在宿主里。
/// </summary>
public sealed class ChatShellViewModel : INotifyPropertyChanged
{
    private readonly SessionStore _store;
    private SessionCatalog _catalog;
    private ChatSessionRuntime? _runtime;
    private string? _activeSessionId;
    private bool _catalogDirty;
    private DateTimeOffset _lastCatalogSave = DateTimeOffset.MinValue;
    private Task _activeTurn = Task.CompletedTask;

    /// <summary>
    /// 当前会话的情境台词服务。**只有单聊才有一个明确的"她"**——
    /// 群聊里没有单一说话人，所以为 null 时走中性语气。
    /// </summary>
    private HuTao.Persona.ScriptedLines? _activeScripted;

    public ChatShellViewModel(SessionStore store, SessionCatalog catalog)
    {
        _store = store;
        _catalog = catalog;
    }

    /// <summary>侧栏数据源（按 <see cref="SessionCatalog.Ordered"/> 的顺序）。</summary>
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    /// <summary>当前会话的消息（已渲染的形态，由窗口负责转成气泡）。</summary>
    public ObservableCollection<ConversationTurn> ActiveTurns { get; } = [];

    public string? ActiveSessionId
    {
        get => _activeSessionId;
        private set
        {
            if (_activeSessionId == value)
                return;
            _activeSessionId = value;
            Raise();
            Raise(nameof(ActiveSession));
            Raise(nameof(ActiveDescriptor));
        }
    }

    public SessionItemViewModel? ActiveSession
        => Sessions.FirstOrDefault(session => session.Id == ActiveSessionId);

    public SessionDescriptor? ActiveDescriptor => ActiveSession?.Descriptor;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            Raise();
            Raise(nameof(CanSend));
        }
    }
    private bool _isBusy;

    /// <summary>正在生成时**输入框仍然可用**（允许排队打字），只有发送按钮变灰。</summary>
    public bool CanSend => !IsBusy && ActiveSessionId is not null;

    /// <summary>
    /// 用户已经发出、正等角色开口。
    ///
    /// 原版桌宠在这段时间显示一个"正在输入"占位气泡（<c>AddThinkingBubble</c>）。
    /// 没有它，用户会以为自己的消息被吞了——因为角色回复可能要好几秒。
    /// </summary>
    public bool IsAwaitingReply
    {
        get => _isAwaitingReply;
        private set
        {
            if (_isAwaitingReply == value)
                return;
            _isAwaitingReply = value;
            Raise();
        }
    }
    private bool _isAwaitingReply;

    /// <summary>系统提示行。**绝不混进消息流**——那是这个项目从桌宠时代就守的规矩。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
                return;
            _statusMessage = value;
            Raise();
            Raise(nameof(HasStatus));
        }
    }
    private string _statusMessage = "";

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>自动对谈是否在跑。群聊才用得上。</summary>
    public bool IsAutoTalking
    {
        get => _isAutoTalking;
        private set
        {
            if (_isAutoTalking == value)
                return;
            _isAutoTalking = value;
            Raise();
        }
    }
    private bool _isAutoTalking;

    public string SearchText { get; set; } = "";

    public int TotalSessions => _catalog.Sessions.Count;

    /// <summary>
    /// 导演调度信息（只有群聊有意义）。给窗口的 🐞 面板用——它是作者视角的东西，
    /// 默认折叠，**绝不混进消息流**（旧聊天室窗口也是这么处理的）。
    /// </summary>
    public string DirectorTrace
    {
        get
        {
            var decision = _runtime?.LastDirectorDecision;
            if (decision is null)
                return "";
            return string.IsNullOrWhiteSpace(decision.NextSpeakerId)
                ? $"导演 → 收场：{decision.Reason}"
                : $"导演 → {DisplayNameOf(decision.NextSpeakerId)}：{decision.Reason}";
        }
    }

    private static string DisplayNameOf(string characterId)
        => HuTao.Persona.CharacterCatalog.Find(characterId)?.Name ?? characterId;

    // ── 初始化 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 载入目录并按需迁移。默认选中"桌宠当前角色"的单聊，其次目录里的第一条。
    /// </summary>
    public async Task InitializeAsync(
        string repositoryRoot,
        IReadOnlyList<HuTao.Persona.CharacterDefinition> characters,
        Func<string, string>? accentProvider = null,
        string? preferredCharacterId = null,
        CancellationToken ct = default)
    {
        _catalog = await _store.LoadCatalogAsync(ct);
        var report = await SessionMigration.RunAsync(
            _store, repositoryRoot, characters, accentProvider, ct: ct);

        foreach (var warning in report.Warnings)
            StatusMessage = warning;

        _catalog = await _store.LoadCatalogAsync(ct);
        RefreshSessions();

        var preferred = preferredCharacterId is null
            ? null
            : SessionFactory.SingleSessionId(preferredCharacterId);
        var target = preferred is not null && _catalog.Find(preferred) is not null
            ? preferred
            : _catalog.Ordered().FirstOrDefault()?.Id;

        if (target is not null)
            await SwitchSessionAsync(target, ct);
    }

    /// <summary>把目录投影回侧栏。搜索过滤在这一层做，UI 只绑结果。</summary>
    public void RefreshSessions()
    {
        var ordered = _catalog.Ordered()
            .Where(Matches)
            .ToArray();

        // 原地对齐而不是清空重建：清空会让 ListBox 丢掉选中项、并让滚动位置跳回顶部。
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i < Sessions.Count && Sessions[i].Id == ordered[i].Id)
            {
                Sessions[i].Update(ordered[i]);
                continue;
            }
            var existingIndex = Sessions.ToList().FindIndex(item => item.Id == ordered[i].Id);
            if (existingIndex >= 0)
                Sessions.Move(existingIndex, i);
            else
                Sessions.Insert(i, new SessionItemViewModel(ordered[i]));
        }
        while (Sessions.Count > ordered.Length)
            Sessions.RemoveAt(Sessions.Count - 1);

        Raise(nameof(TotalSessions));
    }

    private bool Matches(SessionDescriptor session)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;
        var needle = SearchText.Trim();
        if (session.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return true;
        // 群聊按成员名也能搜到——用户记的是"有谁在"，不是群名。
        return session.ActivePresences.Any(presence =>
            (HuTao.Persona.CharacterCatalog.Find(presence.CharacterId)?.Name ?? presence.CharacterId)
            .Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    // ── 切换会话 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 切到另一个会话。顺序**不可调换**：
    /// 等当前回合收尾 → 结束旧会话（交接 + 释放租约）→ 清空消息 → 激活新的 → 重建消息。
    /// 先把消息清空再结束旧会话，会让交接时读到的上下文对不上；不结束旧会话
    /// 就直接激活新的，新会话会把旧会话还在用的角色实例抢走。
    /// </summary>
    public async Task SwitchSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var descriptor = _catalog.Find(sessionId);
        if (descriptor is null)
            return;

        if (ActiveSessionId == sessionId && _runtime is not null)
            return;

        var previousId = ActiveSessionId;
        IsBusy = true;
        try
        {
            try { await _activeTurn; }
            catch (Exception) { /* 上一轮失败不该阻止切换 */ }

            if (previousId is not null)
                await FlushCatalogForLeaveAsync(previousId, ct);

            if (_runtime is not null)
                await DetachRuntimeAsync(ct);

            ActiveTurns.Clear();

            _catalog = _catalog.MarkRead(sessionId, DateTimeOffset.Now);
            _catalogDirty = true;
            await SaveCatalogAsync(force: true);
            RefreshSessions();

            _runtime = await CreateRuntimeAsync(descriptor, ct);
            // 运行时改了自己的描述符（最近活跃/预览）时并进目录表——
            // 侧栏读的是 index.json，不接这条线预览就会停在"切换会话那一刻"。
            _runtime.DescriptorChanged += OnRuntimeDescriptorChanged;
            // 单聊才有一个明确的"她"；群聊没有单一说话人，用中性语气。
            _activeScripted = descriptor.Kind == SessionKind.Single
                ? _runtime.PersonaForScriptedLines
                : null;
            await _runtime.ActivateAsync(ct);

            var timeline = await _store.LoadTimelineAsync(sessionId, ct);
            foreach (var turn in timeline)
                ActiveTurns.Add(turn);

            ActiveSessionId = sessionId;
            StatusMessage = timeline.Count == 0
                ? $"和 {DisplayName(descriptor)} 打个招呼吧。"
                : "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            HuTao.Foundation.Diagnostics.LocalDiagnosticLog.Default.Write("chat_shell.switch", ex);
            StatusMessage = $"切换会话失败：{ex.GetType().Name}";
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(ActiveTurns));
        }
    }

    /// <summary>可被宿主替换的运行时工厂。默认没有——没装配时切会话会明确报错，而不是静默空转。</summary>
    public Func<SessionDescriptor, CancellationToken, Task<ChatSessionRuntime>>? RuntimeFactory { get; set; }

    private async Task<ChatSessionRuntime> CreateRuntimeAsync(SessionDescriptor descriptor, CancellationToken ct)
    {
        if (RuntimeFactory is not null)
            return await RuntimeFactory(descriptor, ct);
        throw new InvalidOperationException(
            "尚未提供运行时工厂：宿主必须先调用 UseRuntimeFactory 用 AgentPool 装配。");
    }

    /// <summary>由宿主注入真实工厂（它持有 AgentPool 与 TTS 引擎）。</summary>
    public void UseRuntimeFactory(Func<SessionDescriptor, CancellationToken, Task<ChatSessionRuntime>> factory)
        => RuntimeFactory = factory;

    // ── 收发 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 发一条消息。
    ///
    /// 原版桌宠的行为是：**回车那一刻自己的话就出现在屏幕上**，然后才等角色回应。
    /// 所以这里做两件事：
    /// ① 立刻插一条"本地待确认"的用户气泡（只在内存里，不落盘）；
    /// ② 等 Core 回调说"用户那句落盘了"，就用真记录把它换掉——这样时间戳/Id
    ///    来自时间线，而不是界面自己编的。
    /// 全程不必等角色生成完，所以用户不必干等到下一句出现才看到自己说过什么。
    /// </summary>
    public Task SendAsync(string text, CancellationToken ct = default)
    {
        if (_runtime is null || string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;
        if (_activeTurn is { IsCompleted: false })
            return Task.CompletedTask;

        var trimmed = text.Trim();
        var optimistic = new ConversationTurn(
            "local-user-" + Guid.NewGuid().ToString("N")[..8],
            DateTimeOffset.Now,
            "user",
            "你",
            trimmed,
            IsUser: true,
            AudioPath: null,
            Audience: _runtime.MemberIds,
            Kind: ConversationTurnKind.Message);
        var optimisticIndex = InsertOptimisticUserTurn(optimistic);
        IsAwaitingReply = true;

        var outcomeTask = RunTurnAsync(ct, () => _runtime.SubmitUserAsync(trimmed, ct,
            persisted =>
            {
                // 回调来自线程池（运行时内部一路 ConfigureAwait(false)），
                // 改绑到界面的集合必须先回 UI 线程。
                RunOnUi(() => ReplaceOptimisticUserTurn(optimistic, persisted));
                return Task.CompletedTask;
            }));
        _activeTurn = outcomeTask;
        return outcomeTask;
    }

    /// <summary>插入乐观用户气泡，返回它的下标（用于稍后原地替换）。</summary>
    private int InsertOptimisticUserTurn(ConversationTurn turn)
    {
        ActiveTurns.Add(turn);
        return ActiveTurns.Count - 1;
    }

    /// <summary>
    /// 用落盘的真记录替换乐观气泡。
    ///
    /// 按下标原地替换而不是"先删再加"：加在末尾会让用户那句话在生成期间
    /// 跳到"正在输入"占位下面，看起来像顺序反了。
    /// 找不到就退回追加——乐观项可能已被整表重载刷掉，那不算错。
    /// </summary>
    private void ReplaceOptimisticUserTurn(ConversationTurn optimistic, ConversationTurn persisted)
    {
        var index = ActiveTurns.IndexOf(optimistic);
        if (index < 0)
        {
            if (!ActiveTurns.Contains(persisted))
                ActiveTurns.Add(persisted);
            return;
        }
        ActiveTurns.RemoveAt(index);
        ActiveTurns.Insert(index, persisted);
    }

    /// <summary>不说话，只推进一步。</summary>
    public Task StepAsync(CancellationToken ct = default)
    {
        if (_runtime is null || _activeTurn is { IsCompleted: false })
            return Task.CompletedTask;
        var outcomeTask = RunTurnAsync(ct, () => _runtime.StepAsync(ct));
        _activeTurn = outcomeTask;
        return outcomeTask;
    }

    /// <summary>自动对谈：到上限或收场就停。</summary>
    public async Task ToggleAutoTalkAsync(CancellationToken ct = default)
    {
        if (IsAutoTalking)
        {
            IsAutoTalking = false;
            StatusMessage = "已暂停自动对谈。";
            return;
        }

        IsAutoTalking = true;
        var rounds = 0;
        try
        {
            while (IsAutoTalking && !ct.IsCancellationRequested)
            {
                var before = ActiveTurns.Count;
                await StepAsync(ct);
                if (ActiveTurns.Count == before)
                    break;
                if (++rounds >= 40)
                {
                    StatusMessage = "自动对谈已达 40 轮，先停一下——再聊下去容易变成互相客套。";
                    break;
                }
                try { await Task.Delay(600, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            IsAutoTalking = false;
        }
    }

    private async Task RunTurnAsync(CancellationToken ct, Func<Task<SessionTurnOutcome>> action)
    {
        IsBusy = true;
        try
        {
            await SaveCatalogAsync();

            // 这里**不能**用 ConfigureAwait(false)：`ChatSessionRuntime` 内部一路用
            // ConfigureAwait(false)，所以 action() 之后线程上下文已经跑到线程池上了。
            // ActiveTurns 是绑到 UI 的 ObservableCollection，从线程池改它会抛
            // 「不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改」——
            // 表现是**消息发出去了、也落盘了，但界面上一个字都没多**（真机实测抓到）。
            // 这个类整体是 UI 层的视图模型，所有续体都必须回到 UI 线程。
            var outcome = await action();
            // 先把本轮**每一段**都追进去，再以落盘时间线为准重建。
            // 只追 outcome.Turn（最后一段）会让一次发言里的前几句整段消失——
            // 原版桌宠单聊是"一句一个气泡"，一轮里落 3 条就要显示 3 个。
            foreach (var turn in outcome.AllTurns)
            {
                if (!ActiveTurns.Contains(turn))
                    ActiveTurns.Add(turn);
            }
            if (outcome.Turn is not null)
            {
                // 时间线是唯一事实来源，列表只是它的视图；重建能同时把
                // 乐观气泡换成真记录、并按落盘顺序摆正。
                await ReloadActiveTurnsAsync(ct);
                await ReloadCatalogAsync(ct);
            }
            if (!string.IsNullOrWhiteSpace(outcome.StatusMessage))
                StatusMessage = outcome.StatusMessage;
            else if (outcome.Ended)
                StatusMessage = "对话自然收场。可以插话重新起话题。";
            else if (outcome.Turn is not null)
                StatusMessage = $"第 {ActiveTurns.Count(t => !t.IsUser)} 轮 · {outcome.SpeakerName}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            HuTao.Foundation.Diagnostics.LocalDiagnosticLog.Default.Write("chat_shell.turn", ex);
            StatusMessage = $"这一轮失败了：{ex.GetType().Name}";
        }
        finally
        {
            IsBusy = false;
            // 无论成功、失败还是取消都要收掉"正在输入"占位——
            // 忘了这一步，占位会永远挂在那里，看起来像卡死了。
            IsAwaitingReply = false;
        }
    }

    /// <summary>
    /// 把一段工作调度回 UI 线程。宿主注入（WPF 用 <c>Dispatcher</c>）；
    /// 为 null 时直接就地执行，方便离线检查。
    ///
    /// 为什么必须有它：运行时会在**线程池**上抛 <see cref="ChatSessionRuntime.DescriptorChanged"/>
    /// （它内部一路 ConfigureAwait(false)）。而处理它要改绑到界面的
    /// ObservableCollection 并重画侧栏——从线程池做这些会抛
    /// 「此操作仅在…Dispatcher 线程上有效」，真机表现是**发出去的消息角色不回**。
    /// </summary>
    public Action<Action>? Dispatch { get; set; }

    private void RunOnUi(Action action)
    {
        if (Dispatch is null) { action(); return; }
        Dispatch(action);
    }

    /// <summary>
    /// 运行时更新了会话描述符（最近活跃时间、侧栏预览）→ 并进目录表并刷新侧栏。
    /// 目录表的落盘由本类统一负责（单写入者），运行时只管通知。
    /// </summary>
    private void OnRuntimeDescriptorChanged(SessionDescriptor descriptor)
    {
        // 描述符是 record（不可变），可以安全跨线程捕获；真正的界面改动丢回 UI 线程做。
        RunOnUi(() =>
        {
            _catalog = _catalog.Upsert(descriptor);
            _catalogDirty = true;
            RefreshSessions();
        });
    }

    /// <summary>按落盘的时间线重建当前会话的消息列表。找不到会话或时间线为空时不抛。</summary>
    public async Task ReloadActiveTurnsAsync(CancellationToken ct = default)
    {
        if (ActiveSessionId is not { } sessionId)
            return;
        var timeline = await _store.LoadTimelineAsync(sessionId, ct);
        ActiveTurns.Clear();
        foreach (var turn in timeline)
            ActiveTurns.Add(turn);
    }

    // ── 目录操作 ─────────────────────────────────────────────────────────

    public Task SetPinnedAsync(string sessionId, bool pinned)
        => MutateAsync(catalog => catalog.SetPinned(sessionId, pinned));

    public Task SetMutedAsync(string sessionId, bool muted)
        => MutateAsync(catalog => catalog.SetMuted(sessionId, muted));

    public Task MarkReadAsync(string sessionId)
        => MutateAsync(catalog => catalog.MarkRead(sessionId, DateTimeOffset.Now));

    public Task RenameAsync(string sessionId, string title)
        => MutateAsync(catalog => catalog.Rename(sessionId, title));

    public Task SetAllowProactiveAsync(string sessionId, bool allow)
        => MutateAsync(catalog => catalog.SetAllowProactive(sessionId, allow));

    /// <summary>把成员变更写进目录（在场区间由调用方算好，因为它需要"现在"这个时刻）。</summary>
    public Task ApplyPresencesAsync(string sessionId, IReadOnlyList<MemberPresence> presences)
        => MutateAsync(catalog => catalog.ApplyPresences(sessionId, presences));

    /// <summary>新建群聊。命中"成员集合完全相同"时直接激活既有群（不新建重复群）。</summary>
    public async Task<SessionDescriptor> CreateGroupAsync(
        IReadOnlyList<string> memberIds, string title, Func<string, string>? accentProvider = null,
        CancellationToken ct = default)
    {
        var before = _catalog.Sessions.Count;
        var group = await SessionFactory.EnsureGroupAsync(
            _store, _catalog, memberIds, title, accentProvider, ct: ct);
        _catalog = _catalog.Upsert(group);
        await SaveCatalogAsync(force: true);
        RefreshSessions();
        if (_catalog.Sessions.Count == before)
            StatusMessage = "已经有一个成员相同的群了，直接切过去。";
        await SwitchSessionAsync(group.Id, ct);
        return group;
    }

    /// <summary>
    /// 唯一的运行时收尾入口：先退订事件，再结束会话、释放。
    /// 三处（切会话 / 清空 / 删除）都走它，避免"某一条路径忘了退订"
    /// 让旧运行时继续往目录表里推自己的描述符。
    /// </summary>
    private async Task DetachRuntimeAsync(CancellationToken ct = default)
    {
        if (_runtime is null)
            return;
        _runtime.DescriptorChanged -= OnRuntimeDescriptorChanged;
        _activeScripted = null;
        try
        {
            await _runtime.EndSessionAsync(ct);
            await _runtime.DisposeAsync();
        }
        finally
        {
            _runtime = null;
        }
    }

    /// <summary>清空当前会话的消息（保留会话与长期记忆）。</summary>
    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        if (ActiveSessionId is null)
            return;
        var sessionId = ActiveSessionId;
        await _store.ClearHistoryAsync(sessionId, ct);
        ActiveTurns.Clear();
        await DetachRuntimeAsync(ct);
        // 重新激活以便重建空上下文；不重建的话 agent 还记着刚被清掉的历史。
        if (_catalog.Find(sessionId) is { } descriptor)
        {
            _runtime = await CreateRuntimeAsync(descriptor, ct);
            _runtime.DescriptorChanged += OnRuntimeDescriptorChanged;
            // 单聊才有一个明确的"她"；群聊没有单一说话人，用中性语气。
            _activeScripted = descriptor.Kind == SessionKind.Single
                ? _runtime.PersonaForScriptedLines
                : null;
            await _runtime.ActivateAsync(ct);
        }
        // 这句是**角色说的**，所以要走人设数据：以前写死成胡桃口吻，
        // 于是清空芙宁娜的记录也会得到一句胡桃语气的话。
        // 单聊取该角色的台词；群聊没有单一角色，用中性语气。
        StatusMessage = _activeScripted?.Get(HuTao.Persona.ScriptedLines.Situation.HistoryCleared)
                        ?? HuTao.Persona.ScriptedLines.Neutral(HuTao.Persona.ScriptedLines.Situation.HistoryCleared);
    }

    /// <summary>删除会话：目录 + 目录树一起走，都进回收站。</summary>
    public async Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        if (ActiveSessionId == sessionId)
        {
            await DetachRuntimeAsync(ct);
            ActiveTurns.Clear();
            ActiveSessionId = null;
        }

        _catalog = _catalog.Remove(sessionId);
        await SaveCatalogAsync(force: true);
        await _store.DeleteSessionAsync(sessionId, ct);
        RefreshSessions();

        if (ActiveSessionId is null && _catalog.Ordered().FirstOrDefault() is { } next)
            await SwitchSessionAsync(next.Id, ct);
    }

    private async Task MutateAsync(Func<SessionCatalog, SessionCatalog> change)
    {
        _catalog = change(_catalog);
        _catalogDirty = true;
        await SaveCatalogAsync(force: true);
        RefreshSessions();
    }

    private async Task ReloadCatalogAsync(CancellationToken ct)
    {
        // 运行时会把 LastActiveAt / LastPreview 写进 session.json，
        // 这里按值同步回来，避免侧栏预览停在旧内容。
        var descriptor = _runtime?.Descriptor;
        if (descriptor is not null)
            _catalog = _catalog.Upsert(descriptor);
        _catalogDirty = true;
        await SaveCatalogAsync();
        RefreshSessions();
    }

    /// <summary>
    /// 离开会话前把它记到目录里。未读**不在这里加**——未读是"消息产生时"的事实
    /// （见 <see cref="BumpUnreadForOthersAsync"/>），离开时才加会把"我在看着它的时候
    /// 它自己冒出来的消息"也算成未读。
    /// </summary>
    private Task FlushCatalogForLeaveAsync(string sessionId, CancellationToken ct)
    {
        _ = ct;
        if (_catalog.Find(sessionId) is { } descriptor)
            _catalog = _catalog.Touch(sessionId, descriptor.LastActiveAt, descriptor.LastPreview);
        _catalogDirty = true;
        return Task.CompletedTask;
    }

    /// <summary>非激活会话收到角色消息时累加未读。UI 在消息到来时调用。</summary>
    public async Task BumpUnreadForOthersAsync(string sessionId)
    {
        if (ActiveSessionId == sessionId)
            return;
        _catalog = _catalog.BumpUnread(sessionId);
        _catalogDirty = true;
        await SaveCatalogAsync();
        RefreshSessions();
    }

    /// <summary>
    /// 落盘目录表。默认 debounce ≥500ms，避免拖动置顶时狂写盘；
    /// <paramref name="force"/> 用于"用户明确改了什么"的场合，必须立刻生效。
    /// </summary>
    public async Task SaveCatalogAsync(bool force = false)
    {
        if (!_catalogDirty && !force)
            return;
        var now = DateTimeOffset.Now;
        if (!force && now - _lastCatalogSave < TimeSpan.FromMilliseconds(500))
            return;
        _lastCatalogSave = now;
        _catalogDirty = false;
        try
        {
            await _store.SaveCatalogAsync(_catalog);
        }
        catch (Exception ex)
        {
            HuTao.Foundation.Diagnostics.LocalDiagnosticLog.Default.Write("chat_shell.catalog_save", ex);
        }
    }

    /// <summary>关窗前的收尾：结束当前会话、把目录落盘。</summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        IsAutoTalking = false;
        try { await _activeTurn; } catch (Exception) { }
        await DetachRuntimeAsync(ct);
        await SaveCatalogAsync(force: true);
    }

    /// <summary>切换会话时用到的入参：运行时的装配交给宿主注入的工厂。</summary>
    public SessionStore SessionStoreForHost => _store;

    public SessionCatalog Catalog => _catalog;

    private static string DisplayName(SessionDescriptor descriptor)
        => descriptor.Kind == SessionKind.Group ? descriptor.Title : descriptor.Title;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
