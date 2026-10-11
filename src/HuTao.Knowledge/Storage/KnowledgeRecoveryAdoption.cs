using System.Text.Json;
using HuTao.Knowledge.Retrieval;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Storage;

public sealed record RecoveryAdoptionDecision(string ReviewerId,string Namespace,IReadOnlyList<string> EvidenceIds,
    long SourceCommitSequence,long SourcePrivacyEpoch,long TargetCommitSequence,long TargetPrivacyEpoch,
    long SourceMemoryRevision=0,long TargetMemoryRevision=0);
public sealed record RecoveryAdoptionResult(string DecisionId,int Adopted,bool Replay);
public sealed record RecoveryTurnDependency(long MemoryRevision,string SourceId,long SourceRevision);
public sealed record RecoveryTurnReview(string Id,long Revision,string Speaker,string Text,bool Archived,bool CandidateHasId,bool HasDependencies)
{
    public IReadOnlyList<RecoveryTurnDependency> Dependencies {get;init;}=[];
}
public sealed record RecoveryAdoptionReview(RecoveryAdoptionDecision Decision,IReadOnlyList<RecoveryTurnReview> Turns);

/// <summary>Explicit adoption of original conversation Turns with exact provenance. No inferred facts or conflicting IDs are copied.</summary>
public static class KnowledgeRecoveryAdoption
{
    public static RecoveryAdoptionReview Review(SqliteKnowledgeDatabase target,string quarantinePath,KnowledgeAccessContext access,string reviewerId,IReadOnlyList<string> evidenceIds)
    {
        if(evidenceIds.Count is <1 or >100 || evidenceIds.Any(string.IsNullOrWhiteSpace)||evidenceIds.Distinct().Count()!=evidenceIds.Count)throw new ArgumentException("Select 1-100 distinct originals.");
        if(!File.Exists(quarantinePath))throw new FileNotFoundException("Quarantine missing.");
        using var c=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=target.FilePath,Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,Pooling=false}.ToString());c.Open();
        using(var attach=Command(c,null,"ATTACH DATABASE @path AS quarantine",("@path",Path.GetFullPath(quarantinePath))))attach.ExecuteNonQuery();
        using var t=c.BeginTransaction(deferred:true);
        long Number(string sql){using var q=Command(c,t,sql,("@n",access.MemoryNamespace));return Convert.ToInt64(q.ExecuteScalar());}
        if(Number("SELECT COUNT(*) FROM main.recovery_gate")!=1 || Number("SELECT COUNT(*) FROM quarantine.recovery_gate")!=1 ||
            Number("SELECT COUNT(*) FROM main.database_identity a JOIN quarantine.database_identity b ON a.lineage_id=b.lineage_id")!=1)throw new InvalidDataException("Review requires gated outputs of the same lineage.");
        var decision=new RecoveryAdoptionDecision(reviewerId,access.MemoryNamespace,evidenceIds,
            Number("SELECT commit_seq FROM quarantine.namespace_state WHERE namespace_id=@n"),Number("SELECT privacy_epoch FROM quarantine.namespaces WHERE namespace_id=@n"),
            Number("SELECT commit_seq FROM main.namespace_state WHERE namespace_id=@n"),Number("SELECT privacy_epoch FROM main.namespaces WHERE namespace_id=@n"),
            Number("SELECT state_revision FROM quarantine.memory_state WHERE namespace_id=@n"),Number("SELECT state_revision FROM main.memory_state WHERE namespace_id=@n"));
        using var query=Command(c,t,"""
            SELECT e.evidence_id,e.revision,e.speaker,e.text,m.archived,
              EXISTS(SELECT 1 FROM main.evidence_units current WHERE current.namespace_id=e.namespace_id AND current.evidence_id=e.evidence_id),
              EXISTS(SELECT 1 FROM quarantine.memory_derivations d WHERE d.namespace_id=e.namespace_id AND d.memory_id=e.evidence_id)
            FROM quarantine.memory_records m JOIN quarantine.evidence_units e
              ON e.namespace_id=m.namespace_id AND e.evidence_id=m.memory_id AND e.revision=m.evidence_revision
            WHERE e.namespace_id=@n AND m.kind='Turn' AND e.deleted_at IS NULL AND e.evidence_id IN (SELECT value FROM json_each(@ids))
              AND NOT EXISTS(SELECT 1 FROM main.deletion_tombstones d WHERE d.namespace_id=e.namespace_id AND d.evidence_id=e.evidence_id)
              AND NOT EXISTS(SELECT 1 FROM quarantine.deletion_tombstones d WHERE d.namespace_id=e.namespace_id AND d.evidence_id=e.evidence_id)
            ORDER BY e.evidence_id
            """,("@n",access.MemoryNamespace),("@ids",JsonSerializer.Serialize(evidenceIds)));
        var turns=new List<RecoveryTurnReview>();using(var rows=query.ExecuteReader())while(rows.Read())turns.Add(new(rows.GetString(0),rows.GetInt64(1),rows.GetString(2),rows.GetString(3),rows.GetInt64(4)!=0,rows.GetInt64(5)!=0,rows.GetInt64(6)!=0));
        if(turns.Count!=evidenceIds.Count)throw new InvalidDataException("Selected originals are missing or forgotten.");
        for(var i=0;i<turns.Count;i++)
        {
            using var sources=Command(c,t,"SELECT memory_revision,source_evidence_id,source_revision FROM quarantine.memory_derivations WHERE namespace_id=@n AND memory_id=@id ORDER BY memory_revision,source_evidence_id",("@n",access.MemoryNamespace),("@id",turns[i].Id));
            var dependencies=new List<RecoveryTurnDependency>();using var rows=sources.ExecuteReader();
            while(rows.Read())dependencies.Add(new(rows.GetInt64(0),rows.GetString(1),rows.GetInt64(2)));
            turns[i]=turns[i] with {Dependencies=dependencies};
        }
        return new(decision,turns);
    }
    public static RecoveryAdoptionResult Adopt(SqliteKnowledgeDatabase target,string quarantinePath,KnowledgeAccessContext access,RecoveryAdoptionDecision decision)
    {
        if(decision.Namespace!=access.MemoryNamespace)throw new UnauthorizedAccessException("Recovery requires the selected private scope.");
        if(string.IsNullOrWhiteSpace(decision.ReviewerId)||decision.ReviewerId.Length>200 || decision.EvidenceIds is null || decision.EvidenceIds.Count is <1 or >100 ||
            decision.EvidenceIds.Any(string.IsNullOrWhiteSpace)||decision.EvidenceIds.Distinct(StringComparer.Ordinal).Count()!=decision.EvidenceIds.Count ||
            new[]{decision.SourceCommitSequence,decision.SourcePrivacyEpoch,decision.TargetCommitSequence,decision.TargetPrivacyEpoch,decision.SourceMemoryRevision,decision.TargetMemoryRevision}.Any(n=>n<0))throw new ArgumentException("Invalid recovery decision.");
        if(!File.Exists(quarantinePath)||string.Equals(Path.GetFullPath(quarantinePath),target.FilePath,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Quarantine must be a separate existing database.");
        var ids=JsonSerializer.Serialize(decision.EvidenceIds.Order(StringComparer.Ordinal));
        var decisionId=Hash(JsonSerializer.Serialize(decision with {EvidenceIds=decision.EvidenceIds.Order(StringComparer.Ordinal).ToArray()}));
        using var c=target.Open();
        using(var attach=Command(c,null,"ATTACH DATABASE @path AS quarantine",("@path",Path.GetFullPath(quarantinePath))))attach.ExecuteNonQuery();
        // Immediate transaction locks both attached databases against competing writers until adoption commits.
        using var t=c.BeginTransaction(deferred:false);
        long Number(string sql)=>Convert.ToInt64(CommandValue(sql));
        object? CommandValue(string sql){using var q=Command(c,t,sql,("@n",decision.Namespace),("@ids",ids));return q.ExecuteScalar();}
        if(Number("SELECT COUNT(*) FROM main.recovery_gate")!=1||Number("SELECT COUNT(*) FROM quarantine.recovery_gate")!=1)
            throw new InvalidOperationException("Both databases must remain gated recovery outputs.");
        if(!Equals(CommandValue("SELECT lineage_id FROM main.database_identity"),CommandValue("SELECT lineage_id FROM quarantine.database_identity")))throw new InvalidDataException("Recovery lineage mismatch.");
        if(Number("SELECT MAX(version) FROM main.schema_migrations")!=Number("SELECT MAX(version) FROM quarantine.schema_migrations"))throw new InvalidDataException("Recovery schema mismatch.");
        if(Number("SELECT COUNT(*) FROM main.deletion_tombstones WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids))")!=0 ||
            Number("SELECT COUNT(*) FROM quarantine.deletion_tombstones WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids))")!=0)
            throw new InvalidDataException("Forgotten originals cannot be adopted.");
        using(var replay=Command(c,t,"SELECT 1 FROM recovery_adoptions WHERE decision_id=@id",("@id",decisionId)))
            if(replay.ExecuteScalar() is not null)return new(decisionId,0,true);
        foreach(var (schema,sequence,epoch) in new[]{("main",decision.TargetCommitSequence,decision.TargetPrivacyEpoch),("quarantine",decision.SourceCommitSequence,decision.SourcePrivacyEpoch)})
        {
            if(Number($"SELECT COUNT(*) FROM {schema}.namespaces WHERE namespace_id=@n")!=1 ||
                Number($"SELECT commit_seq FROM {schema}.namespace_state WHERE namespace_id=@n")!=sequence ||
                Number($"SELECT privacy_epoch FROM {schema}.namespaces WHERE namespace_id=@n")!=epoch)throw new InvalidOperationException("Recovery decision is stale.");
        }
        if(Number("SELECT state_revision FROM main.memory_state WHERE namespace_id=@n")!=decision.TargetMemoryRevision ||
            Number("SELECT state_revision FROM quarantine.memory_state WHERE namespace_id=@n")!=decision.SourceMemoryRevision)
            throw new InvalidOperationException("Recovery memory state changed after review.");
        if(Number("SELECT COUNT(*) FROM main.evidence_units WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids))")!=0)
            throw new InvalidDataException("Existing evidence ID requires explicit conflict resolution, not adoption.");
        if(Number("""
            SELECT COUNT(*) FROM quarantine.memory_records m JOIN quarantine.evidence_units e
              ON e.namespace_id=m.namespace_id AND e.evidence_id=m.memory_id AND e.revision=m.evidence_revision
            WHERE m.namespace_id=@n AND m.memory_id IN (SELECT value FROM json_each(@ids)) AND m.kind='Turn'
              AND e.speaker IN ('user','assistant') AND e.deleted_at IS NULL AND m.superseded_by IS NULL
              AND NOT EXISTS(SELECT 1 FROM quarantine.evidence_units newer WHERE newer.namespace_id=e.namespace_id AND newer.evidence_id=e.evidence_id AND newer.revision>e.revision)
            """)!=decision.EvidenceIds.Count)throw new InvalidDataException("Select current user or assistant Turns only.");
        if(Number("""
            SELECT COUNT(*) FROM quarantine.evidence_units e WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids))
              AND speaker='assistant' AND NOT EXISTS(SELECT 1 FROM quarantine.memory_derivations d
                WHERE d.namespace_id=e.namespace_id AND d.memory_id=e.evidence_id AND d.memory_revision=e.revision)
            """)!=0)throw new InvalidDataException("Assistant history needs attributable source revisions.");
        if(Number("""
            WITH RECURSIVE reachable(origin,id) AS (
              SELECT memory_id,source_evidence_id FROM quarantine.memory_derivations WHERE namespace_id=@n AND memory_id IN (SELECT value FROM json_each(@ids))
              UNION
              SELECT r.origin,d.source_evidence_id FROM reachable r JOIN quarantine.memory_derivations d ON d.memory_id=r.id
              WHERE d.namespace_id=@n AND d.memory_id IN (SELECT value FROM json_each(@ids)))
            SELECT COUNT(*) FROM reachable WHERE origin=id
            """)!=0)throw new InvalidDataException("Cyclic provenance cannot be adopted.");
        if(Number("SELECT COUNT(*) FROM quarantine.evidence_units WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids)) AND (source_id<>'memory:'||evidence_id OR deleted_at IS NOT NULL)")!=0)
            throw new InvalidDataException("Original source history is not independently transferable.");
        if(Number("SELECT COUNT(*) FROM main.sources WHERE namespace_id=@n AND source_id IN (SELECT 'memory:'||value FROM json_each(@ids))")!=0)
            throw new InvalidDataException("Original source ID conflict.");
        void Write(string sql)=>Execute(c,t,sql,("@n",decision.Namespace),("@ids",ids));
        Write("INSERT INTO main.sources SELECT * FROM quarantine.sources WHERE namespace_id=@n AND source_id IN (SELECT 'memory:'||value FROM json_each(@ids))");
        Write("INSERT INTO main.source_revisions SELECT * FROM quarantine.source_revisions WHERE namespace_id=@n AND source_id IN (SELECT 'memory:'||value FROM json_each(@ids))");
        Write("INSERT INTO main.evidence_units SELECT * FROM quarantine.evidence_units WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids)) ORDER BY rowid");
        Write("INSERT INTO main.memory_records SELECT * FROM quarantine.memory_records WHERE namespace_id=@n AND memory_id IN (SELECT value FROM json_each(@ids))");
        // Validate after staged originals exist so explicitly selected dependency batches work regardless of selection order.
        // Full row equivalence detects divergent branches even when their IDs and revision numbers happen to match.
        if(Number("""
            SELECT COUNT(*) FROM quarantine.memory_derivations d WHERE d.namespace_id=@n AND d.memory_id IN (SELECT value FROM json_each(@ids))
              AND (EXISTS(SELECT 1 FROM main.deletion_tombstones z WHERE z.namespace_id=d.namespace_id AND z.evidence_id=d.source_evidence_id)
                OR EXISTS(SELECT 1 FROM quarantine.deletion_tombstones z WHERE z.namespace_id=d.namespace_id AND z.evidence_id=d.source_evidence_id)
                OR NOT EXISTS(SELECT 1 FROM main.evidence_units e WHERE e.namespace_id=d.namespace_id AND e.evidence_id=d.source_evidence_id AND e.revision=d.source_revision AND e.deleted_at IS NULL)
                OR EXISTS(SELECT * FROM quarantine.evidence_units e WHERE e.namespace_id=d.namespace_id AND e.evidence_id=d.source_evidence_id AND e.revision=d.source_revision
                  EXCEPT SELECT * FROM main.evidence_units e WHERE e.namespace_id=d.namespace_id AND e.evidence_id=d.source_evidence_id AND e.revision=d.source_revision))
            """)!=0)throw new InvalidDataException("A source revision is missing, forgotten or different in the candidate.");
        Write("INSERT INTO main.memory_derivations SELECT * FROM quarantine.memory_derivations WHERE namespace_id=@n AND memory_id IN (SELECT value FROM json_each(@ids))");
        Write("INSERT INTO main.evidence_search_map(namespace_id,evidence_id,revision) SELECT namespace_id,evidence_id,revision FROM main.evidence_units WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids))");
        using(var query=Command(c,t,"SELECT s.rowid,e.speaker,e.text FROM main.evidence_search_map s JOIN main.evidence_units e USING(namespace_id,evidence_id,revision) WHERE e.namespace_id=@n AND e.evidence_id IN (SELECT value FROM json_each(@ids))",("@n",decision.Namespace),("@ids",ids)))
        using(var rows=query.ExecuteReader())while(rows.Read())Execute(c,t,"INSERT INTO main.evidence_fts(rowid,tokens) VALUES(@r,@tokens)",("@r",rows.GetInt64(0)),("@tokens",SqlEvidenceRepository.Tokens(rows.GetString(1)+" "+rows.GetString(2))));
        Write("INSERT INTO main.memory_state(namespace_id,state_revision) VALUES(@n,1) ON CONFLICT(namespace_id) DO UPDATE SET state_revision=state_revision+1");
        Write("UPDATE main.namespace_state SET commit_seq=commit_seq+1 WHERE namespace_id=@n");
        Execute(c,t,"INSERT INTO main.turn_ingestion(namespace_id,evidence_id,evidence_revision,status,diagnostic,created_at) SELECT m.namespace_id,m.memory_id,m.evidence_revision,'deferred','recovery-review-required',@now FROM main.memory_records m JOIN main.evidence_units e ON e.namespace_id=m.namespace_id AND e.evidence_id=m.memory_id AND e.revision=m.evidence_revision WHERE m.namespace_id=@n AND m.memory_id IN (SELECT value FROM json_each(@ids)) AND e.speaker='user'",("@n",decision.Namespace),("@ids",ids),("@now",Now()));
        Execute(c,t,"INSERT INTO main.recovery_adoptions VALUES(@id,@n,@reviewer,@ids,@now)",("@id",decisionId),("@n",decision.Namespace),("@reviewer",decision.ReviewerId),("@ids",ids),("@now",Now()));
        t.Commit();return new(decisionId,decision.EvidenceIds.Count,false);
    }
}
