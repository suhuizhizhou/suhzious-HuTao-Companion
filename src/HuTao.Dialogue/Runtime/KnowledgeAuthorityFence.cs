namespace HuTao.Dialogue.Runtime;

/// <summary>Short synchronous leases serialize JSON access with SQL activation across processes.</summary>
public sealed class KnowledgeAuthorityFence(string authorityPath)
{
    private readonly object _gate=new();
    private FileStream? _handle;
    private int _depth;
    private bool _retired;

    // Hold the short activation lease while acquiring either handle, preventing
    // registration from racing SQL's check. Lifetime handles are thread independent.
    public IDisposable RegisterLegacyRuntime()=>new RuntimeLease(this,new FileStream(authorityPath+".legacy-live",FileMode.OpenOrCreate,FileAccess.Read,FileShare.Read));
    public IDisposable RequireNoLegacyRuntime()=>new FileStream(authorityPath+".legacy-live",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    public IDisposable RegisterSqlRuntime()=>new FileStream(authorityPath+".sql-live",FileMode.OpenOrCreate,FileAccess.Read,FileShare.Read);
    public IDisposable RequireNoSqlRuntime()=>new FileStream(authorityPath+".sql-live",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);

    public bool LegacyIsCurrent()
    {
        try {using var lease=Acquire(legacy:true);return true;}
        catch(IOException){return false;}
        catch(InvalidOperationException){return false;}
    }

    public IDisposable Acquire(bool legacy)
    {
        Monitor.Enter(_gate);
        try
        {
            if(legacy && _retired)throw new InvalidOperationException("This legacy runtime has been closed.");
            if(_depth==0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
                _handle=new FileStream(authorityPath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            }
            if(legacy && File.Exists(authorityPath))
                throw new InvalidOperationException("SQL owns this conversation; this legacy runtime must be restarted with the SQL backend.");
            _depth++;
            return new Lease(this);
        }
        catch
        {
            if(_depth==0){_handle?.Dispose();_handle=null;}
            Monitor.Exit(_gate);
            throw;
        }
    }

    private sealed class Lease(KnowledgeAuthorityFence owner):IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if(_disposed)return;
            _disposed=true;
            if(--owner._depth==0){owner._handle!.Dispose();owner._handle=null;}
            Monitor.Exit(owner._gate);
        }
    }

    private sealed class RuntimeLease(KnowledgeAuthorityFence owner,FileStream handle):IDisposable
    {
        public void Dispose()
        {
            lock(owner._gate)
            {
                owner._retired=true;
                handle.Dispose();
            }
        }
    }
}
