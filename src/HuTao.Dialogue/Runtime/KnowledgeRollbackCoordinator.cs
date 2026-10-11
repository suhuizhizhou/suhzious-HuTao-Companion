using System.Text.Json;
using HuTao.Knowledge.Import;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using HuTao.Dialogue.Storage;
using HuTao.Dialogue.Core;

namespace HuTao.Dialogue.Runtime;

public sealed record HostRollbackConversion(ChatLogRollbackReport Chat,int PreviousImportedChatEntries,
    int ImportedChatEntries,string LegacyMemorySha256);

/// <summary>Synchronously prepares a rollback while runtime registration and SQLite writers are excluded.</summary>
public static class KnowledgeRollbackCoordinator
{
    public static MemoryRollbackManifest Prepare(string authorityPath,SqliteKnowledgeDatabase database,
        KnowledgeAccessContext access,string package,string destination,string? chatLogPath=null,string? expectedChatSha256=null)
    {
        var fence=new KnowledgeAuthorityFence(Path.GetFullPath(authorityPath));
        using var activation=fence.Acquire(legacy:false);
        using var legacy=fence.RequireNoLegacyRuntime();
        using var sql=fence.RequireNoSqlRuntime();
        using var authority=JsonDocument.Parse(File.ReadAllBytes(authorityPath));
        var marker=authority.RootElement;
        if(!string.Equals(marker.GetProperty("database").GetString(),database.FilePath,StringComparison.OrdinalIgnoreCase) ||
            marker.GetProperty("space").GetString()!=access.MemoryNamespace ||
            marker.GetProperty("lineageId").GetString()!=KnowledgeRecovery.ReadLineage(database))
            throw new InvalidDataException("Rollback identity differs from the host SQL authority.");

        // BEGIN IMMEDIATE excludes even SQLite writers that do not use our runtime handles.
        // Keep this synchronous: the short activation lease is owned by this thread.
        using var connection=database.Open();
        using var stoppedWrites=connection.BeginTransaction(deferred:false);
        if(chatLogPath is null)
            return MemoryRollbackPackage.Prepare(package,access,destination,currentAuthority:database);
        var target=Path.GetFullPath(destination);
        if(File.Exists(target)||Directory.Exists(target))throw new IOException("Host rollback destination already exists.");
        var staging=Path.Combine(Path.GetDirectoryName(target)!,".host-rollback-"+Guid.NewGuid().ToString("N"));
        // Hold the actual source file against mutation/replacement until both artifacts are published.
        using var chatReadLock=new FileStream(chatLogPath,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            var manifest=MemoryRollbackPackage.Prepare(package,access,staging,currentAuthority:database);
            using var stampCommand=connection.CreateCommand();stampCommand.Transaction=stoppedWrites;
            stampCommand.CommandText="SELECT privacy_epoch FROM namespaces WHERE namespace_id=@n";
            stampCommand.Parameters.AddWithValue("@n",access.MemoryNamespace);
            var epoch=stampCommand.ExecuteScalar()??throw new InvalidDataException("Privacy authority is missing.");
            var report=ChatLogRollback.Prepare(chatLogPath,Path.Combine(staging,"chat-log.json"),
                new ConversationPrivacyStamp(access.MemoryNamespace,Convert.ToInt64(epoch)),expectedChatSha256);
            var memory=JsonSerializer.Deserialize<HuTao.Knowledge.Memory.MemoryFileState>(File.ReadAllBytes(Path.Combine(staging,"memory.json")),HuTao.Knowledge.Memory.MemoryFileState.Json)
                ??throw new InvalidDataException("Prepared memory missing.");
            var previousCursor=memory.ImportedChatEntries;
            // SQL's memory snapshot is authoritative. Historical log entries must not create
            // new memory IDs on first legacy startup; subsequent appended entries may import.
            memory.ImportedChatEntries=report.Retained;
            var legacyMemory=JsonSerializer.SerializeToUtf8Bytes(memory,HuTao.Knowledge.Memory.MemoryFileState.Json);
            using(var file=new FileStream(Path.Combine(staging,"legacy-memory.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {file.Write(legacyMemory);file.Flush(true);}
            var conversion=new HostRollbackConversion(report,previousCursor,report.Retained,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(legacyMemory)));
            using(var file=new FileStream(Path.Combine(staging,"chat-conversion.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(file,conversion);file.Flush(true);}
            Directory.Move(staging,target);
            return manifest;
        }
        finally
        {
            if(Directory.Exists(staging))
            {
                foreach(var name in new[]{"knowledge.db","knowledge.db-wal","knowledge.db-shm","memory.json","memory.archive.jsonl","important-memories.json","manifest.json","chat-log.json","chat-conversion.json","legacy-memory.json"})
                {var owned=Path.Combine(staging,name);if(File.Exists(owned))File.Delete(owned);}
                Directory.Delete(staging);
            }
        }
        // Disposal rolls back the empty lock transaction. The authority marker is unchanged;
        // publishing preparation materials does not grant a legacy writer activation rights.
    }
}
