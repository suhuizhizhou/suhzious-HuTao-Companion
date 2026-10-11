using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HuTao.Knowledge.Memory;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;

namespace HuTao.Knowledge.Import;

public sealed record MemoryRollbackManifest(int FormatVersion,string Namespace,long StateRevision,
    int ActiveRecords,int ArchivedRecords,KnowledgeBackupManifest Database,
    IReadOnlyDictionary<string,string> FileHashes)
{
    public int ImportantItems {get;init;}
}

/// <summary>Reviewable snapshot only. Does not revoke SQL authority or activate legacy writers.</summary>
public static class MemoryRollbackPackage
{
    /// <summary>Validate and stage in a new directory; never installs files into an active host.</summary>
    public static MemoryRollbackManifest Prepare(string package,KnowledgeAccessContext access,string destination,
        long maxUncompressedBytes=16L*1024*1024*1024,SqliteKnowledgeDatabase? currentAuthority=null)
    {
        if(maxUncompressedBytes<1)throw new ArgumentOutOfRangeException(nameof(maxUncompressedBytes));
        var target=Path.GetFullPath(destination);
        if(File.Exists(target)||Directory.Exists(target))throw new IOException("Preparation destination already exists.");
        var parent=Path.GetDirectoryName(target)!;Directory.CreateDirectory(parent);
        var staging=Path.Combine(parent,".rollback-prepare-"+Guid.NewGuid().ToString("N"));
        string[] payloads=["knowledge.db","memory.json","memory.archive.jsonl","important-memories.json"];
        string[] names=[..payloads,"manifest.json"];
        Directory.CreateDirectory(staging);
        try
        {
            using(var input=File.OpenRead(package))
            using(var zip=new ZipArchive(input,ZipArchiveMode.Read))
            {
                if(zip.Entries.Count!=names.Length || names.Any(name=>zip.Entries.Count(e=>e.FullName==name)!=1))
                    throw new InvalidDataException("Unexpected, duplicate, or missing rollback entries.");
                long remaining=maxUncompressedBytes;
                foreach(var entry in zip.Entries)
                {
                    if(entry.Length<0 || entry.Length>remaining || entry.FullName=="manifest.json" && entry.Length>65536 ||
                        entry.FullName!="knowledge.db" && entry.Length>256L*1024*1024)
                        throw new InvalidDataException("Rollback payload exceeds preparation limits.");
                    remaining-=entry.Length;
                }
                foreach(var name in names)
                {
                    var entry=zip.GetEntry(name)!;
                    using var source=entry.Open();
                    using var file=new FileStream(Path.Combine(staging,name),FileMode.CreateNew,FileAccess.Write,FileShare.None);
                    var buffer=new byte[81920];long count=0;int read;
                    while((read=source.Read(buffer))>0)
                    {
                        count+=read;
                        if(count>entry.Length)throw new InvalidDataException("Rollback payload length mismatch.");
                        file.Write(buffer,0,read);
                    }
                    if(count!=entry.Length)throw new InvalidDataException("Incomplete rollback payload.");
                    file.Flush(true);
                }
            }
            var manifest=JsonSerializer.Deserialize<MemoryRollbackManifest>(File.ReadAllBytes(Path.Combine(staging,"manifest.json")))
                ??throw new InvalidDataException("Missing rollback manifest.");
            if(manifest.FormatVersion!=2 || manifest.Namespace!=access.MemoryNamespace || manifest.StateRevision<0 || manifest.ImportantItems<0 ||
                manifest.ActiveRecords<0 || manifest.ArchivedRecords<0 || manifest.Database is null ||
                manifest.FileHashes is null || manifest.FileHashes.Count!=payloads.Length || payloads.Any(p=>!manifest.FileHashes.ContainsKey(p)))
                throw new InvalidDataException("Invalid rollback identity or manifest.");
            foreach(var name in payloads)
            {
                using var file=File.OpenRead(Path.Combine(staging,name));
                if(Convert.ToHexString(SHA256.HashData(file))!=manifest.FileHashes[name])
                    throw new InvalidDataException("Rollback payload hash mismatch.");
            }
            var databasePath=Path.Combine(staging,"knowledge.db");
            if(KnowledgeBackupPackage.Inspect(databasePath)!=manifest.Database)
                throw new InvalidDataException("Rollback database differs from manifest.");
            var restoredDatabase=new SqliteKnowledgeDatabase(databasePath);
            var repository=new SqlConversationMemoryRepository(restoredDatabase,access);
            var snapshot=repository.LoadForMigration();
            MemoryJsonImporter.DemandLegacyCompatible(snapshot);
            var review=new MemoryJsonImporter(repository).Import(Path.Combine(staging,"memory.json"));
            if(!review.AlreadyImported || snapshot.Revision!=manifest.StateRevision ||
                review.ArchivedRecords!=manifest.ArchivedRecords || review.Records-review.ArchivedRecords!=manifest.ActiveRecords)
                throw new InvalidDataException("Rollback JSON and SQL do not describe the same state.");
            var important=JsonSerializer.Deserialize<List<ImportantItem>>(File.ReadAllBytes(Path.Combine(staging,"important-memories.json")))
                ??throw new InvalidDataException("Missing important items.");
            var sqlImportant=new SqlImportantItemRepository(restoredDatabase,access).Load();
            if(important.Count!=manifest.ImportantItems || !important.OrderBy(i=>i.Id).SequenceEqual(sqlImportant.OrderBy(i=>i.Id)))
                throw new InvalidDataException("Rollback important items differ from SQL.");
            if(KnowledgeBackupPackage.Inspect(databasePath)!=manifest.Database)
                throw new InvalidDataException("Preparation changed the database; regenerate with the current schema.");
            if(currentAuthority is not null)
                DemandCurrent(restoredDatabase,currentAuthority,access);
            Directory.Move(staging,target);
            return manifest;
        }
        finally
        {
            // Fixed names under the uniquely owned staging directory; no recursive deletion.
            if(Directory.Exists(staging))
            {
                foreach(var name in names.Concat(new[]{"knowledge.db-wal","knowledge.db-shm"}))
                {
                    var owned=Path.Combine(staging,name);if(File.Exists(owned))File.Delete(owned);
                }
                Directory.Delete(staging);
            }
        }
    }

