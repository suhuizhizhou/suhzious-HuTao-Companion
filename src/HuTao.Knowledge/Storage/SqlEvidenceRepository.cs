using System.Text.Json;
using HuTao.Knowledge.Rag;
using HuTao.Knowledge.Retrieval;
using Microsoft.Data.Sqlite;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Storage;

public sealed record EvidenceWrite(string Id, string Text, string Speaker, string SourceId,
    string SourceKind, string ChapterId = "", int Sequence = 0, int Variant = 0,
    DateTimeOffset? ObservedAt = null, string MetadataJson = "{}");
public sealed record KnowledgeReadStamp(long CommitSequence,long PrivacyEpoch);

public sealed class SqlEvidenceRepository(SqliteKnowledgeDatabase database)
{
    // Bounded provenance walk in the same SQL read snapshot. Repeated paths count toward the bound;
    // uncertain/oversized dependency graphs fail closed rather than returning stale derived text.
    internal const string ProvenancePredicate="""
        (WITH RECURSIVE dependencies(id,revision,depth,path,cycle) AS (
          SELECT e.evidence_id,e.revision,0,'|'||hex(e.evidence_id)||':'||e.revision||'|',0
          UNION ALL
          SELECT d.source_evidence_id,d.source_revision,p.depth+1,
            p.path||hex(d.source_evidence_id)||':'||d.source_revision||'|',
            instr(p.path,'|'||hex(d.source_evidence_id)||':'||d.source_revision||'|')>0
          FROM dependencies p JOIN memory_derivations d ON d.namespace_id=e.namespace_id AND d.memory_id=p.id AND d.memory_revision=p.revision
          WHERE p.cycle=0 AND p.depth<64 LIMIT 513)
        SELECT COUNT(*)<513 AND NOT EXISTS(
          SELECT 1 FROM dependencies d WHERE d.cycle=1 OR d.depth>=64 OR NOT EXISTS(
            SELECT 1 FROM evidence_units source WHERE source.namespace_id=e.namespace_id AND source.evidence_id=d.id AND source.revision=d.revision
              AND source.deleted_at IS NULL AND source.recorded_at<=@known
              AND NOT EXISTS(SELECT 1 FROM deletion_tombstones z WHERE z.namespace_id=source.namespace_id AND z.evidence_id=source.evidence_id)
              AND NOT EXISTS(SELECT 1 FROM evidence_units newer WHERE newer.namespace_id=source.namespace_id AND newer.evidence_id=source.evidence_id
                AND newer.revision>source.revision AND newer.recorded_at<=@known AND (n.domain='memory' OR newer.corpus_version=n.active_corpus_version))))
        FROM dependencies)
        """;
    public SqliteKnowledgeDatabase Database => database;
    public int RebuildSearchIndex(KnowledgeAccessContext access,string space,CancellationToken ct=default)
    {
        access.Demand(space);
        return database.Write((c,t)=>{
            Execute(c,t,"DELETE FROM evidence_fts WHERE rowid IN (SELECT rowid FROM evidence_search_map WHERE namespace_id=@n)",("@n",space));
            using var cmd=Command(c,t,"""
                SELECT m.rowid,e.speaker,e.text FROM evidence_search_map m JOIN evidence_units e
                ON e.namespace_id=m.namespace_id AND e.evidence_id=m.evidence_id AND e.revision=m.revision
                WHERE m.namespace_id=@n AND e.deleted_at IS NULL ORDER BY m.rowid
                """,("@n",space));
            using var rows=cmd.ExecuteReader();var count=0;
            while(rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                Execute(c,t,"INSERT INTO evidence_fts(rowid,tokens) VALUES(@id,@tokens)",("@id",rows.GetInt64(0)),("@tokens",Tokens(rows.GetString(1)+" "+rows.GetString(2))));
                count++;
            }
            return count;
        },ct);
    }
    public EvidenceRef Put(string space, EvidenceWrite evidence, string version = "memory")
    {
        database.EnsureNamespace(space);
        return database.Write((c,t) => Put(c,t,space,evidence,version));
    }
    internal static EvidenceRef Put(SqliteConnection c, SqliteTransaction t, string space, EvidenceWrite evidence, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.Text);
        // SQLite stores millisecond timestamps; hash the persisted precision so reload/save cannot invent a revision.
        if(evidence.ObservedAt is { } observed)evidence=evidence with {ObservedAt=DateTimeOffset.FromUnixTimeMilliseconds(observed.ToUnixTimeMilliseconds())};
        using (var tombstone = Command(c,t,"SELECT 1 FROM deletion_tombstones WHERE namespace_id=@n AND evidence_id=@id",("@n",space),("@id",evidence.Id)))
            if (tombstone.ExecuteScalar() is not null) throw new InvalidOperationException("Deleted evidence cannot be reimported.");
        var hash = Hash(JsonSerializer.Serialize(evidence));
        using (var existing=Command(c,t,"SELECT revision,text_hash FROM evidence_units WHERE namespace_id=@n AND evidence_id=@id AND corpus_version=@v ORDER BY revision DESC LIMIT 1",("@n",space),("@id",evidence.Id),("@v",version)))
        using (var row=existing.ExecuteReader())
        {
            if (row.Read())
            {
                if (row.GetString(1)==hash) return new(space,evidence.Id,row.GetInt64(0));
                if (space.StartsWith("corpus:",StringComparison.Ordinal)) throw new InvalidDataException("Conflicting evidence ID in the same corpus version.");
            }
        }
        Execute(c,t,"INSERT OR IGNORE INTO sources VALUES(@n,@s,@k,@s,@authority,NULL)",("@n",space),("@s",evidence.SourceId),("@k",evidence.SourceKind),("@authority",space.StartsWith("corpus:",StringComparison.Ordinal)?"configured-corpus":"speaker-attributed"));
        long sourceRevision;
        using (var cmd=Command(c,t,"SELECT COALESCE(MAX(revision),0) FROM source_revisions WHERE namespace_id=@n AND source_id=@s",("@n",space),("@s",evidence.SourceId)))
            sourceRevision=Convert.ToInt64(cmd.ExecuteScalar())+1;
        // Each immutable evidence revision has a source snapshot; no mutable source contents behind an ID.
        Execute(c,t,"INSERT INTO source_revisions VALUES(@n,@s,@r,@h,@v,@at,NULL)",("@n",space),("@s",evidence.SourceId),("@r",sourceRevision),("@h",hash),("@v",version),("@at",Now()));
        long revision;
        using (var cmd=Command(c,t,"SELECT COALESCE(MAX(revision),0)+1 FROM evidence_units WHERE namespace_id=@n AND evidence_id=@id",("@n",space),("@id",evidence.Id))) revision=Convert.ToInt64(cmd.ExecuteScalar());
        Execute(c,t,"""
            INSERT INTO evidence_units(namespace_id,evidence_id,revision,source_id,source_revision,text,text_hash,speaker,chapter_id,sequence,variant,source_kind,observed_at,recorded_at,corpus_version,metadata_json)
            VALUES(@n,@id,@r,@s,@sr,@text,@h,@speaker,@chapter,@seq,@variant,@kind,@observed,@at,@v,@meta)
            """,("@n",space),("@id",evidence.Id),("@r",revision),("@s",evidence.SourceId),("@sr",sourceRevision),("@text",evidence.Text),("@h",hash),("@speaker",evidence.Speaker),("@chapter",evidence.ChapterId),("@seq",evidence.Sequence),("@variant",evidence.Variant),("@kind",evidence.SourceKind),("@observed",evidence.ObservedAt?.ToUnixTimeMilliseconds()),("@at",Now()),("@v",version),("@meta",evidence.MetadataJson));
        Execute(c,t,"INSERT INTO evidence_search_map(namespace_id,evidence_id,revision) VALUES(@n,@id,@r)",("@n",space),("@id",evidence.Id),("@r",revision));
        using var rowId=Command(c,t,"SELECT last_insert_rowid()"); var searchId=Convert.ToInt64(rowId.ExecuteScalar());
        Execute(c,t,"INSERT INTO evidence_fts(rowid,tokens) VALUES(@id,@tokens)",("@id",searchId),("@tokens",Tokens(evidence.Speaker+" "+evidence.Text)));
        Execute(c,t,"UPDATE namespace_state SET commit_seq=commit_seq+1 WHERE namespace_id=@n",("@n",space));
        return new(space,evidence.Id,revision);
    }

    // Hash bigrams use the same normalized units as the existing BM25, avoiding FTS query operators.
    public static string Tokens(string text) => string.Join(' ', StoryQueryAnalyzer.BigramHashes(text).Distinct().Order().Select(x=>"t"+x.ToString("x8")));
    public IReadOnlyList<KnowledgeCandidate> Search(KnowledgeAccessContext access, string query, int limit = 40,
        string? onlyNamespace = null, RecallBudget? budget = null, CancellationToken ct = default,IReadOnlyList<string>? excludedIds=null,TimeSpan? duration=null)
    {
        if(onlyNamespace is not null)access.Demand(onlyNamespace);
        if(excludedIds is {Count:>100})throw new ArgumentException("Excluded evidence selection exceeds limit.");
        var tokens=Tokens(query).Split(' ',StringSplitOptions.RemoveEmptyEntries).Take(64).ToArray();
        if(tokens.Length==0)return [];
        var result=new List<KnowledgeCandidate>();
        return database.Read(c=>
        {
        foreach(var space in access.Namespaces.Order(StringComparer.Ordinal))
        {
            if(onlyNamespace is not null && space!=onlyNamespace)continue;
            ct.ThrowIfCancellationRequested();
            if(budget is not null && !budget.ReserveQuery())break;
            using var cmd=Command(c,null,$"""
                SELECT e.evidence_id,e.revision FROM evidence_fts
                JOIN evidence_search_map m ON m.rowid=evidence_fts.rowid
                JOIN evidence_units e ON e.namespace_id=m.namespace_id AND e.evidence_id=m.evidence_id AND e.revision=m.revision
                JOIN namespaces n ON n.namespace_id=e.namespace_id
                WHERE evidence_fts MATCH @q AND e.namespace_id=@n AND e.deleted_at IS NULL
                AND e.evidence_id NOT IN (SELECT value FROM json_each(@excluded))
                AND (n.domain='memory' OR e.corpus_version=n.active_corpus_version)
                AND NOT EXISTS(SELECT 1 FROM memory_records mr WHERE mr.namespace_id=e.namespace_id AND mr.memory_id=e.evidence_id
                    AND (mr.archived=1 OR mr.superseded_by IS NOT NULL OR (mr.valid_to IS NOT NULL AND mr.valid_to<=@now)))
                AND NOT EXISTS(SELECT 1 FROM evidence_units newer WHERE newer.namespace_id=e.namespace_id AND newer.evidence_id=e.evidence_id AND newer.revision>e.revision AND (n.domain='memory' OR newer.corpus_version=n.active_corpus_version))
                AND {ProvenancePredicate}
                ORDER BY bm25(evidence_fts),e.evidence_id LIMIT @limit
                """,("@q",string.Join(" OR ",tokens)),("@n",space),("@limit",Math.Clamp(limit,1,100)),("@now",Now()),("@known",Now()),("@excluded",JsonSerializer.Serialize(excludedIds??[])));
            using var rows=cmd.ExecuteReader(); var rank=0;
            while(rows.Read()) result.Add(new(new(space,rows.GetString(0),rows.GetInt64(1)),"sql-lexical",++rank));
        }
        return (IReadOnlyList<KnowledgeCandidate>)result;
        },ct,duration);
    }
    public KnowledgeEvidence? Read(KnowledgeAccessContext access, EvidenceRef reference,CancellationToken ct=default)
    {
        access.Demand(reference.Namespace);
        return database.Read<KnowledgeEvidence?>(c=>
        {
        using var cmd=Command(c,null,$"""
            SELECT e.text,e.speaker,e.source_kind,e.source_id,e.chapter_id,e.observed_at FROM evidence_units e
            JOIN namespaces n ON e.namespace_id=n.namespace_id
            WHERE e.namespace_id=@n AND e.evidence_id=@id AND e.revision=@r AND e.deleted_at IS NULL
            AND (n.domain='memory' OR e.corpus_version=n.active_corpus_version)
            AND NOT EXISTS(SELECT 1 FROM evidence_units newer WHERE newer.namespace_id=e.namespace_id AND newer.evidence_id=e.evidence_id AND newer.revision>e.revision AND (n.domain='memory' OR newer.corpus_version=n.active_corpus_version))
            AND {ProvenancePredicate}
            """,("@n",reference.Namespace),("@id",reference.Id),("@r",reference.Revision),("@known",Now()));
        using var row=cmd.ExecuteReader();
        return row.Read()?new(reference,row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetString(4),row.IsDBNull(5)?null:DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(5))):null;
        },ct);
    }

    /// <summary>Bounded personal originals for relevance selection, not preselected answer evidence.</summary>
    public IReadOnlyList<KnowledgeEvidence> PersonalSelectionCandidates(KnowledgeAccessContext access,
        IReadOnlyList<string>? excludedIds=null,int limit=32,CancellationToken ct=default,TimeSpan? duration=null)
    {
        if(limit is <1 or >32 || excludedIds is {Count:>100})
            throw new ArgumentException("Personal candidate selection exceeds limit.");
        ct.ThrowIfCancellationRequested();
        return database.Read(c=>
        {
        using var command=Command(c,null,$"""
            SELECT e.evidence_id,e.revision,e.text,e.speaker,e.source_kind,e.source_id,e.chapter_id,e.observed_at,e.metadata_json,e.recorded_at
            FROM evidence_units e JOIN namespaces n ON n.namespace_id=e.namespace_id
            LEFT JOIN memory_records mr ON mr.namespace_id=e.namespace_id AND mr.memory_id=e.evidence_id
            WHERE e.namespace_id=@n AND n.domain='memory' AND e.speaker='user' AND e.deleted_at IS NULL
              AND e.evidence_id NOT IN (SELECT value FROM json_each(@excluded))
              AND (mr.memory_id IS NULL OR (mr.archived=0 AND mr.superseded_by IS NULL AND (mr.valid_to IS NULL OR mr.valid_to>@known)))
              AND NOT EXISTS(SELECT 1 FROM evidence_units newer WHERE newer.namespace_id=e.namespace_id
                AND newer.evidence_id=e.evidence_id AND newer.revision>e.revision)
              AND {ProvenancePredicate}
            ORDER BY e.recorded_at DESC,e.evidence_id LIMIT @limit
            """,("@n",access.MemoryNamespace),("@excluded",JsonSerializer.Serialize(excludedIds??[])),("@known",Now()),("@limit",limit));
        using var rows=command.ExecuteReader();var result=new List<KnowledgeEvidence>();
        while(rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new(new(access.MemoryNamespace,rows.GetString(0),rows.GetInt64(1)),rows.GetString(2),rows.GetString(3),rows.GetString(4),
                rows.GetString(5),rows.GetString(6),rows.IsDBNull(7)?null:DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(7)),
                rows.GetString(8),DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(9))));
        }
        return (IReadOnlyList<KnowledgeEvidence>)result;
        },ct,duration);
    }
    public IReadOnlyList<KnowledgeCandidate> Neighbors(KnowledgeAccessContext access,IReadOnlyList<EvidenceRef> anchors,CancellationToken ct=default,TimeSpan? duration=null)
    {
        if(anchors.Count>5)throw new ArgumentException("Context anchor limit exceeded.");
        foreach(var reference in anchors)
        {
            access.Demand(reference.Namespace);
            if(!reference.Namespace.StartsWith("corpus:",StringComparison.Ordinal))throw new ArgumentException("Only public story scenes have context windows.");
        }
        var selected=JsonSerializer.Serialize(anchors.Select(r=>new{ns=r.Namespace,id=r.Id,revision=r.Revision}));
        return database.Read(c=>
        {
        using var command=Command(c,null,"""
            SELECT n.namespace_id,n.evidence_id,n.revision,MIN(ABS(n.sequence-a.sequence)) AS distance
            FROM json_each(@selected) selected
            JOIN evidence_units a ON a.namespace_id=json_extract(selected.value,'$.ns') AND a.evidence_id=json_extract(selected.value,'$.id') AND a.revision=json_extract(selected.value,'$.revision')
            JOIN namespaces scope ON scope.namespace_id=a.namespace_id AND scope.active_corpus_version=a.corpus_version
            JOIN evidence_units n ON n.namespace_id=a.namespace_id AND n.corpus_version=a.corpus_version
                AND n.chapter_id=a.chapter_id AND n.source_kind=a.source_kind AND n.source_id=a.source_id
                AND ABS(n.sequence-a.sequence)<=3 AND n.deleted_at IS NULL
            WHERE a.deleted_at IS NULL
                AND COALESCE(json_extract(n.metadata_json,'$.talk_id'),0)=COALESCE(json_extract(a.metadata_json,'$.talk_id'),0)
                AND COALESCE(json_extract(n.metadata_json,'$.subquest_index'),0)=COALESCE(json_extract(a.metadata_json,'$.subquest_index'),0)
                AND COALESCE(json_extract(n.metadata_json,'$.source_page_id'),'')=COALESCE(json_extract(a.metadata_json,'$.source_page_id'),'')
                AND (a.source_kind<>'character_story' OR json_extract(n.metadata_json,'$.text_hash')=json_extract(a.metadata_json,'$.text_hash'))
            GROUP BY n.namespace_id,n.evidence_id,n.revision ORDER BY distance,n.evidence_id LIMIT 40
            """,("@selected",selected));
        using var rows=command.ExecuteReader();var result=new List<KnowledgeCandidate>();
        while(rows.Read())
        {ct.ThrowIfCancellationRequested();result.Add(new(new(rows.GetString(0),rows.GetString(1),rows.GetInt64(2)),"sql-context",rows.GetInt32(3)+1));}
        return (IReadOnlyList<KnowledgeCandidate>)result;
        },ct,duration);
    }
    public IReadOnlyDictionary<string,long> PrivacyEpochs(KnowledgeAccessContext access,CancellationToken ct=default)
    {
        return database.Read(c=>
        {
        var epochs=new Dictionary<string,long>(StringComparer.Ordinal);
        foreach(var space in access.Namespaces)
        { using var cmd=Command(c,null,"SELECT privacy_epoch FROM namespaces WHERE namespace_id=@n",("@n",space)); epochs[space]=Convert.ToInt64(cmd.ExecuteScalar()??0L); }
        return (IReadOnlyDictionary<string,long>)epochs;
        },ct);
    }

    public IReadOnlyDictionary<string,KnowledgeReadStamp> ReadStamps(KnowledgeAccessContext access,IEnumerable<string> spaces,CancellationToken ct=default,TimeSpan? duration=null)
    {
        var result=new Dictionary<string,KnowledgeReadStamp>(StringComparer.Ordinal);
        return database.Read(c=>
        {
        using var t=c.BeginTransaction(deferred:true);
        foreach(var space in spaces.Distinct(StringComparer.Ordinal))
        {
            access.Demand(space);
            using var command=Command(c,t,"SELECT commit_seq,privacy_epoch FROM namespace_state JOIN namespaces USING(namespace_id) WHERE namespace_id=@n",("@n",space));
            using var row=command.ExecuteReader();
            result[space]=row.Read()?new(row.GetInt64(0),row.GetInt64(1)):new(0,0);
        }
        return (IReadOnlyDictionary<string,KnowledgeReadStamp>)result;
        },ct,duration);
    }

    public IReadOnlyList<KnowledgeEvidence> ReadMany(KnowledgeAccessContext access,IReadOnlyList<EvidenceRef> references,KnowledgeQuery query,CancellationToken ct=default,TimeSpan? duration=null)
    {
        if(references.Count>200)throw new ArgumentException("Evidence hydration limit exceeded.");
        if(!Enum.IsDefined(query.TimeMode) || query.TimeMode==KnowledgeTimeMode.Current && (query.AsOf is not null || query.KnownAt is not null))
            throw new ArgumentException("Invalid evidence time mode.");
        foreach(var reference in references)access.Demand(reference.Namespace);
        ct.ThrowIfCancellationRequested();
        var selected=JsonSerializer.Serialize(references.Distinct().Select(r=>new{ns=r.Namespace,id=r.Id,revision=r.Revision}));
        return database.Read(c=>
        {
        using var t=c.BeginTransaction(deferred:true);
        using var command=Command(c,t,$"""
            SELECT e.namespace_id,e.evidence_id,e.revision,e.text,e.speaker,e.source_kind,e.source_id,e.chapter_id,e.observed_at,e.metadata_json,e.recorded_at
            FROM json_each(@selected) wanted
            JOIN evidence_units e ON e.namespace_id=json_extract(wanted.value,'$.ns') AND e.evidence_id=json_extract(wanted.value,'$.id')
                AND e.revision=json_extract(wanted.value,'$.revision')
            JOIN namespaces n ON n.namespace_id=e.namespace_id
            WHERE e.deleted_at IS NULL AND e.recorded_at<=@known AND (n.domain='memory' OR e.corpus_version=n.active_corpus_version)
            AND NOT EXISTS(SELECT 1 FROM evidence_units newer WHERE newer.namespace_id=e.namespace_id AND newer.evidence_id=e.evidence_id
                AND newer.revision>e.revision AND newer.recorded_at<=@known AND (n.domain='memory' OR newer.corpus_version=n.active_corpus_version))
            AND (@history=1 OR NOT EXISTS(SELECT 1 FROM memory_records mr WHERE mr.namespace_id=e.namespace_id AND mr.memory_id=e.evidence_id
                AND (mr.archived=1 OR mr.superseded_by IS NOT NULL OR (mr.valid_to IS NOT NULL AND mr.valid_to<=@now))))
            AND {ProvenancePredicate}
            """,("@selected",selected),("@known",(query.KnownAt??DateTimeOffset.UtcNow).ToUnixTimeMilliseconds()),
            ("@history",query.TimeMode==KnowledgeTimeMode.Historical?1:0),("@now",Now()));
        using var rows=command.ExecuteReader();var result=new List<KnowledgeEvidence>();
        while(rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new(new(rows.GetString(0),rows.GetString(1),rows.GetInt64(2)),rows.GetString(3),rows.GetString(4),rows.GetString(5),rows.GetString(6),rows.GetString(7),
                rows.IsDBNull(8)?null:DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(8)),rows.GetString(9),DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(10))));
        }
        return (IReadOnlyList<KnowledgeEvidence>)result;
        },ct,duration);
    }
}
