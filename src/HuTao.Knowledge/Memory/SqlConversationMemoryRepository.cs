using System.Text.Json;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Memory;

public sealed class SqlConversationMemoryRepository : IConversationMemoryRepository
{
    private readonly SqliteKnowledgeDatabase _database;
    private readonly string _space;
    private readonly int _maxActiveRecords;
    public SqlConversationMemoryRepository(SqliteKnowledgeDatabase database, KnowledgeAccessContext access,int maxActiveRecords=20000)
    {
        _database=database;
        if(maxActiveRecords<100)throw new ArgumentOutOfRangeException(nameof(maxActiveRecords));
        _maxActiveRecords=maxActiveRecords;
        _space=access.MemoryNamespace;
        database.EnsureNamespace(_space);
    }

    public MemoryRepositorySnapshot Load()=>LoadCore(false,int.MaxValue);
    public MemoryRepositorySnapshot LoadArchived(int limit=100)=>LoadCore(true,Math.Clamp(limit,1,1000));
    public MemoryRepositorySnapshot LoadForLegacyExport()=>LoadCore(false,int.MaxValue,true);
    public MemoryRepositorySnapshot LoadForMigration()=>LoadCore(null,int.MaxValue);
    private MemoryRepositorySnapshot LoadCore(bool? archived,int limit,bool requireNoArchive=false)
    {
        using var c=_database.Open();
        using var transaction=c.BeginTransaction(deferred:true);
        if(requireNoArchive)
        {
            using var archive=Command(c,transaction,"SELECT 1 FROM memory_records m JOIN evidence_units e ON e.namespace_id=m.namespace_id AND e.evidence_id=m.memory_id AND e.revision=m.evidence_revision WHERE m.namespace_id=@n AND m.archived=1 AND e.deleted_at IS NULL LIMIT 1",("@n",_space));
            if(archive.ExecuteScalar() is not null)
                throw new InvalidOperationException("Legacy export would omit archived SQL memory; use a full SQL backup until versioned archive export is available.");
        }
        var state=new MemoryFileState();
        var archivedIds=new HashSet<string>(StringComparer.Ordinal);
        var evidenceRevisions=new Dictionary<string,long>(StringComparer.Ordinal);
        long revision=0;
        using(var cmd=Command(c,transaction,"SELECT imported_chat_entries,last_consolidated_at,state_revision FROM memory_state WHERE namespace_id=@n",("@n",_space)))
        using(var row=cmd.ExecuteReader())
            if(row.Read()) { state.ImportedChatEntries=row.GetInt32(0); state.LastConsolidatedAt=Time(row,1); revision=row.GetInt64(2); }
        using(var cmd=Command(c,transaction,"""
            SELECT m.memory_id,m.kind,m.importance,m.fingerprint,m.valid_to,m.superseded_by,m.access_count,
                   m.last_accessed_at,m.consolidated_at,e.text,e.speaker,e.observed_at,e.metadata_json,m.evidence_revision,m.archived
            FROM memory_records m JOIN evidence_units e ON e.namespace_id=m.namespace_id AND e.evidence_id=m.memory_id AND e.revision=m.evidence_revision
            WHERE m.namespace_id=@n AND (@archived=-1 OR m.archived=@archived) AND e.deleted_at IS NULL ORDER BY e.rowid LIMIT @limit
            """,("@n",_space),("@archived",archived is null?-1:archived.Value?1:0),("@limit",limit)))
        using(var rows=cmd.ExecuteReader())
            while(rows.Read())
            {
                var record=new MemoryRecord {
                    Id=rows.GetString(0),Kind=Enum.Parse<MemoryKind>(rows.GetString(1)),Importance=rows.GetDouble(2),Fingerprint=rows.GetString(3),
                    ValidUntil=Time(rows,4),SupersededById=rows.IsDBNull(5)?null:rows.GetString(5),AccessCount=rows.GetInt32(6),
                    LastAccessedAt=Time(rows,7),ConsolidatedAt=Time(rows,8),Text=rows.GetString(9),Speaker=rows.GetString(10),
                    ObservedAt=Time(rows,11)??throw new InvalidDataException("Memory observed time missing."),
                    RecordedAt=JsonSerializer.Deserialize<DateTimeOffset>(rows.GetString(12))
                };
                using var sources=Command(c,transaction,"SELECT source_evidence_id,source_revision FROM memory_derivations WHERE namespace_id=@n AND memory_id=@id AND memory_revision=@r ORDER BY rowid",("@n",_space),("@id",record.Id),("@r",rows.GetInt64(13)));
                using var sourceRows=sources.ExecuteReader();
                while(sourceRows.Read()) {record.Sources.Add(sourceRows.GetString(0));record.SourceRevisions.Add(sourceRows.GetString(0),sourceRows.GetInt64(1));}
                state.Records.Add(record);
                evidenceRevisions.Add(record.Id,rows.GetInt64(13));
                if(rows.GetInt32(14)!=0)archivedIds.Add(record.Id);
            }
        transaction.Commit();
        return new(state,revision){ArchivedIds=archivedIds,EvidenceRevisions=evidenceRevisions};
    }

