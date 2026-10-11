using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HuTao.Knowledge.Storage;

public sealed record KnowledgeBackupManifest(int FormatVersion,string Sha256,long DatabaseBytes,int SchemaVersion,long EvidenceRows,long MemoryRows,long ArchivedRows,long AssertionRows,long NamespaceRows);
public sealed record KnowledgeReconciledRestore(KnowledgeBackupManifest Original,KnowledgeBackupManifest Prepared,KnowledgeRecoveryReport Recovery);

/// <summary>Complete database snapshot, including archived sources and privacy state. Restore never activates a host.</summary>
public static class KnowledgeBackupPackage
{
    public static KnowledgeReconciledRestore RestoreWithDeletions(string package,string currentDatabase,string destination,CancellationToken ct=default)
    {
        var target=Path.GetFullPath(destination);DemandNew(target);
        var working=target+".reconcile-"+Guid.NewGuid().ToString("N");
        var prepared=target+".prepared-"+Guid.NewGuid().ToString("N");
        try
        {
            ct.ThrowIfCancellationRequested();
            var original=Restore(package,working);
            if(original.SchemaVersion<9)throw new InvalidDataException("Backup predates lineage tracking; use raw restore for inspection, not reconciled recovery.");
            var database=new SqliteKnowledgeDatabase(working);
            var recovery=KnowledgeRecovery.ReconcileDeletions(database,currentDatabase,ct);
            // Materialize all committed WAL changes before promoting the standalone database file.
            database.Backup(prepared);var manifest=Inspect(prepared);
            ct.ThrowIfCancellationRequested();DemandNew(target);File.Move(prepared,target,overwrite:false);
            return new(original,manifest,recovery);
        }
        finally{CleanupSnapshot(working);CleanupSnapshot(prepared);}
    }
    public static KnowledgeBackupManifest Create(SqliteKnowledgeDatabase database,string destination)
        =>CreateSnapshot(database.Backup,destination);
    public static KnowledgeBackupManifest CreateFromFile(string sourcePath,string destination)
    {
        if(!File.Exists(sourcePath))throw new FileNotFoundException("Source database missing.");
        return CreateSnapshot(snapshot=>SnapshotFile(sourcePath,snapshot),destination);
    }
    internal static void SnapshotFile(string sourcePath,string snapshot)
    {
            DemandNew(snapshot);
            using var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.GetFullPath(sourcePath),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
            using var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=snapshot,Pooling=false}.ToString());
            source.Open();target.Open();source.BackupDatabase(target);
    }
    private static KnowledgeBackupManifest CreateSnapshot(Action<string> backup,string destination)
    {
        var target=Path.GetFullPath(destination);DemandNew(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var snapshot=target+".snapshot-"+Guid.NewGuid().ToString("N");
        var temporary=target+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            backup(snapshot);
            var manifest=Inspect(snapshot);
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,leaveOpen:true))
                {
                    using(var file=File.OpenRead(snapshot))using(var entry=zip.CreateEntry("knowledge.db",CompressionLevel.NoCompression).Open())file.CopyTo(entry);
                    using var metadata=zip.CreateEntry("manifest.json",CompressionLevel.NoCompression).Open();JsonSerializer.Serialize(metadata,manifest);
                }
                stream.Flush(true);
            }
            File.Move(temporary,target,overwrite:false);return manifest;
        }
        finally{CleanupSnapshot(snapshot);if(File.Exists(temporary))File.Delete(temporary);}
    }

    public static KnowledgeBackupManifest Restore(string package,string destination)
    {
        var target=Path.GetFullPath(destination);DemandNew(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary=target+".restore-"+Guid.NewGuid().ToString("N");
        try
        {
            using var stream=File.OpenRead(package);using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
            if(zip.Entries.Count!=2 || zip.Entries.Count(e=>e.FullName=="knowledge.db")!=1 || zip.Entries.Count(e=>e.FullName=="manifest.json")!=1)
                throw new InvalidDataException("Unexpected backup entries.");
            var metadata=zip.GetEntry("manifest.json")!;
            if(metadata.Length>65536)throw new InvalidDataException("Manifest exceeds limit.");
            KnowledgeBackupManifest expected;
            using(var input=metadata.Open())expected=JsonSerializer.Deserialize<KnowledgeBackupManifest>(input)??throw new InvalidDataException("Missing manifest.");
            var payload=zip.GetEntry("knowledge.db")!;
            if(expected.FormatVersion!=1 || expected.DatabaseBytes<=0 || payload.Length!=expected.DatabaseBytes)throw new InvalidDataException("Unsupported or incomplete package.");
            using(var input=payload.Open())using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {input.CopyTo(file);file.Flush(true);}
            var actual=Inspect(temporary);
            if(actual!=expected)throw new InvalidDataException("Backup checksum or manifest mismatch.");
            DemandNew(target);File.Move(temporary,target,overwrite:false);return actual;
        }
        finally{CleanupSnapshot(temporary);}
    }
    private static void CleanupSnapshot(string path)
    {
        foreach(var owned in new[]{path,path+"-wal",path+"-shm"})if(File.Exists(owned))File.Delete(owned);
    }
    private static void DemandNew(string path)
    {
        if(File.Exists(path)||Directory.Exists(path)||File.Exists(path+"-wal")||File.Exists(path+"-shm"))throw new IOException("Destination and SQLite sidecars must not exist.");
    }
    internal static KnowledgeBackupManifest Inspect(string path)
    {
        using(var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
        {
            c.Open();using var command=c.CreateCommand();command.CommandText="PRAGMA integrity_check";
            if(command.ExecuteScalar() as string!="ok")throw new InvalidDataException("SQLite integrity check failed.");
            command.CommandText="PRAGMA foreign_key_check";using(var rows=command.ExecuteReader())if(rows.Read())throw new InvalidDataException("SQLite foreign keys invalid.");
            long Number(string sql){command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
            var version=checked((int)Number("SELECT MAX(version) FROM schema_migrations"));
            using var input=File.OpenRead(path);var hash=Convert.ToHexString(SHA256.HashData(input));
            return new(1,hash,input.Length,version,Number("SELECT count(*) FROM evidence_units"),Number("SELECT count(*) FROM memory_records"),Number("SELECT count(*) FROM memory_records WHERE archived=1"),Number("SELECT count(*) FROM assertions"),Number("SELECT count(*) FROM namespaces"));
        }
    }
}
