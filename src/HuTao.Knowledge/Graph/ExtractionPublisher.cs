using System.Text.Json;
using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Graph;

public sealed record ExtractionPublication(int Assertions,bool AlreadyPublished);

/// <summary>Host review decision, never model output. Selection, entities, assertions and ledger commit together.</summary>
public sealed class ExtractionPublisher(SqliteKnowledgeDatabase database)
{
    public bool HasSourceDecision(KnowledgeAccessContext access,EvidenceRef reference)
    {
        access.Demand(reference.Namespace);
        using var c=database.Open();
        using var command=Command(c,null,"SELECT 1 FROM extraction_publications p JOIN extraction_jobs j ON j.job_id=p.job_id WHERE j.namespace_id=@n AND j.evidence_id=@e AND j.evidence_revision=@r LIMIT 1",
            ("@n",reference.Namespace),("@e",reference.Id),("@r",reference.Revision));
        return command.ExecuteScalar() is not null;
    }
    public bool HasDecision(KnowledgeAccessContext access,string jobId)
    {
        using var c=database.Open();
        using var command=Command(c,null,"SELECT j.namespace_id FROM extraction_publications p JOIN extraction_jobs j ON j.job_id=p.job_id WHERE p.job_id=@id",("@id",jobId));
        if(command.ExecuteScalar() is not string space)return false;
        access.Demand(space);return true;
    }
    public int Retract(KnowledgeAccessContext access,string jobId,CancellationToken ct=default)
    {
        return database.Write((c,t)=>{
            string space;string[] ids;
            using(var cmd=Command(c,t,"SELECT j.namespace_id,p.assertion_ids_json,p.retracted_at FROM extraction_publications p JOIN extraction_jobs j ON j.job_id=p.job_id WHERE p.job_id=@id",("@id",jobId)))
            using(var row=cmd.ExecuteReader())
            {
                if(!row.Read())throw new InvalidOperationException("Publication not found.");
                space=row.GetString(0);access.Demand(space);
                if(!row.IsDBNull(2))return 0;
                ids=JsonSerializer.Deserialize<string[]>(row.GetString(1))??throw new InvalidDataException("Invalid publication ledger.");
            }
            var at=Now();
            foreach(var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                using var latest=Command(c,t,"SELECT MAX(revision) FROM assertions WHERE namespace_id=@n AND assertion_id=@id",("@n",space),("@id",id));
                var revision=Convert.ToInt64(latest.ExecuteScalar());
                Execute(c,t,"UPDATE assertions SET retired_at=@at WHERE namespace_id=@n AND assertion_id=@id AND revision=@r",("@at",at),("@n",space),("@id",id),("@r",revision));
                Execute(c,t,"""
                    INSERT INTO assertions SELECT namespace_id,assertion_id,revision+1,subject_id,predicate_id,object_id,typed_literal,polarity,qualifiers_json,
                    valid_from,valid_to,time_basis,'retracted',extraction_confidence,validation_method,superseded_by,@at,NULL
                    FROM assertions WHERE namespace_id=@n AND assertion_id=@id AND revision=@r
                    """,("@at",at),("@n",space),("@id",id),("@r",revision));
                Execute(c,t,"""
                    INSERT INTO assertion_evidence SELECT namespace_id,assertion_id,assertion_revision+1,evidence_id,evidence_revision,start_utf16,end_utf16,support_kind
                    FROM assertion_evidence WHERE namespace_id=@n AND assertion_id=@id AND assertion_revision=@r
                    """,("@n",space),("@id",id),("@r",revision));
                GraphOutboxRepository.Enqueue(c,t,space,id,revision+1,"assertion-retract");
            }
            Execute(c,t,"UPDATE extraction_publications SET retracted_at=@at WHERE job_id=@id",("@at",at),("@id",jobId));
            return ids.Length;
        },ct);
    }