    public bool HasCommitted(MemoryConsolidationRun run)
    {
        using var c=_database.Open();
        using var cmd=Command(c,null,"SELECT 1 FROM consolidation_runs WHERE namespace_id=@n AND input_hash=@h AND prompt_version=@p AND status='committed'",("@n",_space),("@h",run.InputHash),("@p",run.PromptVersion));
        return cmd.ExecuteScalar() is not null;
    }

    public long Save(MemoryFileState state,long expectedRevision,MemoryConsolidationRun? run=null)
        =>SaveCore(state,expectedRevision,run,null);
    public long ImportSnapshot(MemoryFileState state,IReadOnlySet<string> archivedIds)
        =>SaveCore(state,0,null,archivedIds);
    private long SaveCore(MemoryFileState state,long expectedRevision,MemoryConsolidationRun? run,IReadOnlySet<string>? importedArchives)
    {
        if(state.Version!=1 || state.ImportedChatEntries<0 || state.Records.Select(r=>r.Id).Distinct().Count()!=state.Records.Count)
            throw new InvalidDataException("Invalid memory snapshot.");
        return _database.Write((c,t)=>{
            Execute(c,t,"INSERT OR IGNORE INTO memory_state(namespace_id) VALUES(@n)",("@n",_space));
            using(var version=Command(c,t,"SELECT state_revision FROM memory_state WHERE namespace_id=@n",("@n",_space)))
                if(Convert.ToInt64(version.ExecuteScalar())!=expectedRevision)throw new InvalidOperationException("Stale memory writer; reload required.");
            if(importedArchives is not null)
            {
                using var existing=Command(c,t,"SELECT 1 FROM memory_records WHERE namespace_id=@n LIMIT 1",("@n",_space));
                if(existing.ExecuteScalar() is not null)throw new InvalidOperationException("Import requires an empty memory target.");
                if(importedArchives.Except(state.Records.Select(r=>r.Id),StringComparer.Ordinal).Any())throw new InvalidDataException("Unknown archived memory ID.");
            }
            var refs=new Dictionary<string,EvidenceRef>(StringComparer.Ordinal);
            foreach(var record in state.Records)
            {
                if(!Enum.IsDefined(record.Kind))throw new InvalidDataException("Unknown memory kind.");
                refs.Add(record.Id,SqlEvidenceRepository.Put(c,t,_space,new(record.Id,record.Text,record.Speaker,"memory:"+record.Id,"memory",ObservedAt:record.ObservedAt,MetadataJson:JsonSerializer.Serialize(record.RecordedAt)),"memory"));
                if(record.Kind==MemoryKind.Turn && record.Speaker=="user")
                    Execute(c,t,"INSERT OR IGNORE INTO turn_ingestion(namespace_id,evidence_id,evidence_revision,created_at) VALUES(@n,@id,@r,@at)",
                        ("@n",_space),("@id",record.Id),("@r",refs[record.Id].Revision),("@at",Now()));
                Execute(c,t,"""
                    INSERT INTO memory_records(namespace_id,memory_id,evidence_revision,kind,importance,fingerprint,valid_from,valid_to,superseded_by,access_count,last_accessed_at,consolidated_at)
                    VALUES(@n,@id,@r,@kind,@importance,@fingerprint,@from,@to,@superseded,@access,@last,@consolidated)
                    ON CONFLICT(namespace_id,memory_id) DO UPDATE SET evidence_revision=excluded.evidence_revision,kind=excluded.kind,
                    importance=excluded.importance,fingerprint=excluded.fingerprint,valid_from=excluded.valid_from,valid_to=excluded.valid_to,
                    superseded_by=excluded.superseded_by,access_count=excluded.access_count,last_accessed_at=excluded.last_accessed_at,consolidated_at=excluded.consolidated_at
                    """,("@n",_space),("@id",record.Id),("@r",refs[record.Id].Revision),("@kind",record.Kind.ToString()),("@importance",record.Importance),
                    ("@fingerprint",record.Fingerprint),("@from",record.ObservedAt.ToUnixTimeMilliseconds()),("@to",record.ValidUntil?.ToUnixTimeMilliseconds()),
                    ("@superseded",record.SupersededById),("@access",record.AccessCount),("@last",record.LastAccessedAt?.ToUnixTimeMilliseconds()),("@consolidated",record.ConsolidatedAt?.ToUnixTimeMilliseconds()));
            }
            foreach(var record in state.Records)
            {
                var existingSources=new Dictionary<string,long>(StringComparer.Ordinal);
                using(var existing=Command(c,t,"SELECT source_evidence_id,source_revision FROM memory_derivations WHERE namespace_id=@n AND memory_id=@id AND memory_revision=@r",("@n",_space),("@id",record.Id),("@r",refs[record.Id].Revision)))
                using(var rows=existing.ExecuteReader())while(rows.Read())existingSources.Add(rows.GetString(0),rows.GetInt64(1));
                var selectedSources=new Dictionary<string,long>(StringComparer.Ordinal);
                foreach(var source in record.Sources.Distinct(StringComparer.Ordinal))
                {
                    EvidenceRef reference;
                    if(record.SourceRevisions.TryGetValue(source,out var pinned))
                    {
                        using var valid=Command(c,t,"""
                            SELECT 1 FROM evidence_units e WHERE namespace_id=@n AND evidence_id=@id AND revision=@r AND deleted_at IS NULL
                              AND (@existing=1 OR NOT EXISTS(SELECT 1 FROM evidence_units newer WHERE newer.namespace_id=e.namespace_id AND newer.evidence_id=e.evidence_id AND newer.revision>e.revision))
                            """,("@n",_space),("@id",source),("@r",pinned),("@existing",existingSources.GetValueOrDefault(source)==pinned?1:0));
                        if(valid.ExecuteScalar() is null)throw new InvalidDataException("Memory source revision changed before commit.");
                        reference=new(_space,source,pinned);
                    }
                    else if(existingSources.TryGetValue(source,out var existingRevision))reference=new(_space,source,existingRevision);
                    else if(!refs.TryGetValue(source,out reference!))
                    {
                        // Raw turns may depend on an explicit important item created in the same authorized interaction.
                        if(record.Kind!=MemoryKind.Turn || !source.StartsWith("important:",StringComparison.Ordinal))throw new InvalidDataException($"Unresolved memory source: {source}");
                        using var current=Command(c,t,"""
                            SELECT revision FROM evidence_units WHERE namespace_id=@n AND evidence_id=@id AND deleted_at IS NULL
                            ORDER BY revision DESC LIMIT 1
                            """,("@n",_space),("@id",source));
                        var revision=current.ExecuteScalar();if(revision is null)throw new InvalidDataException("Important item source is no longer available.");
                        reference=new(_space,source,Convert.ToInt64(revision));
                    }
                    selectedSources.Add(source,reference.Revision);
                }
                if(existingSources.Count>0 && (existingSources.Count!=selectedSources.Count || existingSources.Any(s=>selectedSources.GetValueOrDefault(s.Key)!=s.Value)))
                    throw new InvalidDataException("Existing memory provenance cannot be rewritten without a new evidence revision.");
                if(record.Kind!=MemoryKind.Turn)
                {
                    var attributable=false;
                    foreach(var source in selectedSources)
                    {
                        using var speaker=Command(c,t,"SELECT speaker FROM evidence_units WHERE namespace_id=@n AND evidence_id=@id AND revision=@r AND deleted_at IS NULL",
                            ("@n",_space),("@id",source.Key),("@r",source.Value));
                        var value=speaker.ExecuteScalar() as string;
                        attributable|=value=="user" || (record.Kind==MemoryKind.Commitment && value=="assistant");
                    }
                    if(!attributable)throw new InvalidDataException("Derived memory lacks an attributable source.");
                }
                foreach(var source in selectedSources)
                    Execute(c,t,"INSERT OR IGNORE INTO memory_derivations VALUES(@n,@id,@r,@source,@sr)",("@n",_space),("@id",record.Id),("@r",refs[record.Id].Revision),("@source",source.Key),("@sr",source.Value));
            }
            if(importedArchives is not null)
            {
                foreach(var id in importedArchives)
                    Execute(c,t,"UPDATE memory_records SET archived=1 WHERE namespace_id=@n AND memory_id=@id",("@n",_space),("@id",id));
            }
            else Execute(c,t,"""
                UPDATE memory_records SET archived=1 WHERE namespace_id=@n AND memory_id IN (
                  SELECT m.memory_id FROM memory_records m WHERE m.namespace_id=@n AND m.archived=0 AND m.kind='Turn'
                    AND NOT EXISTS(SELECT 1 FROM memory_derivations d JOIN memory_records derived
                      ON derived.namespace_id=d.namespace_id AND derived.memory_id=d.memory_id AND derived.evidence_revision=d.memory_revision
                      WHERE d.namespace_id=m.namespace_id AND d.source_evidence_id=m.memory_id AND derived.archived=0 AND derived.kind<>'Turn')
                  ORDER BY (m.superseded_by IS NOT NULL) DESC,(m.valid_to IS NOT NULL AND m.valid_to<@now) DESC,m.importance,m.valid_from,m.memory_id
                  LIMIT MAX(0,(SELECT count(*) FROM memory_records WHERE namespace_id=@n AND archived=0)-@max))
                """,("@n",_space),("@now",Now()),("@max",_maxActiveRecords));
            Execute(c,t,"UPDATE memory_state SET imported_chat_entries=@cursor,last_consolidated_at=@at,state_revision=state_revision+1 WHERE namespace_id=@n",("@cursor",state.ImportedChatEntries),("@at",state.LastConsolidatedAt?.ToUnixTimeMilliseconds()),("@n",_space));
            if(run is not null)
                Execute(c,t,"INSERT INTO consolidation_runs VALUES(@id,@n,@h,@model,@p,'committed',@cursor)",
                    ("@id",Hash(_space+"\u001f"+run.InputHash+"\u001f"+run.PromptVersion)),("@n",_space),("@h",run.InputHash),("@model",run.Model),("@p",run.PromptVersion),("@cursor",state.LastConsolidatedAt?.ToUnixTimeMilliseconds().ToString()));
            return expectedRevision+1;
        });
    }
    private static DateTimeOffset? Time(Microsoft.Data.Sqlite.SqliteDataReader row,int index)=>row.IsDBNull(index)?null:DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(index));
}