    /// <summary>Point-in-time freshness check. Final activation must hold the host's writer exclusion across this check and installation.</summary>
    public static void DemandCurrent(SqliteKnowledgeDatabase prepared,SqliteKnowledgeDatabase authority,KnowledgeAccessContext access)
    {
        var packaged=ReadAuthorityStamp(prepared,access.MemoryNamespace);
        var current=ReadAuthorityStamp(authority,access.MemoryNamespace);
        if(packaged!=current)
            throw new InvalidOperationException("Rollback snapshot is not current; stop writers and regenerate from the current authority.");
    }

    private sealed record AuthorityStamp(string Lineage,long PrivacyEpoch,long CommitSequence,long MemoryRevision,string TombstoneHash,string ContentHash);
    private static AuthorityStamp ReadAuthorityStamp(SqliteKnowledgeDatabase database,string space)
    {
        using var connection=database.Open();using var transaction=connection.BeginTransaction(deferred:true);
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT COUNT(*) FROM recovery_gate";
        if(Convert.ToInt64(command.ExecuteScalar())!=0)throw new InvalidOperationException("Pending recovery cannot authorize legacy rollback.");
        command.CommandText="""
            SELECT d.lineage_id,n.privacy_epoch,s.commit_seq,m.state_revision
            FROM namespaces n JOIN namespace_state s USING(namespace_id)
            JOIN memory_state m USING(namespace_id) CROSS JOIN database_identity d
            WHERE n.namespace_id=@n AND d.singleton=1
            """;
        command.Parameters.AddWithValue("@n",space);
        string lineage;long privacy,sequence,revision;
        using(var rows=command.ExecuteReader())
        {
            if(!rows.Read())throw new InvalidDataException("Rollback authority namespace is missing.");
            lineage=rows.GetString(0);privacy=rows.GetInt64(1);sequence=rows.GetInt64(2);revision=rows.GetInt64(3);
        }
        command.CommandText="SELECT evidence_id FROM deletion_tombstones WHERE namespace_id=@n ORDER BY evidence_id COLLATE BINARY";
        var deleted=new List<string>();using(var rows=command.ExecuteReader())while(rows.Read())deleted.Add(rows.GetString(0));
        var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(deleted)));
        using var content=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Fixed authoritative tables; projection checkpoints and worker leases are not memory content.
        (string Table,string Order)[] tables=[
            ("sources","source_id"),("source_revisions","source_id,revision"),
            ("evidence_units","evidence_id,revision"),("memory_records","memory_id"),
            ("memory_derivations","memory_id,memory_revision,source_evidence_id,source_revision"),
            ("memory_state","namespace_id"),("important_items","item_id"),
            ("entities","entity_id"),("entity_aliases","alias,entity_id,source_evidence_id,source_revision"),
            ("mentions","evidence_id,evidence_revision,entity_id,start_utf16"),
            ("assertions","assertion_id,revision"),
            ("assertion_evidence","assertion_id,assertion_revision,evidence_id,evidence_revision,start_utf16"),
            ("consolidation_runs","run_id"),("deletion_tombstones","evidence_id")];
        foreach(var (table,order) in tables)
        {
            content.AppendData(JsonSerializer.SerializeToUtf8Bytes(table));content.AppendData("\n"u8);
            command.CommandText=$"SELECT * FROM {table} WHERE namespace_id=@n ORDER BY {order}";
            using var rows=command.ExecuteReader();
            while(rows.Read())
            {
                var values=new object?[rows.FieldCount];
                for(var i=0;i<values.Length;i++)values[i]=rows.IsDBNull(i)?null:rows.GetValue(i);
                content.AppendData(JsonSerializer.SerializeToUtf8Bytes(values));content.AppendData("\n"u8);
            }
        }
        var contentHash=Convert.ToHexString(content.GetHashAndReset());
        transaction.Commit();return new(lineage,privacy,sequence,revision,hash,contentHash);
    }

    public static MemoryRollbackManifest Create(SqliteKnowledgeDatabase database,KnowledgeAccessContext access,string destination)
    {
        var target=Path.GetFullPath(destination);
        if(File.Exists(target)||Directory.Exists(target))throw new IOException("Rollback destination already exists.");
        var parent=Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var snapshot=Path.Combine(parent,".rollback-snapshot-"+Guid.NewGuid().ToString("N")+".db");
        var temporary=Path.Combine(parent,".rollback-package-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            database.Backup(snapshot);
            var frozen=new SqliteKnowledgeDatabase(snapshot);
            using(var connection=frozen.Open())
            using(var command=connection.CreateCommand())
            {
                command.CommandText="SELECT 1 FROM memory_state WHERE namespace_id=@n";
                command.Parameters.AddWithValue("@n",access.MemoryNamespace);
                if(command.ExecuteScalar() is null)throw new InvalidOperationException("No persisted memory state for rollback namespace.");
            }
            var memory=new SqlConversationMemoryRepository(frozen,access).LoadForMigration();
            MemoryJsonImporter.DemandLegacyCompatible(memory);
            var active=new MemoryFileState {
                Version=memory.State.Version,ImportedChatEntries=memory.State.ImportedChatEntries,
                LastConsolidatedAt=memory.State.LastConsolidatedAt,
                Records=memory.State.Records.Where(r=>!memory.ArchivedIds.Contains(r.Id)).ToList()
            };
            var json=JsonSerializer.SerializeToUtf8Bytes(active,MemoryFileState.Json);
            var compact=new JsonSerializerOptions(MemoryFileState.Json){WriteIndented=false};
            var archive=System.Text.Encoding.UTF8.GetBytes(string.Concat(memory.State.Records
                .Where(r=>memory.ArchivedIds.Contains(r.Id)).Select(r=>JsonSerializer.Serialize(r,compact)+"\n")));
            var importantItems=new SqlImportantItemRepository(frozen,access).Load();
            var importantJson=JsonSerializer.SerializeToUtf8Bytes(importantItems);
            // This is the same fixed SQL snapshot from which both JSON payloads were derived.
            var backup=KnowledgeBackupPackage.Inspect(snapshot);
            var hashes=new Dictionary<string,string> {
                ["knowledge.db"]=backup.Sha256,
                ["memory.json"]=Convert.ToHexString(SHA256.HashData(json)),
                ["memory.archive.jsonl"]=Convert.ToHexString(SHA256.HashData(archive)),
                ["important-memories.json"]=Convert.ToHexString(SHA256.HashData(importantJson))
            };
            var manifest=new MemoryRollbackManifest(2,access.MemoryNamespace,memory.Revision,
                active.Records.Count,memory.ArchivedIds.Count,backup,hashes){ImportantItems=importantItems.Count};
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                using(var zip=new ZipArchive(output,ZipArchiveMode.Create,leaveOpen:true))
                {
                    using(var entry=zip.CreateEntry("knowledge.db",CompressionLevel.NoCompression).Open())
                    using(var source=File.OpenRead(snapshot))source.CopyTo(entry);
                    using(var entry=zip.CreateEntry("memory.json").Open())entry.Write(json);
                    using(var entry=zip.CreateEntry("memory.archive.jsonl").Open())entry.Write(archive);
                    using(var entry=zip.CreateEntry("important-memories.json").Open())entry.Write(importantJson);
                    using(var entry=zip.CreateEntry("manifest.json").Open())JsonSerializer.Serialize(entry,manifest);
                }
                output.Flush(true);
            }
            File.Move(temporary,target,overwrite:false);
            return manifest;
        }
        finally
        {
            foreach(var owned in new[]{snapshot,snapshot+"-wal",snapshot+"-shm",temporary})
                if(File.Exists(owned))File.Delete(owned);
        }
    }
}
