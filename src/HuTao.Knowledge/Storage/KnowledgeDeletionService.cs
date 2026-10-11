using System.Text.Json;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Retrieval;
using Microsoft.Data.Sqlite;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Storage;

public sealed record KnowledgeDeletionResult(int EvidenceDeleted,int AssertionsRetracted,int SharedDerivationsRetained,long PrivacyEpoch);

/// <summary>Explicit host-authorized forgetting, distinct from archive or correction. No remote work in the transaction.</summary>
public sealed class KnowledgeDeletionService(SqliteKnowledgeDatabase database)
{
    public KnowledgeDeletionResult Forget(KnowledgeAccessContext access,string space,IReadOnlyCollection<string> evidenceIds,CancellationToken ct=default)
    {
        access.Demand(space);
        if(space!=access.MemoryNamespace)throw new UnauthorizedAccessException("Forgetting requires the current private conversation namespace.");
        if(evidenceIds.Count>1000 || evidenceIds.Any(string.IsNullOrWhiteSpace))throw new ArgumentException("Invalid deletion selection.");
        return database.Write((c,t)=>ForgetInTransaction(c,t,space,evidenceIds,ct),ct);
    }

    internal static KnowledgeDeletionResult ForgetInTransaction(SqliteConnection c,SqliteTransaction t,string space,IReadOnlyCollection<string> evidenceIds,CancellationToken ct=default)
    {
        var deleted=new HashSet<string>(evidenceIds,StringComparer.Ordinal);
        using(var existing=Command(c,t,"SELECT evidence_id FROM deletion_tombstones WHERE namespace_id=@n",("@n",space)))
        using(var rows=existing.ExecuteReader())while(rows.Read())deleted.Remove(rows.GetString(0));
        long Epoch() {using var command=Command(c,t,"SELECT privacy_epoch FROM namespaces WHERE namespace_id=@n",("@n",space));return Convert.ToInt64(command.ExecuteScalar());}
        if(deleted.Count==0)return new(0,0,0,Epoch());
        string Json()=>JsonSerializer.Serialize(deleted);
        var retained=new HashSet<string>(StringComparer.Ordinal);
        // A remaining identical attributable source can replace a forgotten source. Merely having another source is insufficient.
        bool EquivalentSources(string table,string idColumn,string revisionColumn,string id,long revision)
        {
            var sourceId=table=="memory_derivations"?"source_evidence_id":"evidence_id";
            var sourceRevision=table=="memory_derivations"?"source_revision":"evidence_revision";
            var span=table=="assertion_evidence"?" AND old.start_utf16=remaining.start_utf16 AND old.end_utf16=remaining.end_utf16 AND old.support_kind=remaining.support_kind":"";
            using var command=Command(c,t,$"""
                SELECT COUNT(*) FROM {table} old JOIN evidence_units source
                  ON source.namespace_id=old.namespace_id AND source.evidence_id=old.{sourceId} AND source.revision=old.{sourceRevision}
                WHERE old.namespace_id=@n AND old.{idColumn}=@id AND old.{revisionColumn}=@r
                  AND old.{sourceId} IN (SELECT value FROM json_each(@deleted))
                  AND NOT EXISTS(SELECT 1 FROM {table} remaining JOIN evidence_units live
                    ON live.namespace_id=remaining.namespace_id AND live.evidence_id=remaining.{sourceId} AND live.revision=remaining.{sourceRevision}
                    WHERE remaining.namespace_id=old.namespace_id AND remaining.{idColumn}=old.{idColumn} AND remaining.{revisionColumn}=old.{revisionColumn}
                      AND remaining.{sourceId} NOT IN (SELECT value FROM json_each(@deleted)) AND live.deleted_at IS NULL
                      AND live.text=source.text AND live.speaker=source.speaker AND live.source_kind=source.source_kind AND live.observed_at IS source.observed_at {span}
                      AND NOT EXISTS(SELECT 1 FROM evidence_units newer WHERE newer.namespace_id=live.namespace_id AND newer.evidence_id=live.evidence_id AND newer.revision>live.revision))
                """,("@n",space),("@id",id),("@r",revision),("@deleted",Json()));
            return Convert.ToInt64(command.ExecuteScalar())==0;
        }
        bool changed;
        do
        {
            changed=false;
            using var command=Command(c,t,"""
                SELECT DISTINCT d.memory_id,d.memory_revision FROM memory_derivations d JOIN evidence_units e
                ON e.namespace_id=d.namespace_id AND e.evidence_id=d.memory_id AND e.revision=d.memory_revision
                WHERE d.namespace_id=@n AND e.deleted_at IS NULL AND d.source_evidence_id IN (SELECT value FROM json_each(@ids))
                """,("@n",space),("@ids",Json()));
            var dependencies=new List<(string Id,long Revision)>();using(var rows=command.ExecuteReader())while(rows.Read())dependencies.Add((rows.GetString(0),rows.GetInt64(1)));
            foreach(var item in dependencies)
            {
                ct.ThrowIfCancellationRequested();if(deleted.Contains(item.Id))continue;
                if(EquivalentSources("memory_derivations","memory_id","memory_revision",item.Id,item.Revision))retained.Add(item.Id);
                else {deleted.Add(item.Id);retained.Remove(item.Id);changed=true;}
            }
        }while(changed);
        var ids=Json();var at=Now();var assertions=new List<(string Id,long Revision)>();
        using(var command=Command(c,t,"""
            SELECT a.assertion_id,MAX(a.revision) FROM assertions a WHERE a.namespace_id=@n AND a.assertion_id IN
              (SELECT assertion_id FROM assertion_evidence WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids)))
            GROUP BY a.assertion_id
            """,("@n",space),("@ids",ids)))
        using(var rows=command.ExecuteReader())while(rows.Read())assertions.Add((rows.GetString(0),rows.GetInt64(1)));
        var retracted=0;
        foreach(var item in assertions)
        {
            ct.ThrowIfCancellationRequested();
            var keep=EquivalentSources("assertion_evidence","assertion_id","assertion_revision",item.Id,item.Revision);
            // Old revisions may contain details no longer justified by the remaining sources: scrub them even when the current fact survives.
            Execute(c,t,"""
                UPDATE assertions SET status='retracted',typed_literal=CASE WHEN object_id IS NULL THEN '' ELSE NULL END,
                  qualifiers_json='{}',validation_method='forgotten',retired_at=COALESCE(retired_at,@at)
                WHERE namespace_id=@n AND assertion_id=@id AND revision<@r
                """,("@n",space),("@id",item.Id),("@r",item.Revision),("@at",at));
            Execute(c,t,"""
                INSERT INTO assertions SELECT namespace_id,assertion_id,revision+1,subject_id,predicate_id,object_id,
                  CASE WHEN @keep=1 THEN typed_literal WHEN object_id IS NULL THEN '' ELSE NULL END,polarity,
                  CASE WHEN @keep=1 THEN qualifiers_json ELSE '{}' END,valid_from,valid_to,time_basis,
                  CASE WHEN @keep=1 THEN status ELSE 'retracted' END,extraction_confidence,
                  CASE WHEN @keep=1 THEN 'remaining-identical-source' ELSE 'forgotten' END,superseded_by,@at,NULL
                FROM assertions WHERE namespace_id=@n AND assertion_id=@id AND revision=@r
                """,("@keep",keep?1:0),("@at",at),("@n",space),("@id",item.Id),("@r",item.Revision));
            Execute(c,t,"UPDATE assertions SET retired_at=@at WHERE namespace_id=@n AND assertion_id=@id AND revision=@r",("@at",at),("@n",space),("@id",item.Id),("@r",item.Revision));
            if(keep)
                Execute(c,t,"""
                    INSERT INTO assertion_evidence SELECT namespace_id,assertion_id,assertion_revision+1,evidence_id,evidence_revision,start_utf16,end_utf16,support_kind
                    FROM assertion_evidence WHERE namespace_id=@n AND assertion_id=@id AND assertion_revision=@r AND evidence_id NOT IN (SELECT value FROM json_each(@ids))
                    """,("@n",space),("@id",item.Id),("@r",item.Revision),("@ids",ids));
            else
            {
                retracted++;
                Execute(c,t,"UPDATE assertions SET status='retracted',typed_literal=CASE WHEN object_id IS NULL THEN '' ELSE NULL END,qualifiers_json='{}',validation_method='forgotten' WHERE namespace_id=@n AND assertion_id=@id",("@n",space),("@id",item.Id));
            }
            GraphOutboxRepository.Enqueue(c,t,space,item.Id,item.Revision+1,keep?"assertion-upsert":"assertion-retract");
        }
        Execute(c,t,"DELETE FROM assertion_evidence WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids))",("@n",space),("@ids",ids));
        Execute(c,t,"DELETE FROM memory_derivations WHERE namespace_id=@n AND (source_evidence_id IN (SELECT value FROM json_each(@ids)) OR memory_id IN (SELECT value FROM json_each(@ids)))",("@n",space),("@ids",ids));
        // Scrub names only for entities whose attributable mentions/aliases were affected and have no surviving attribution.
        Execute(c,t,"""
            UPDATE entities SET canonical_name='',status='forgotten',revision=revision+1 WHERE namespace_id=@n AND entity_id IN
              (SELECT entity_id FROM mentions WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids))
               UNION SELECT entity_id FROM entity_aliases WHERE namespace_id=@n AND source_evidence_id IN (SELECT value FROM json_each(@ids)))
              AND NOT EXISTS(SELECT 1 FROM mentions m WHERE m.namespace_id=@n AND m.entity_id=entities.entity_id AND m.evidence_id NOT IN (SELECT value FROM json_each(@ids)))
              AND NOT EXISTS(SELECT 1 FROM entity_aliases a WHERE a.namespace_id=@n AND a.entity_id=entities.entity_id AND a.source_evidence_id NOT IN (SELECT value FROM json_each(@ids)))
            """,("@n",space),("@ids",ids));
        Execute(c,t,"DELETE FROM mentions WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids))",("@n",space),("@ids",ids));
        Execute(c,t,"DELETE FROM entity_aliases WHERE namespace_id=@n AND source_evidence_id IN (SELECT value FROM json_each(@ids))",("@n",space),("@ids",ids));
        Execute(c,t,"DELETE FROM evidence_fts WHERE rowid IN (SELECT rowid FROM evidence_search_map WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids)))",("@n",space),("@ids",ids));
        var count=Execute(c,t,"UPDATE evidence_units SET text='',metadata_json='{}',speaker='',chapter_id='',deleted_at=@at WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids)) AND deleted_at IS NULL",("@n",space),("@ids",ids),("@at",at));
        Execute(c,t,"INSERT OR IGNORE INTO deletion_tombstones SELECT @n,value,@at FROM json_each(@ids)",("@n",space),("@ids",ids),("@at",at));
        Execute(c,t,"UPDATE memory_records SET archived=1,fingerprint='' WHERE namespace_id=@n AND memory_id IN (SELECT value FROM json_each(@ids))",("@n",space),("@ids",ids));
        Execute(c,t,"DELETE FROM important_items WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids))",("@n",space),("@ids",ids));
        // Entity catalogs may contain names from other units: invalidate this namespace's whole extraction cache, including in-flight jobs.
        Execute(c,t,"UPDATE turn_ingestion SET status='cancelled',lease_token=NULL,lease_until=NULL,diagnostic='forgotten' WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids))",("@n",space),("@ids",ids));
        Execute(c,t,"UPDATE extraction_jobs SET output_json=NULL,status='interrupted',error_code='privacy-invalidated',finished_at=@at WHERE namespace_id=@n",("@n",space),("@at",at));
        Execute(c,t,"UPDATE extraction_publications SET retracted_at=COALESCE(retracted_at,@at) WHERE job_id IN (SELECT job_id FROM extraction_jobs WHERE namespace_id=@n AND evidence_id IN (SELECT value FROM json_each(@ids)))",("@n",space),("@ids",ids),("@at",at));
        Execute(c,t,"UPDATE graph_rebuild_jobs SET status='invalidated',lease_token=NULL,lease_until=NULL,updated_at=@at WHERE namespace_id=@n AND status<>'activated'",("@n",space),("@at",at));
        Execute(c,t,"DELETE FROM graph_rebuild_items WHERE namespace_id=@n",("@n",space));
        Execute(c,t,"UPDATE memory_state SET state_revision=state_revision+1 WHERE namespace_id=@n",("@n",space));
        Execute(c,t,"UPDATE namespace_state SET commit_seq=commit_seq+1 WHERE namespace_id=@n",("@n",space));
        Execute(c,t,"UPDATE namespaces SET privacy_epoch=privacy_epoch+1 WHERE namespace_id=@n",("@n",space));
        return new(count,retracted,retained.Count,Epoch());
    }
}
