namespace HuTao.Dialogue.Runtime;

/// <summary>Retains failed cleanup owners until a later successful retry.</summary>
public sealed class PendingRuntimeCleanup
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly HashSet<IAsyncDisposable> _pending=new(ReferenceEqualityComparer.Instance);

    public async Task RetireAsync(IAsyncDisposable runtime)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _pending.Add(runtime);
            await runtime.DisposeAsync().ConfigureAwait(false);
            _pending.Remove(runtime);
        }
        finally {_gate.Release();}
    }

    public async Task RetryAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var failures=new List<Exception>();
            foreach(var runtime in _pending.ToArray())
            {
                try {await runtime.DisposeAsync().ConfigureAwait(false);_pending.Remove(runtime);}
                catch(Exception ex){failures.Add(ex);}
            }
            if(failures.Count>0)throw new AggregateException("Runtime cleanup remains incomplete.",failures);
        }
        finally {_gate.Release();}
    }
}
