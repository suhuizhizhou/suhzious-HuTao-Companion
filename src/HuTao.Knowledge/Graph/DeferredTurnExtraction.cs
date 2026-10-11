using HuTao.Foundation.Abstractions;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Graph;

public sealed record DeferredTurnSummary(EvidenceRef Reference,string Status,int Attempts,string? Diagnostic);

/// <summary>Explicit, bounded host operation. This creates reviewable jobs, never publishes model claims.</summary>
public sealed class DeferredTurnExtraction(SqliteKnowledgeDatabase database,ILLMProvider provider)
{
    public IReadOnlyList<DeferredTurnSummary> List(KnowledgeAccessContext access)
    {
        var space=access.MemoryNamespace;access.Demand(space);
        using var c=database.Open();
        using var command=Command(c,null,"SELECT evidence_id,evidence_revision,status,attempts,diagnostic FROM turn_ingestion WHERE namespace_id=@n AND status IN ('deferred','failed') ORDER BY created_at,evidence_id LIMIT 100",("@n",space));
        using var rows=command.ExecuteReader();var result=new List<DeferredTurnSummary>();
        while(rows.Read())result.Add(new(new(space,rows.GetString(0),rows.GetInt64(1)),rows.GetString(2),rows.GetInt32(3),rows.IsDBNull(4)?null:rows.GetString(4)));
        return result;
    }

    public async Task<ExtractionJobResult> ExtractAsync(KnowledgeAccessContext access,EvidenceRef reference,ExtractionBudget budget,CancellationToken ct=default)
    {
        if(reference.Namespace!=access.MemoryNamespace)throw new UnauthorizedAccessException("Only the current user's memory source can be extracted.");
        var evidence=new SqlEvidenceRepository(database).Read(access,reference)??throw new InvalidDataException("Source is not current.");
        if(evidence.Speaker!="user" || evidence.Text.Length>4000)throw new InvalidDataException("Unsupported turn source.");
        var entities=new List<ExtractionEntity>();
        using(var c=database.Open())using(var command=Command(c,null,"SELECT entity_id,type,canonical_name FROM entities WHERE namespace_id=@n AND status='active' ORDER BY entity_id LIMIT 40",("@n",reference.Namespace)))
        using(var rows=command.ExecuteReader())while(rows.Read())entities.Add(new(rows.GetString(0),rows.GetString(1),rows.GetString(2)));
        if(!entities.Any(e=>e.Id=="user" && e.Type=="person"))throw new InvalidDataException("Host user entity is missing.");
        var queue=new TurnIngestionQueue(database);
        var lease=queue.Claim(access,ct,reference,includeDeferred:true)??throw new InvalidOperationException("Selected turn is not claimable; inspect its state before retrying.");
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var result=await new ExtractionJobRunner(database,provider).RunAsync(access,reference,entities,"user",budget,deadline.Token).ConfigureAwait(false);
            if(!queue.Complete(access,lease,"deferred"))throw new InvalidOperationException("Extraction lease or source changed; result must be revalidated before review.");
            return result;
        }
        catch
        {
            queue.Complete(access,lease,"failed");
            throw;
        }
    }
}
