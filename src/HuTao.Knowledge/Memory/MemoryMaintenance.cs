namespace HuTao.Knowledge.Memory;

/// <summary>
/// 阶段 4 的调度策略：什么时候允许后台整合。
///
/// 判断逻辑独立成类而不是塞进宿主循环，是为了能在离线用例里直接断言
/// 「空闲不够不整合」「待处理太少不整合」「冷却期内不整合」这些边界，
/// 而不用真的等三分钟。
/// </summary>
public sealed class MemoryMaintenance
{
    private readonly ConversationMemoryStore _store;
    private readonly MemoryConsolidator _consolidator;
    private readonly MemoryOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private DateTimeOffset? _lastRunAt;
    private bool _running;

    public MemoryMaintenance(
        ConversationMemoryStore store,
        MemoryConsolidator consolidator,
        MemoryOptions? options = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _consolidator = consolidator;
        _options = options ?? store.Options;
        _clock = clock ?? (() => DateTimeOffset.Now);
        // 上一次整合时间来自持久化状态，重启后不会立刻再整合一遍。
        _lastRunAt = store.LastConsolidatedAt;
    }

    public bool Enabled => _options.EnableConsolidation;

    /// <summary>当前是否应当触发整合。纯函数式判断，不产生副作用。</summary>
    public bool ShouldRun(TimeSpan idle, DateTimeOffset now)
    {
        if (!_options.EnableConsolidation)
            return false;
        if (idle < _options.ConsolidationIdle)
            return false;
        if (_store.UnconsolidatedTurnCount < _options.MinTurnsToConsolidate)
            return false;
        lock (_gate)
        {
            if (_running)
                return false;
            return _lastRunAt is null || now - _lastRunAt.Value >= _options.ConsolidationCooldown;
        }
    }

    /// <summary>
    /// 满足条件就跑一次整合；否则原样返回 null。
    /// 宿主只需要在已有的空闲轮询里调用它，不需要自己管冷却和去重。
    /// </summary>
    public async Task<ConsolidationResult?> RunIfDueAsync(
        TimeSpan idle,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!ShouldRun(idle, now))
                return null;
            _running = true;
        }

        try
        {
            var result = await _consolidator.RunAsync(now, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _lastRunAt = now;
                // 记忆库本身也要刷盘：整合会改动取代关系与游标。
                _store.Flush();
            }
            return result;
        }
        finally
        {
            lock (_gate) _running = false;
        }
    }
}