    public ExtractionPublication Publish(KnowledgeAccessContext access,string jobId,IReadOnlySet<string> selectedIds,
        string reviewerId,bool approveCandidates=false,CancellationToken ct=default,IReadOnlyDictionary<string,string>? supersedes=null,long? expectedCommitSequence=null,TurnIngestionLease? ingestionLease=null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerId);
        if(selectedIds.Count>6)throw new ArgumentException("Publication exceeds extraction bound.");
        var selectedJson=JsonSerializer.Serialize(selectedIds.Order(StringComparer.Ordinal));
        if(supersedes is not null && (supersedes.Keys.Any(id=>!selectedIds.Contains(id)) || supersedes.Values.Distinct(StringComparer.Ordinal).Count()!=supersedes.Count))
            throw new ArgumentException("Invalid correction selection.");
        if(expectedCommitSequence<0 || supersedes?.Any(p=>string.IsNullOrWhiteSpace(p.Key)||string.IsNullOrWhiteSpace(p.Value))==true)
            throw new ArgumentException("Invalid correction identifiers or version.");
        if(supersedes is not null && expectedCommitSequence is null)throw new ArgumentException("Correction requires a source snapshot sequence.");
        var decisionHash=Hash(selectedJson+"\u001f"+reviewerId+"\u001f"+approveCandidates+(supersedes is null?"":"\u001f"+JsonSerializer.Serialize(supersedes.OrderBy(p=>p.Key,StringComparer.Ordinal))));
        return database.Write((c,t)=>{
            string space;StoredExtraction stored;
            using(var job=Command(c,t,"SELECT namespace_id,output_json FROM extraction_jobs WHERE job_id=@id AND status='completed'",("@id",jobId)))
            using(var row=job.ExecuteReader())
            {
                if(!row.Read())throw new InvalidOperationException("Only completed extraction jobs can be reviewed.");
                space=row.GetString(0);access.Demand(space);
                stored=JsonSerializer.Deserialize<StoredExtraction>(row.GetString(1))??throw new InvalidDataException("Invalid extraction output.");
            }
            using(var existing=Command(c,t,"SELECT decision_hash,retracted_at FROM extraction_publications WHERE job_id=@id",("@id",jobId)))
            using(var row=existing.ExecuteReader())
                if(row.Read())
                {
                    if(row.GetString(0)!=decisionHash || !row.IsDBNull(1))throw new InvalidOperationException("Publication decision is immutable or retracted.");
                    return new ExtractionPublication(selectedIds.Count,true);
                }
            if(expectedCommitSequence is { } expected)
            {
                using var sequence=Command(c,t,"SELECT commit_seq FROM namespace_state WHERE namespace_id=@n",("@n",space));
                if(Convert.ToInt64(sequence.ExecuteScalar())!=expected)throw new InvalidOperationException("Fact state changed after correction planning.");
            }
            var selected=stored.Validations.Where(v=>v.Assertion is not null && selectedIds.Contains(v.Assertion.Id)).ToArray();
            if(ingestionLease is { } lease)
            {
                if(lease.Reference.Namespace!=space || selected.Any(v=>v.Assertion!.Sources.Any(s=>s.Reference!=lease.Reference)))throw new InvalidDataException("Ingestion source mismatch.");
                using var liveLease=Command(c,t,"SELECT 1 FROM turn_ingestion WHERE namespace_id=@n AND evidence_id=@e AND evidence_revision=@r AND status='processing' AND lease_token=@token AND lease_until>@at",
                    ("@n",space),("@e",lease.Reference.Id),("@r",lease.Reference.Revision),("@token",lease.Token),("@at",Now()));
                if(liveLease.ExecuteScalar() is null)throw new InvalidOperationException("Ingestion lease expired or cancelled.");
            }
            if(selected.Length!=selectedIds.Count || selected.Any(v=>v.Status!="accepted" && !(approveCandidates && v.Status=="candidate")))
                throw new InvalidDataException("Selection includes unknown, rejected or unreviewed candidates.");
            var referenced=selected.SelectMany(v=>new[]{v.Assertion!.SubjectId,v.Assertion.ObjectId}).Where(id=>id is not null).ToHashSet(StringComparer.Ordinal);
            foreach(var proposal in stored.EntityProposals??[])
            {
                if(proposal.Entity is null || !referenced.Contains(proposal.Entity.Id))continue;
                if(!approveCandidates || proposal.Status!="candidate" || proposal.Source is null)throw new InvalidDataException("New entity requires explicit review.");
                var entity=proposal.Entity;var source=proposal.Source;
                if(source.Reference.Namespace!=space)throw new UnauthorizedAccessException("Entity source outside publication namespace.");
                if(entity.Name!=source.Quote || !SqlAssertionRepository.SourceValid(c,t,source,Now()))throw new InvalidDataException("Entity mention changed before publication.");
                Execute(c,t,"INSERT OR IGNORE INTO entities(namespace_id,entity_id,type,canonical_name) VALUES(@n,@id,@type,@name)",("@n",space),("@id",entity.Id),("@type",entity.Type),("@name",entity.Name));
                using(var identity=Command(c,t,"SELECT 1 FROM entities WHERE namespace_id=@n AND entity_id=@id AND type=@type AND canonical_name=@name AND status='active'",("@n",space),("@id",entity.Id),("@type",entity.Type),("@name",entity.Name)))
                    if(identity.ExecuteScalar() is null)throw new InvalidDataException("Entity identity changed before publication.");
                Execute(c,t,"INSERT OR IGNORE INTO mentions VALUES(@n,@e,@r,@entity,@start,@end)",("@n",space),("@e",source.Reference.Id),("@r",source.Reference.Revision),("@entity",entity.Id),("@start",source.StartUtf16),("@end",source.EndUtf16));
            }
            var repository=new SqlAssertionRepository(database);var at=DateTimeOffset.UtcNow;
            foreach(var validation in selected)
            {
                ct.ThrowIfCancellationRequested();
                var assertion=validation.Assertion!;
                var previous=supersedes?.GetValueOrDefault(assertion.Id);
                repository.PutInTransaction(c,t,access,space,assertion with {Status="accepted",ValidFrom=previous is null?assertion.ValidFrom:at,ValidationMethod=validation.Status=="accepted"?assertion.ValidationMethod:"reviewed:"+reviewerId},at,previous);
            }
            Execute(c,t,"INSERT INTO extraction_publications VALUES(@id,@hash,@reviewer,@at,@ids,NULL)",("@id",jobId),("@hash",decisionHash),("@reviewer",reviewerId),("@at",at.ToUnixTimeMilliseconds()),("@ids",selectedJson));
            return new ExtractionPublication(selected.Length,false);
        },ct);
    }
}
