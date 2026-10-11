using System.Security.Cryptography;
using System.Text.Json;

namespace HuTao.Dialogue.Runtime;

/// <summary>Recoverable file replacement primitive. The caller must hold host and writer exclusion.</summary>
public static class RollbackFileJournal
{
    public sealed record Entry(string Target,string NewHash,string? OriginalHash);
    public sealed record Manifest(int Version,IReadOnlyList<Entry> Entries);

    public static void Create(string directory,IReadOnlyList<(string Source,string Target)> files)
    {
        var root=Path.GetFullPath(directory);
        if(Directory.Exists(root)||File.Exists(root))throw new IOException("Journal already exists.");
        if(files.Count==0)throw new ArgumentException("No installation files.");
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        var targets=files.Select(f=>Path.GetFullPath(f.Target)).ToArray();
        if(targets.Distinct(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal).Count()!=files.Count ||
            targets.Any(p=>p.Equals(root,comparison)||p.StartsWith(root+Path.DirectorySeparatorChar,comparison)))
            throw new InvalidOperationException("Journal targets must be distinct and outside the journal.");
        Directory.CreateDirectory(root);
        var entries=new List<Entry>();
        for(var i=0;i<files.Count;i++)
        {
            var next=File.ReadAllBytes(files[i].Source);
            var previous=File.Exists(targets[i])?File.ReadAllBytes(targets[i]):null;
            WriteNew(Path.Combine(root,$"{i}.new"),next);
            if(previous is not null)WriteNew(Path.Combine(root,$"{i}.old"),previous);
            entries.Add(new(targets[i],Hash(next),previous is null?null:Hash(previous)));
        }
        // Absence of manifest means preparation was interrupted before any target could change.
        WriteNew(Path.Combine(root,"manifest.json"),JsonSerializer.SerializeToUtf8Bytes(new Manifest(1,entries)));
    }

    public static void Apply(string directory,IReadOnlyList<string> expectedTargets,Action<int>? afterReplacement=null)=>Execute(directory,expectedTargets,restore:false,afterReplacement:afterReplacement);
    public static void Restore(string directory,IReadOnlyList<string> expectedTargets)=>Execute(directory,expectedTargets,restore:true);
    public static void VerifyApplied(string directory,IReadOnlyList<string> expectedTargets)=>Execute(directory,expectedTargets,restore:false,verifyOnly:true);
    private static void Execute(string directory,IReadOnlyList<string> expectedTargets,bool restore,bool verifyOnly=false,Action<int>? afterReplacement=null)
    {
        var root=Path.GetFullPath(directory);
        using var exclusive=new FileStream(Path.Combine(root,"operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var manifest=JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(Path.Combine(root,"manifest.json")))
            ??throw new InvalidDataException("Missing journal manifest.");
        if(manifest.Version!=1 || manifest.Entries.Count==0)throw new InvalidDataException("Unsupported journal.");
        var comparer=OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
        if(!manifest.Entries.Select(e=>e.Target).SequenceEqual(expectedTargets.Select(Path.GetFullPath),comparer))
            throw new InvalidDataException("Journal targets differ from the host installation mapping.");
        var payloads=new List<byte[]?>();
        // Validate every backup and current target before changing any target.
        for(var i=0;i<manifest.Entries.Count;i++)
        {
            var entry=manifest.Entries[i];
            if(Directory.Exists(entry.Target))throw new IOException("Installation target is a directory.");
            var next=File.ReadAllBytes(Path.Combine(root,$"{i}.new"));
            var old=entry.OriginalHash is null?null:File.ReadAllBytes(Path.Combine(root,$"{i}.old"));
            if(Hash(next)!=entry.NewHash || old is not null && Hash(old)!=entry.OriginalHash)
                throw new InvalidDataException("Journal payload changed.");
            var current=File.Exists(entry.Target)?Hash(File.ReadAllBytes(entry.Target)):null;
            if(verifyOnly && current!=entry.NewHash)throw new InvalidOperationException("Installation is not fully applied.");
            if(current!=entry.NewHash && current!=entry.OriginalHash)
                throw new InvalidOperationException("Installation target changed outside this transaction.");
            payloads.Add(restore?old:next);
        }
        if(verifyOnly)return;
        for(var i=0;i<manifest.Entries.Count;i++)
        {
            var entry=manifest.Entries[i];var data=payloads[i];
            if(data is null){if(File.Exists(entry.Target))File.Delete(entry.Target);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Target)!);
            var temporary=entry.Target+".rollback-write-"+Guid.NewGuid().ToString("N");
            try{WriteNew(temporary,data);File.Move(temporary,entry.Target,overwrite:true);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            afterReplacement?.Invoke(i);
        }
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static void WriteNew(string path,byte[] bytes)
    {using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);stream.Write(bytes);stream.Flush(true);}
}
