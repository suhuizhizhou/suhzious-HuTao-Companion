using System.Text.RegularExpressions;
using HuTao.Foundation.Diagnostics;
using HuTao.Dialogue.Tools;

namespace HuTao.Dialogue.Core;

/// <summary>
/// 主动调度器：每隔一段时间「心跳」一次，判断是否该主动找用户搭话。
/// 三重保护：冷却时间 + 空闲检测（用户在忙就不打扰）+ 显式的勿扰开关。
/// </summary>
public sealed class ProactiveScheduler
{
    private readonly int _intervalSeconds;
    private readonly int _cooldownSeconds;
    private readonly int _minIdleSeconds;
    private readonly IdleTool _idleTool = new();
    private readonly Func<bool>? _doNotDisturb;
    private readonly TimeProvider _time;
    private readonly Func<string> _readIdle;
    private readonly object _gate = new();
    private DateTimeOffset _lastTrigger = DateTimeOffset.MinValue;

    public ProactiveScheduler(
        int intervalSeconds,
        int cooldownSeconds,
        int minIdleSeconds,
        Func<bool>? doNotDisturb = null,
        TimeProvider? timeProvider = null,
        Func<string>? readIdle = null)
    {
        _intervalSeconds = intervalSeconds;
        _cooldownSeconds = cooldownSeconds;
        _minIdleSeconds = minIdleSeconds;
        _doNotDisturb = doNotDisturb;
        _time = timeProvider ?? TimeProvider.System;
        _readIdle = readIdle ?? (() => _idleTool.ExecuteAsync().GetAwaiter().GetResult());
        NotifyConversationActivity();
    }

    /// <summary>用户发送、气泡/音频全部结束、角色开场和提醒结束都重新计算冷却。</summary>
    public void NotifyConversationActivity()
    {
        lock (_gate) _lastTrigger = _time.GetUtcNow();
    }

    /// <summary>持续运行的心跳循环，到点且满足条件就触发 onTrigger。</summary>
    public async Task RunLoopAsync(Func<CancellationToken, Task> onTrigger, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!ShouldTrigger())
                continue;

            try { await onTrigger(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { LocalDiagnosticLog.Default.Write("proactive.callback", ex); }
            finally { NotifyConversationActivity(); }
        }
    }

    public bool ShouldTrigger()
    {
        if (_doNotDisturb?.Invoke() == true)
            return false;

        TimeSpan sinceLast;
        lock (_gate) sinceLast = _time.GetUtcNow() - _lastTrigger;
        if (sinceLast.TotalSeconds < _cooldownSeconds)
            return false;

        string idle;
        try { idle = _readIdle(); }
        catch (Exception ex) { LocalDiagnosticLog.Default.Write("proactive.idle", ex); return false; }
        var match = Regex.Match(idle, @"(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var sec))
            return sec >= _minIdleSeconds;

        // 无法确认空闲时不主动打扰。
        return false;
    }
}
