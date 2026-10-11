using System.Diagnostics;
using System.Text.Json;
using HuTao.Foundation.Abstractions;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Graph;

public sealed class ExtractionBudget(int maxCalls=20,int maxSourceCharacters=80000,int maxReservedTokens=400000)
{
    private readonly object _gate=new();
    public int Calls {get;private set;}
    public int Units {get;private set;}
    public int SourceCharacters {get;private set;}
    public int ReservedTokens {get;private set;}
    public void Reserve(int characters,int tokenReservation,bool remote=true)
    {
        lock(_gate)
        {
            if(characters<0 || tokenReservation<0 || Units>=maxCalls || SourceCharacters+characters>maxSourceCharacters || ReservedTokens+tokenReservation>maxReservedTokens)
                throw new InvalidOperationException("Extraction batch budget exhausted.");
            Units++;if(remote)Calls++;SourceCharacters+=characters;ReservedTokens+=tokenReservation;
        }
    }
}
public sealed record ExtractionJobResult(string JobId,bool Cached,IReadOnlyList<AssertionValidation> Validations,long ElapsedMs,IReadOnlyList<ExtractedAssertion> Candidates,IReadOnlyList<EntityProposalValidation>? EntityProposals=null,IReadOnlyList<ExtractedEntity>? RawEntities=null);
public sealed record StoredExtraction(IReadOnlyList<ExtractedAssertion> Candidates,IReadOnlyList<AssertionValidation> Validations,IReadOnlyList<EntityProposalValidation>? EntityProposals=null,IReadOnlyList<ExtractedEntity>? RawEntities=null);

