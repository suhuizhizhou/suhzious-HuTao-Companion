using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;
using HuTao.Persona;
using HuTao.Dialogue.Core;

// AgentPool 的核心价值就是那条不变量（每个角色最多一个存活实例），
// 而验证它必须能注入一个假的装配函数——真装配要人设/语音/知识文件，离线测试给不出来。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HuTao.StoryRag.Eval")]

namespace HuTao.Dialogue.Runtime;

/// <summary>
/// 一个会话对若干角色实例的占用声明。
///
/// <see cref="Dispose"/> 只做**登记释放**，不做任何 I/O —— 把上下文交接出去
/// （capture → 投影 → 落盘）是 <c>ChatSessionRuntime.EndSessionAsync</c> 的职责，
/// 顺序反了就会在"已经释放、别人接走"之后才去捕获，拿到的是别人的上下文。
/// </summary>
public sealed class AgentContextLease : IDisposable
{
    private readonly AgentPool _pool;
    private bool _released;

    internal AgentContextLease(AgentPool pool, string sessionId, IReadOnlyDictionary<string, AgentRuntime> runtimes)
    {
        _pool = pool;
        SessionId = sessionId;
        Runtimes = runtimes;
    }

    public string SessionId { get; }

    /// <summary>本次租约覆盖的角色实例（characterId → runtime）。</summary>
    public IReadOnlyDictionary<string, AgentRuntime> Runtimes { get; }

    public void Dispose()
    {
        if (_released)
            return;
        _released = true;
        _pool.ReleaseLease(this);
    }
}

/// <summary>
/// 全进程角色实例池。**不变量 I1（每角色最多一个存活实例）与 I3（每会话记忆作用域唯一）的唯一执行者。**
///
/// 为什么必须有它：同一个角色起两份 runtime 会同时踩四个坑（见设计书 §1.4）——
/// 记忆 json 互相覆写、authority 锁让另一份的在途回合退化成"依据有变化"、
/// 常驻 TTS 的参考音频缓存被交叉设置、生成音频同毫秒撞名。
/// 只要保证"一个角色一个实例、切会话换上下文"，这四个坑就一次性消失。
///
/// 它不理解会话存储，也不做投影：那些是 <c>ChatSessionRuntime</c> 的事。
/// 池子只管"谁活着、谁借给了哪个会话、冲突时先交接"。
/// </summary>
public sealed class AgentPool : IAsyncDisposable
{
    private readonly Func<string, CharacterDefinition, CancellationToken, Task<AgentRuntime>> _createRuntime;
    private readonly LocalDiagnosticLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, AgentRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AgentRuntime> _disposed = [];
    private bool _closed;

    public AgentPool(AgentRuntimeFactory factory, ITtsEngine? sharedTts, LocalDiagnosticLog? log = null)
        : this(
            (sessionId, character, ct) => Task.Run(() => factory.Create(
                character,
                sharedTts: sharedTts,
                // 聊天里角色不该盯着用户的前台窗口——"你在看代码哦"会破坏场景。
                // 这与旧聊天室窗口的既有决定一致，也是聊天场景的既定行为。
                allowAppAwareness: () => false,
                memoryScope: sessionId), ct),
            log)
    {
    }

    /// <summary>
    /// 注入装配函数的构造重载。
    ///
    /// `AgentRuntimeFactory.Create` 需要真实的人设/语音/知识文件，离线检查给不出来；
    /// 而"一个角色只有一个实例"这条不变量恰恰最需要被钉死。宿主做集成测试时也用它。
    /// </summary>
    public AgentPool(
        Func<string, CharacterDefinition, CancellationToken, Task<AgentRuntime>> createRuntime,
        LocalDiagnosticLog? log = null)
    {
        _createRuntime = createRuntime;
        _log = log ?? LocalDiagnosticLog.Default;
    }

    /// <summary>已在池中的角色（不触发装配）。UI 用它预判"这次切换要不要转圈"。</summary>
    public IReadOnlyCollection<string> ResidentCharacterIds
    {
        get
        {
            lock (_leases)
                return _runtimes.Keys.ToArray();
        }
    }

    /// <summary>
    /// 激活一个会话。<paramref name="handoff"/> 会在角色**被别的会话占用**时先被调用，
    /// 让调用方把那一份上下文交接落盘，然后才轮到本会话接管。
    ///
    /// 顺序不可调换：交接必须在释放之前，否则前者拿到的是被后来者覆写过的上下文。
    /// </summary>
    public async Task<AgentContextLease> ActivateAsync(
        string sessionId,
        IReadOnlyList<string> characterIds,
        Func<string, CancellationToken, Task>? handoff = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("会话 Id 不能为空。", nameof(sessionId));

        var wanted = characterIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (wanted.Length == 0)
            throw new ArgumentException("至少要有一位角色。", nameof(characterIds));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed)
                throw new InvalidOperationException("Agent 池已关闭。");

