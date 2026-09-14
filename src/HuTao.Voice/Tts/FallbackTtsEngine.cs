using HuTao.Foundation.Abstractions;

namespace HuTao.Voice;

/// <summary>
/// 主引擎失败时降级到按需进程模式，避免本地 Python/模型环境异常导致桌宠完全无法说话。
/// </summary>
public sealed class FallbackTtsEngine : ITtsEngine, IAsyncDisposable
{
    private readonly ITtsEngine _primary;
    private readonly ITtsEngine _fallback;
    private int _primaryDisabled;

    public FallbackTtsEngine(ITtsEngine primary, ITtsEngine fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public string Name => Volatile.Read(ref _primaryDisabled) == 0
        ? $"{_primary.Name} (fallback: {_fallback.Name})"
        : _fallback.Name;

    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _primaryDisabled) == 0)
        {
            try
            {
                return await _primary.SynthesizeAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                Interlocked.Exchange(ref _primaryDisabled, 1);
            }
        }

        return await _fallback.SynthesizeAsync(request, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_primary is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        if (_fallback is IAsyncDisposable fallbackDisposable)
            await fallbackDisposable.DisposeAsync().ConfigureAwait(false);
        else if (_fallback is IDisposable disposable)
            disposable.Dispose();
    }
}
