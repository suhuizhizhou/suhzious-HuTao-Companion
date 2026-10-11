using System.Text.Json;
using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Retrieval;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Storage;

public sealed record RecoveryFactStage(string JobId,string AssertionId,IReadOnlyList<string> ValidationReasons,bool AlreadyStaged);

/// <summary>Restored facts become review candidates through the existing publisher, never accepted facts by copying rows.</summary>
public static class KnowledgeRecoveryFactReview
{
    public static RecoveryFactStage Stage(SqliteKnowledgeDatabase target,string quarantinePath,KnowledgeAccessContext access,string assertionId,long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assertionId);
        if(revision<=0 || !File.Exists(quarantinePath) || string.Equals(target.FilePath,Path.GetFullPath(quarantinePath),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Existing separate quarantine and exact revision required.");
        var space=access.MemoryNamespace;
        using var c=target.Open();using(var attach=Command(c,null,"ATTACH DATABASE @path AS quarantine",("@path",Path.GetFullPath(quarantinePath))))attach.ExecuteNonQuery();
        using var t=c.BeginTransaction(deferred:false);
        object? Scalar(string sql){using var q=Command(c,t,sql,("@n",space),("@id",assertionId),("@r",revision));return q.ExecuteScalar();}
        if(Convert.ToInt64(Scalar("SELECT COUNT(*) FROM main.recovery_gate"))!=1 || Convert.ToInt64(Scalar("SELECT COUNT(*) FROM quarantine.recovery_gate"))!=1 ||
            Convert.ToInt64(Scalar("SELECT COUNT(*) FROM main.database_identity a JOIN quarantine.database_identity b ON a.lineage_id=b.lineage_id"))!=1)
            throw new InvalidDataException("Fact review requires gated same-lineage recovery databases.");
        if(Scalar("SELECT 1 FROM main.assertions WHERE namespace_id=@n AND assertion_id=@id LIMIT 1") is not null)throw new InvalidDataException("Fact ID already exists; resolve the conflict explicitly.");
        AssertionWrite fact;
        using(var query=Command(c,t,"""
            SELECT subject_id,predicate_id,object_id,typed_literal,polarity,qualifiers_json,valid_from,valid_to,time_basis FROM quarantine.assertions a
            WHERE namespace_id=@n AND assertion_id=@id AND revision=@r AND status='accepted' AND retired_at IS NULL
              AND NOT EXISTS(SELECT 1 FROM quarantine.assertions newer WHERE newer.namespace_id=a.namespace_id AND newer.assertion_id=a.assertion_id AND newer.revision>a.revision)
            """,("@n",space),("@id",assertionId),("@r",revision)))
        using(var row=query.ExecuteReader())
        {
            if(!row.Read())throw new InvalidDataException("Selected fact is not the current accepted quarantine revision.");
            if(row.GetString(8)!=(row.IsDBNull(6)?"unknown":"explicit"))throw new InvalidDataException("Unsupported temporal basis requires separate review.");
            fact=new(assertionId,row.GetString(0),row.GetString(1),row.IsDBNull(2)?null:row.GetString(2),row.IsDBNull(3)?null:row.GetString(3),row.GetInt32(4),
                JsonSerializer.Deserialize<Dictionary<string,string>>(row.GetString(5))??throw new InvalidDataException("Invalid qualifiers."),[],
                row.IsDBNull(6)?null:DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(6)),row.IsDBNull(7)?null:DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(7)),Status:"candidate",ValidationMethod:"recovery-review");
        }
        var entities=new List<ExtractionEntity>();
        var missingEntities=new List<ExtractionEntity>();
        foreach(var id in new[]{fact.SubjectId,fact.ObjectId}.Where(id=>id is not null).Distinct(StringComparer.Ordinal))
        {
            using var query=Command(c,t,"""
                SELECT b.type,b.canonical_name,a.entity_id,a.type,a.canonical_name,a.status FROM quarantine.entities b LEFT JOIN main.entities a ON a.namespace_id=b.namespace_id AND a.entity_id=b.entity_id
                WHERE b.namespace_id=@n AND b.entity_id=@entity AND b.status='active'
                """,("@n",space),("@entity",id));using var row=query.ExecuteReader();
            if(!row.Read())throw new InvalidDataException("Quarantine entity is missing or inactive.");
            var entity=new ExtractionEntity(id!,row.GetString(0),row.GetString(1));
            if(row.IsDBNull(2))missingEntities.Add(entity);
            else if(row.GetString(3)!=entity.Type || row.GetString(4)!=entity.Name || row.GetString(5)!="active")throw new InvalidDataException("Existing candidate entity identity conflicts.");
            entities.Add(entity);
        }
        var sourceKeys=new List<(string Id,long Revision,int Start,int End)>();
        using(var query=Command(c,t,"SELECT evidence_id,evidence_revision,start_utf16,end_utf16,support_kind FROM quarantine.assertion_evidence WHERE namespace_id=@n AND assertion_id=@id AND assertion_revision=@r ORDER BY evidence_id,evidence_revision,start_utf16",("@n",space),("@id",assertionId),("@r",revision)))
        using(var rows=query.ExecuteReader())while(rows.Read())
        {if(rows.GetString(4)!="supports" || sourceKeys.Count>=6)throw new InvalidDataException("Unsupported or oversized source set.");sourceKeys.Add((rows.GetString(0),rows.GetInt64(1),rows.GetInt32(2),rows.GetInt32(3)));}
        if(sourceKeys.Count==0)throw new InvalidDataException("Fact has no attributable sources.");
        var sources=new List<AssertionSource>();var candidates=new List<ExtractedAssertion>();var reasons=new List<string>();
        var sourceEvidence=new Dictionary<EvidenceRef,KnowledgeEvidence>();
        foreach(var source in sourceKeys)
        {
            using var query=Command(c,t,"""
                SELECT e.text,e.speaker,e.source_kind,e.source_id,e.chapter_id,e.observed_at FROM main.evidence_units e
                WHERE e.namespace_id=@n AND e.evidence_id=@e AND e.revision=@er AND e.deleted_at IS NULL
                  AND NOT EXISTS(SELECT 1 FROM main.evidence_units newer WHERE newer.namespace_id=e.namespace_id AND newer.evidence_id=e.evidence_id AND newer.revision>e.revision)
                  AND NOT EXISTS(SELECT 1 FROM quarantine.evidence_units newer WHERE newer.namespace_id=e.namespace_id AND newer.evidence_id=e.evidence_id AND newer.revision>e.revision)
                  AND NOT EXISTS(SELECT 1 FROM main.deletion_tombstones z WHERE z.namespace_id=e.namespace_id AND z.evidence_id=e.evidence_id)
                  AND NOT EXISTS(SELECT 1 FROM quarantine.deletion_tombstones z WHERE z.namespace_id=e.namespace_id AND z.evidence_id=e.evidence_id)
                  AND EXISTS(SELECT 1 FROM quarantine.evidence_units b WHERE b.namespace_id=e.namespace_id AND b.evidence_id=e.evidence_id AND b.revision=e.revision)
                  AND NOT EXISTS(SELECT * FROM quarantine.evidence_units b WHERE b.namespace_id=e.namespace_id AND b.evidence_id=e.evidence_id AND b.revision=e.revision
                    EXCEPT SELECT * FROM main.evidence_units b WHERE b.namespace_id=e.namespace_id AND b.evidence_id=e.evidence_id AND b.revision=e.revision)
                """,("@n",space),("@e",source.Id),("@er",source.Revision));
            KnowledgeEvidence evidence;
            using(var row=query.ExecuteReader())
            {
                if(!row.Read())throw new InvalidDataException("Adopt matching current original evidence before staging its fact.");
                evidence=new(new(space,source.Id,source.Revision),row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetString(4),row.IsDBNull(5)?null:DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(5)));
            }
            if(evidence.Text.Length>4000 || source.Start<0 || source.End>evidence.Text.Length || source.End<=source.Start)throw new InvalidDataException("Invalid recovery evidence span.");
            sourceEvidence[evidence.Reference]=evidence;
            var extracted=new ExtractedAssertion(fact.SubjectId,fact.PredicateId,fact.ObjectId,fact.Literal,fact.Polarity,new(fact.Qualifiers),source.Start,source.End,evidence.Text[source.Start..source.End]);
            var validation=AssertionValidator.Validate(fact.Id,evidence,extracted,entities,"user");
            if(validation.Assertion is null || validation.Status=="rejected")throw new InvalidDataException("Recovered assertion failed current validation: "+validation.Reason);
            sources.AddRange(validation.Assertion.Sources);candidates.Add(extracted);reasons.Add(validation.Reason);
        }
        fact=fact with {Sources=sources};
        var proposals=new List<EntityProposalValidation>();
        foreach(var entity in missingEntities)
        {
            EntityProposalValidation? proposal=null;
            using var mentions=Command(c,t,"SELECT evidence_id,evidence_revision,start_utf16,end_utf16 FROM quarantine.mentions WHERE namespace_id=@n AND entity_id=@entity ORDER BY evidence_id,evidence_revision,start_utf16",("@n",space),("@entity",entity.Id));
            using var rows=mentions.ExecuteReader();
            while(rows.Read())
            {
                var reference=new EvidenceRef(space,rows.GetString(0),rows.GetInt64(1));
                if(!sourceEvidence.TryGetValue(reference,out var evidence))continue;
                var start=rows.GetInt32(2);var end=rows.GetInt32(3);
                if(start<0 || end>evidence.Text.Length || end<=start || evidence.Text[start..end]!=entity.Name)continue;
                var validation=EntityProposalValidator.Validate(evidence,new("new:recovery",entity.Name,entity.Type,start,end,entity.Name));
                if(validation.Entity is null || validation.Source is null)continue;
                // Preserve the explicitly selected same-lineage identity; never resolve a same-name entity automatically.
                proposal=validation with {LocalId="new:recovery-"+proposals.Count,Entity=entity,Reason="recovery-existing-identity-needs-review"};break;
            }
            if(proposal is null)throw new InvalidDataException("Missing entity has no exact name mention in the verified fact sources.");
            proposals.Add(proposal);
        }
        var stored=new StoredExtraction(candidates,[new("candidate","explicit-recovery-review-required",fact)],proposals);
        var output=JsonSerializer.Serialize(stored);
        var epoch=Scalar("SELECT privacy_epoch FROM main.namespaces WHERE namespace_id=@n");
        var inputHash=Hash(output+"\u001f"+epoch+"\u001f"+revision+"\u001f"+AssertionValidator.Version);
        var jobId="recovery:"+Hash(space+"\u001f"+inputHash);
        using(var existing=Command(c,t,"SELECT status FROM extraction_jobs WHERE job_id=@job",("@job",jobId)))
            if(existing.ExecuteScalar() is string status)
            {if(status!="completed")throw new InvalidOperationException("Recovery review was invalidated.");return new(jobId,assertionId,reasons,true);}
        Execute(c,t,"""
            INSERT INTO extraction_jobs(job_id,namespace_id,evidence_id,evidence_revision,input_hash,extractor_version,validator_version,model,status,started_at,finished_at,elapsed_ms,input_characters,output_json,prompt_tokens,completion_tokens)
            VALUES(@job,@n,@e,@r,@hash,'recovery-existing-fact-v1',@validator,'no-model','completed',@now,@now,0,@chars,@output,0,0)
            """,("@job",jobId),("@n",space),("@e",sources[0].Reference.Id),("@r",sources[0].Reference.Revision),("@hash",inputHash),("@validator",AssertionValidator.Version),("@now",Now()),("@chars",sources.Sum(s=>s.Quote.Length)),("@output",output));
        t.Commit();return new(jobId,assertionId,reasons,false);
    }
}