            // 1. 先把被别人占着、而本次也要用的角色交接出去（调用方负责落盘）。
            var evict = new List<string>();
            foreach (var characterId in wanted)
            {
                if (!_leases.TryGetValue(sessionId, out var own) || !own.Contains(characterId))
                {
                    if (_leases.Any(pair => pair.Value.Contains(characterId)))
                        evict.Add(characterId);
                }
            }
            foreach (var characterId in evict)
            {
                if (handoff is null)
                    continue;
                try { await handoff(characterId, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.Write("pool.handoff", ex); }
            }

            // 2. 从所有其他租约里摘掉这些角色（交接已完成，可以安全易主）。
            foreach (var characterId in evict.Concat(wanted))
            {
                foreach (var (owner, members) in _leases)
                {
                    if (string.Equals(owner, sessionId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    members.Remove(characterId);
                }
            }

            // 3. 装配尚未存在的角色。
            var resolved = new Dictionary<string, AgentRuntime>(StringComparer.OrdinalIgnoreCase);
            foreach (var characterId in wanted)
            {
                if (_runtimes.TryGetValue(characterId, out var existing))
                {
                    resolved[characterId] = existing;
                    continue;
                }

                var definition = CharacterCatalog.Find(characterId)
                    ?? throw new ArgumentException($"未注册角色: {characterId}", nameof(characterIds));
                // scope 就是会话 Id —— 这是 I3 的实现：会话 Id 全局唯一，
                // 所以 {角色}_{会话} 的记忆文件必然互不相同。
                var created = await CreateWithTransientRetryAsync(sessionId, definition, ct).ConfigureAwait(false);
                _runtimes[characterId] = created;
                resolved[characterId] = created;
            }

            // 4. 登记租约。
            if (!_leases.TryGetValue(sessionId, out var lease))
                _leases[sessionId] = lease = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var characterId in wanted)
                lease.Add(characterId);

            return new AgentContextLease(this, sessionId, resolved);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 装配角色实例，对**瞬时**文件锁冲突做有限重试。
    ///
    /// 为什么需要它：<c>KnowledgeAuthorityFence.Acquire</c> 全程持有
    /// <c>authority…lock</c>（<c>FileShare.None</c>）。本项目里**允许存在另一个进程/窗口
    /// 持有同一角色**（桌宠窗与聊天客户端就是两套宿主，各自持有工厂），
    /// 于是"对方正好在装配的那一瞬间"会让这里拿到 <see cref="IOException"/>。
    /// 那是**瞬时**冲突，等一会儿就好——把它当成硬失败会让用户看到莫名的"切换会话失败"。
    ///
    /// 只重试 IO 类异常：参数错误、角色未注册这类**确定性**失败必须立刻冒泡，
    /// 重试它们只会让错误变慢。
    /// </summary>
    private async Task<AgentRuntime> CreateWithTransientRetryAsync(
        string sessionId, CharacterDefinition definition, CancellationToken ct)
    {
        const int attempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _createRuntime(sessionId, definition, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < attempts && IsTransientLockConflict(ex))
            {
                _log.Write("pool.create_retry", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(60 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Windows 文件锁冲突的表现形式：<see cref="IOException"/> 及其子类。</summary>
    private static bool IsTransientLockConflict(Exception ex)
        => ex is IOException;

    internal void ReleaseLease(AgentContextLease lease)
    {
        lock (_leases)
        {
            if (_leases.TryGetValue(lease.SessionId, out var members))
            {
                foreach (var characterId in lease.Runtimes.Keys)
                    members.Remove(characterId);
                if (members.Count == 0)
                    _leases.Remove(lease.SessionId);
            }
        }
    }

    /// <summary>取出某角色此刻的上下文（不释放租约、不摘占用）。</summary>
    public ConversationContext? CaptureContext(string characterId)
    {
        lock (_leases)
            return _runtimes.TryGetValue(characterId, out var runtime)
                ? runtime.Agent.CaptureContext()
                : null;
    }

    /// <summary>把一段视角历史灌进角色上下文。这是"切换上下文"的落点。</summary>
    public void Restore(string characterId, IReadOnlyList<ChatMessage> history, ConversationPrivacyStamp? privacy)
    {
        lock (_leases)
        {
            if (_runtimes.TryGetValue(characterId, out var runtime))
                runtime.Agent.RestoreHistory(history, privacy);
        }
    }

    public AgentRuntime? Find(string characterId)
    {
        lock (_leases)
            return _runtimes.TryGetValue(characterId, out var runtime) ? runtime : null;
    }

    /// <summary>
    /// 回收全部实例。走既有的 <see cref="PendingRuntimeCleanup"/>：失败者留在待清理集合里，
    /// 下次再试。**异常收集成列表返回而不是抛**——关窗路径上抛异常会把"某个角色的记忆
    /// 没存上"变成"整个程序关不掉"。
    /// </summary>
    public async Task<IReadOnlyList<Exception>> DisposeAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _closed = true;
            var failures = new List<Exception>();
            var cleanup = new PendingRuntimeCleanup();
            foreach (var (characterId, runtime) in _runtimes)
            {
                try { await cleanup.RetireAsync(runtime).ConfigureAwait(false); }
                catch (Exception ex) { failures.Add(ex); _log.Write("pool.dispose", ex); }
                _disposed.Add(runtime);
                _ = characterId;
            }
            _runtimes.Clear();
            _leases.Clear();
            try { await cleanup.RetryAsync().ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(ex); _log.Write("pool.dispose_retry", ex); }
            return failures;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAllAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
