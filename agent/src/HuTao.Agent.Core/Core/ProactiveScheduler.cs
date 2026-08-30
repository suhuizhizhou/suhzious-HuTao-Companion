using System.Text.RegularExpressions;
using HuTao.Agent.Core.Tools;

namespace HuTao.Agent.Core.Core;

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
    private DateTimeOffset _lastTrigger = DateTimeOffset.MinValue;

    public ProactiveScheduler(
        int intervalSeconds,
        int cooldownSeconds,
        int minIdleSeconds,
        Func<bool>? doNotDisturb = null)
    {
        _intervalSeconds = intervalSeconds;
        _cooldownSeconds = cooldownSeconds;
        _minIdleSeconds = minIdleSeconds;
        _doNotDisturb = doNotDisturb;
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

            _lastTrigger = DateTimeOffset.UtcNow;
            await onTrigger(ct).ConfigureAwait(false);
        }
    }

    private bool ShouldTrigger()
    {
        if (_doNotDisturb?.Invoke() == true)
            return false;

        var sinceLast = DateTimeOffset.UtcNow - _lastTrigger;
        if (sinceLast.TotalSeconds < _cooldownSeconds)
            return false;

        var idle = _idleTool.ExecuteAsync().GetAwaiter().GetResult();
        var match = Regex.Match(idle, @"(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var sec))
            return sec >= _minIdleSeconds;

        // 解析失败时默认允许（不阻塞主动性）
        return true;
    }
}