/// <summary>Durable cache per evidence revision and exact inputs; uncertain calls are not automatically billed again.</summary>
public sealed class ExtractionJobRunner(SqliteKnowledgeDatabase database,ILLMProvider llm)
{
    public async Task<ExtractionJobResult> RunAsync(KnowledgeAccessContext access,EvidenceRef reference,
        IReadOnlyList<ExtractionEntity> entities,string? speakerEntityId,ExtractionBudget budget,CancellationToken ct=default)
    {
        access.Demand(reference.Namespace);ct.ThrowIfCancellationRequested();
        if(entities.Count>40 || entities.Select(e=>e.Id).Distinct(StringComparer.Ordinal).Count()!=entities.Count)
            throw new ArgumentException("Invalid entity catalog.");
        var repository=new SqlEvidenceRepository(database);
        var privacyEpoch=repository.PrivacyEpochs(access)[reference.Namespace];
        var evidence=repository.Read(access,reference)??throw new InvalidDataException("Extraction evidence is not current.");
        if(evidence.Text.Length>4000)throw new ArgumentException("Extraction source too long.");
        var deterministic=DeterministicAssertionRules.Extract(evidence,speakerEntityId);
        var modelIdentity=deterministic.Count>0?"deterministic:"+DeterministicAssertionRules.Version:llm is IMeteredLLMProvider metered?metered.ModelIdentity:llm.Name;
        var serialized=JsonSerializer.Serialize(new {evidence,entities,speakerEntityId,privacyEpoch,model=modelIdentity,extractor=AssertionExtractor.Version,validator=AssertionValidator.Version,catalog=PredicateCatalog.Version,rules=DeterministicAssertionRules.Version,entityValidator=EntityProposalValidator.Version});
        var inputHash=Hash(serialized);
        var jobId=Hash(reference.Namespace+"\u001f"+reference.Id+"\u001f"+inputHash);
        using(var c=database.Open())
        using(var cmd=Command(c,null,"SELECT status,output_json,elapsed_ms FROM extraction_jobs WHERE job_id=@id",("@id",jobId)))
        using(var row=cmd.ExecuteReader())
        {
            if(row.Read())
            {
                if(row.GetString(0)!="completed")throw new InvalidOperationException("Extraction has an incomplete or failed attempt; inspect before explicitly retrying.");
                var stored=JsonSerializer.Deserialize<StoredExtraction>(row.GetString(1))??throw new InvalidDataException("Invalid cached extraction.");
                return new(jobId,true,stored.Validations,row.GetInt64(2),stored.Candidates,stored.EntityProposals,stored.RawEntities);
            }
        }
        // Conservative byte-based reservation plus fixed prompt/framing allowance; actual usage is recorded separately.
        budget.Reserve(evidence.Text.Length,deterministic.Count>0?0:System.Text.Encoding.UTF8.GetByteCount(serialized)+8192+(llm is IMeteredLLMProvider bounded?bounded.MaxOutputTokens:32000),remote:deterministic.Count==0);
        database.Write((c,t)=>Execute(c,t,"""
            INSERT INTO extraction_jobs(job_id,namespace_id,evidence_id,evidence_revision,input_hash,extractor_version,validator_version,model,status,started_at,input_characters)
            VALUES(@id,@n,@e,@r,@hash,@extractor,@validator,@model,'started',@at,@chars)
            """,("@id",jobId),("@n",reference.Namespace),("@e",reference.Id),("@r",reference.Revision),("@hash",inputHash),
            ("@extractor",AssertionExtractor.Version),("@validator",AssertionValidator.Version),("@model",modelIdentity),("@at",Now()),("@chars",evidence.Text.Length)),ct);
        var timer=Stopwatch.StartNew();
        try
        {
            var extraction=deterministic.Count>0?new ExtractionResponse(deterministic,new("",0,0,0,modelIdentity)):await new AssertionExtractor(llm).ExtractAsync(evidence,entities,ct,usage=>
                database.Write((c,t)=>Execute(c,t,"UPDATE extraction_jobs SET prompt_tokens=@pt,completion_tokens=@ct WHERE job_id=@id",
                    ("@pt",usage.PromptTokens),("@ct",usage.CompletionTokens),("@id",jobId)))).ConfigureAwait(false);
            var proposals=(extraction.Entities??[]).Select(p=>EntityProposalValidator.Validate(evidence,p)).ToArray();
            if(proposals.Select(p=>p.LocalId).Distinct(StringComparer.Ordinal).Count()!=proposals.Length || proposals.Any(p=>entities.Any(e=>e.Id==p.LocalId)))
                throw new InvalidDataException("Duplicate or colliding proposed entity ID.");
            var mappings=proposals.Where(p=>p.Entity is not null).ToDictionary(p=>p.LocalId,p=>p.Entity!,StringComparer.Ordinal);
            var available=entities.Concat(mappings.Values).DistinctBy(e=>e.Id,StringComparer.Ordinal).ToArray();
            string Remap(string? id)=>id is null?"":mappings.TryGetValue(id,out var entity)?entity.Id:id;
            var validations=extraction.Candidates.Select((candidate,i)=>AssertionValidator.Validate("assertion:"+Hash(jobId+":"+i),evidence,
                candidate with {SubjectId=Remap(candidate.SubjectId),ObjectId=candidate.ObjectId is null?null:Remap(candidate.ObjectId)},available,speakerEntityId)).ToArray();
            ct.ThrowIfCancellationRequested();
            if(repository.Read(access,reference)!=evidence)throw new InvalidDataException("Evidence changed during extraction.");
            database.Write((c,t)=>{
                using(var epoch=Command(c,t,"SELECT privacy_epoch FROM namespaces WHERE namespace_id=@n",("@n",reference.Namespace)))
                    if(Convert.ToInt64(epoch.ExecuteScalar())!=privacyEpoch)throw new InvalidDataException("Privacy changed during extraction.");
                using var live=Command(c,t,"SELECT 1 FROM evidence_units WHERE namespace_id=@n AND evidence_id=@e AND revision=@r AND deleted_at IS NULL",("@n",reference.Namespace),("@e",reference.Id),("@r",reference.Revision));
                if(live.ExecuteScalar() is null)throw new InvalidDataException("Evidence deleted during extraction.");
                var written=Execute(c,t,"UPDATE extraction_jobs SET status='completed',finished_at=@at,elapsed_ms=@ms,output_json=@output,prompt_tokens=@pt,completion_tokens=@ct WHERE job_id=@id AND status='started'",("@at",Now()),("@ms",timer.ElapsedMilliseconds),("@output",JsonSerializer.Serialize(new StoredExtraction(extraction.Candidates,validations,proposals,extraction.Entities))),("@pt",extraction.Usage?.PromptTokens),("@ct",extraction.Usage?.CompletionTokens),("@id",jobId));
                if(written!=1)throw new InvalidDataException("Extraction job invalidated before commit.");
                return written;
            },ct);
            return new(jobId,false,validations,timer.ElapsedMilliseconds,extraction.Candidates,proposals,extraction.Entities);
        }
        catch(Exception ex)
        {
            database.Write((c,t)=>Execute(c,t,"UPDATE extraction_jobs SET status=@status,finished_at=@at,elapsed_ms=@ms,error_code=@error WHERE job_id=@id AND status='started'",("@status",ex is OperationCanceledException?"interrupted":"failed"),("@at",Now()),("@ms",timer.ElapsedMilliseconds),("@error",ex.GetType().Name),("@id",jobId)));
            throw;
        }
    }
}
