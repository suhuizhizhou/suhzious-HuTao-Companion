using System.Security.Cryptography;
using System.Text.Json;
using HuTao.Knowledge.Import;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using HuTao.Knowledge.Memory;
using HuTao.Dialogue.Storage;
using HuTao.Dialogue.Core;

namespace HuTao.Dialogue.Runtime;

/// <summary>Installs recoverable files, leaving activation blocked until a separate final authority handoff.</summary>
public static class KnowledgeRollbackInstallation
{
    public sealed record Pending(string Namespace,string Journal,string AuthorityHash);
    public sealed record LegacyAuthority(string Namespace,string RetiredDatabase,string Lineage,string InstallationJournal,string SqlAuthorityHash);
    public static void Complete(KnowledgeHostPaths paths,SqliteKnowledgeDatabase database,KnowledgeAccessContext access,string prepared,Action<string>? checkpoint=null)
    {
        var fence=new KnowledgeAuthorityFence(paths.Authority);
        using var activation=fence.Acquire(false);
        using var legacy=fence.RequireNoLegacyRuntime();using var sql=fence.RequireNoSqlRuntime();
        using var connection=database.Open();using var stopped=connection.BeginTransaction(deferred:false);
        var pendingPath=paths.Authority+".rollback-pending.json";
        var pending=JsonSerializer.Deserialize<Pending>(File.ReadAllBytes(pendingPath))??throw new InvalidDataException("Pending installation missing.");
        var root=Path.GetFullPath(prepared);var journal=Path.Combine(root,"installation-journal");
        var retired=paths.Authority+".retired-sql.json";
        var source=File.Exists(paths.Authority)?paths.Authority:retired;
        DemandAuthority(paths with {Authority=source},database,access);
        if(pending.Namespace!=access.MemoryNamespace || pending.Journal!=journal || pending.AuthorityHash!=HashFile(source))
            throw new InvalidDataException("Pending installation authority changed.");
        MemoryRollbackPackage.DemandCurrent(new(Path.Combine(root,"knowledge.db")),database,access);
        RollbackFileJournal.VerifyApplied(journal,Map(paths,root).Select(f=>f.Target).ToArray());
        var completed=new LegacyAuthority(access.MemoryNamespace,database.FilePath,KnowledgeRecovery.ReadLineage(database),journal,pending.AuthorityHash);
        // Commit the SQL fence before publishing legacy authority. A crash leaves pending
        // in place; retries validate the same installation and never reopen old writers.
        KnowledgeNamespaceRetirement.Retire(connection,stopped,access.MemoryNamespace,journal);
        stopped.Commit();
        var legacyPath=paths.Authority+".legacy-authority.json";
        if(File.Exists(legacyPath))
        {
            if(JsonSerializer.Deserialize<LegacyAuthority>(File.ReadAllBytes(legacyPath))!=completed)throw new InvalidDataException("Conflicting legacy authority.");
        }
        else WriteAtomicNew(legacyPath,completed);
        if(File.Exists(paths.Authority))File.Move(paths.Authority,retired,overwrite:false);
        checkpoint?.Invoke("authority-renamed");
        File.Delete(pendingPath);
    }
    public static void InstallFiles(KnowledgeHostPaths paths,SqliteKnowledgeDatabase database,KnowledgeAccessContext access,string prepared,Action<string>? checkpoint=null)
    {
        var fence=new KnowledgeAuthorityFence(paths.Authority);
        using var activation=fence.Acquire(false);
        using var legacy=fence.RequireNoLegacyRuntime();using var sql=fence.RequireNoSqlRuntime();
        var pendingPath=paths.Authority+".rollback-pending.json";
        if(File.Exists(pendingPath))throw new InvalidOperationException("Recover the pending installation first.");
        DemandAuthority(paths,database,access);
        using var connection=database.Open();using var stopped=connection.BeginTransaction(deferred:false);
        var root=Path.GetFullPath(prepared);
        using var materialLease=new ReadLease(new[]{"manifest.json","memory.json","memory.archive.jsonl","important-memories.json","chat-log.json","legacy-memory.json","chat-conversion.json"}.Select(name=>Path.Combine(root,name)));
        using var authorityLease=new ReadLease([paths.Authority]);
        using var logLease=paths.ChatLog is null?null:new ReadLease([paths.ChatLog]);
        var snapshotPath=Path.Combine(root,"knowledge.db");
        using var snapshotFile=new FileStream(snapshotPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        var snapshotDatabase=new SqliteKnowledgeDatabase(snapshotPath);
        var repository=new SqlConversationMemoryRepository(snapshotDatabase,access);
        var itemRepository=new SqlImportantItemRepository(snapshotDatabase,access);
        using var snapshotConnection=snapshotDatabase.Open();
        using var snapshotWrites=snapshotConnection.BeginTransaction(deferred:false);
        var manifest=JsonSerializer.Deserialize<MemoryRollbackManifest>(File.ReadAllBytes(Path.Combine(root,"manifest.json")))??throw new InvalidDataException("Manifest missing.");
        if(manifest.FormatVersion!=2 || manifest.Namespace!=access.MemoryNamespace || paths.ChatLog is null || paths.ImportantItems is null && manifest.ImportantItems>0)
            throw new InvalidDataException("Prepared materials do not match host targets.");
        foreach(var name in new[]{"knowledge.db","memory.json","memory.archive.jsonl","important-memories.json"})
            if(!manifest.FileHashes.TryGetValue(name,out var expected) || HashFile(Path.Combine(root,name))!=expected)throw new InvalidDataException("Prepared payload changed.");
        var conversion=JsonSerializer.Deserialize<HostRollbackConversion>(File.ReadAllBytes(Path.Combine(root,"chat-conversion.json")))??throw new InvalidDataException("Chat conversion missing.");
        if(conversion.Chat.Authority.Namespace!=access.MemoryNamespace || HashFile(paths.ChatLog)!=conversion.Chat.SourceSha256 ||
            HashFile(Path.Combine(root,"chat-log.json"))!=conversion.Chat.OutputSha256 || HashFile(Path.Combine(root,"legacy-memory.json"))!=conversion.LegacyMemorySha256)
            throw new InvalidDataException("Chat or legacy memory changed after preparation.");
        MemoryRollbackPackage.DemandCurrent(snapshotDatabase,database,access);
        MemoryJsonImporter.DemandLegacyCompatible(repository.LoadForMigration());
        var memoryReview=new MemoryJsonImporter(repository).Import(Path.Combine(root,"memory.json"));
        if(!memoryReview.AlreadyImported)throw new InvalidDataException("Prepared JSON differs from the SQL memory snapshot.");
        var important=JsonSerializer.Deserialize<List<ImportantItem>>(File.ReadAllBytes(Path.Combine(root,"important-memories.json")))??throw new InvalidDataException("Important items missing.");
        if(!important.OrderBy(i=>i.Id).SequenceEqual(itemRepository.Load().OrderBy(i=>i.Id)))
            throw new InvalidDataException("Prepared important items differ from SQL.");
        using var stampCommand=connection.CreateCommand();stampCommand.Transaction=stopped;
        stampCommand.CommandText="SELECT privacy_epoch FROM namespaces WHERE namespace_id=@n";stampCommand.Parameters.AddWithValue("@n",access.MemoryNamespace);
        var currentStamp=new ConversationPrivacyStamp(access.MemoryNamespace,Convert.ToInt64(stampCommand.ExecuteScalar()??throw new InvalidDataException("Privacy authority missing.")));
        var checkedChat=Path.Combine(root,".install-chat-check-"+Guid.NewGuid().ToString("N"));
        try
        {
            var regenerated=ChatLogRollback.Prepare(paths.ChatLog,checkedChat,currentStamp,conversion.Chat.SourceSha256);
            if(regenerated!=conversion.Chat)throw new InvalidDataException("Prepared chat conversion differs from current privacy rules.");
            var memory=JsonSerializer.Deserialize<MemoryFileState>(File.ReadAllBytes(Path.Combine(root,"memory.json")),MemoryFileState.Json)!;
            if(memory.ImportedChatEntries!=conversion.PreviousImportedChatEntries || conversion.ImportedChatEntries!=regenerated.Retained)
                throw new InvalidDataException("Prepared import cursor differs from the converted log.");
            memory.ImportedChatEntries=regenerated.Retained;
            if(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(memory,MemoryFileState.Json)))!=conversion.LegacyMemorySha256)
                throw new InvalidDataException("Prepared legacy memory differs from the authoritative conversion.");
        }
        finally{if(File.Exists(checkedChat))File.Delete(checkedChat);}
        var files=Map(paths,root);
        var journal=Path.Combine(root,"installation-journal");
        RollbackFileJournal.Create(journal,files);
        WriteAtomicNew(pendingPath,new Pending(access.MemoryNamespace,journal,HashFile(paths.Authority)));
        checkpoint?.Invoke("pending-written");
        // The journal now owns the verified source and original bytes. Release only the
        // destination log handle so its atomic replacement is possible on Windows.
        logLease?.Dispose();
        RollbackFileJournal.Apply(journal,files.Select(f=>f.Target).ToArray(),i=>checkpoint?.Invoke("file-replaced:"+i));
    }

    public static void RestoreFiles(KnowledgeHostPaths paths,SqliteKnowledgeDatabase database,KnowledgeAccessContext access,string prepared)
    {
        var fence=new KnowledgeAuthorityFence(paths.Authority);
        using var activation=fence.Acquire(false);
        using var legacy=fence.RequireNoLegacyRuntime();using var sql=fence.RequireNoSqlRuntime();
        var retired=paths.Authority+".retired-sql.json";
        var authoritySource=File.Exists(paths.Authority)?paths.Authority:retired;
        DemandAuthority(paths with {Authority=authoritySource},database,access);
        using var connection=database.Open();using var stopped=connection.BeginTransaction(deferred:false);
        var pendingPath=paths.Authority+".rollback-pending.json";
        var pending=JsonSerializer.Deserialize<Pending>(File.ReadAllBytes(pendingPath))??throw new InvalidDataException("Pending installation missing.");
        var root=Path.GetFullPath(prepared);var journal=Path.Combine(root,"installation-journal");
        if(pending.Namespace!=access.MemoryNamespace || pending.Journal!=journal || pending.AuthorityHash!=HashFile(authoritySource))
            throw new InvalidDataException("Pending installation differs from current authority or host mapping.");
        KnowledgeNamespaceRetirement.DemandMatching(connection,stopped,access.MemoryNamespace,journal);
        var legacyPath=paths.Authority+".legacy-authority.json";
        if(File.Exists(legacyPath))
        {
            var completed=JsonSerializer.Deserialize<LegacyAuthority>(File.ReadAllBytes(legacyPath));
            if(completed?.InstallationJournal!=journal || completed.SqlAuthorityHash!=pending.AuthorityHash)
                throw new InvalidDataException("Conflicting legacy authority during recovery.");
        }
        RollbackFileJournal.Restore(journal,Map(paths,root).Select(f=>f.Target).ToArray());
        if(!File.Exists(paths.Authority))File.Move(retired,paths.Authority,overwrite:false);
        if(File.Exists(legacyPath))
        {
            File.Delete(legacyPath);
        }
        KnowledgeNamespaceRetirement.Restore(connection,stopped,access.MemoryNamespace,journal);
        stopped.Commit();
        File.Delete(pendingPath);
    }
    private static void WriteAtomicNew<T>(string path,T value)
    {
        var temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,value);file.Flush(true);}
            File.Move(temporary,path,overwrite:false);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static List<(string Source,string Target)> Map(KnowledgeHostPaths paths,string root)
    {
        if(paths.ChatLog is null)throw new InvalidOperationException("Chat log mapping is required for this installation.");
        var files=new List<(string,string)>{(Path.Combine(root,"legacy-memory.json"),paths.Memory),(Path.Combine(root,"memory.archive.jsonl"),paths.Archive),(Path.Combine(root,"chat-log.json"),paths.ChatLog)};
        if(paths.ImportantItems is not null)files.Add((Path.Combine(root,"important-memories.json"),paths.ImportantItems));
        return files;
    }
    private static void DemandAuthority(KnowledgeHostPaths paths,SqliteKnowledgeDatabase database,KnowledgeAccessContext access)
    {
        using var document=JsonDocument.Parse(File.ReadAllBytes(paths.Authority));var marker=document.RootElement;
        if(marker.GetProperty("space").GetString()!=access.MemoryNamespace || marker.GetProperty("lineageId").GetString()!=KnowledgeRecovery.ReadLineage(database) ||
            !string.Equals(marker.GetProperty("database").GetString(),database.FilePath,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installation authority mismatch.");
    }
    private static string HashFile(string path)
    {
        // SQLite retains a read/write handle during its lock transaction. The caller's
        // file leases or SQLite transaction provide exclusion while this handle reads.
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    private sealed class ReadLease:IDisposable
    {
        private readonly List<FileStream> _handles=[];
        public ReadLease(IEnumerable<string> paths)
        {
            try{foreach(var path in paths)_handles.Add(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read));}
            catch{Dispose();throw;}
        }
        public void Dispose(){foreach(var handle in _handles)handle.Dispose();_handles.Clear();}
    }
}
