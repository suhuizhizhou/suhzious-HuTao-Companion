namespace HuTao.Knowledge.Retrieval;

/// <summary>The existing planner selects bounded originals; execution only resolves host-bound references.</summary>
public sealed class SqlSemanticStrategy:IKnowledgeRetrievalStrategy
{
    public KnowledgeStrategy Name=>KnowledgeStrategy.SqlSemantic;
    public Task<KnowledgeStrategyResult> RetrieveAsync(KnowledgeAccessContext access,KnowledgeStrategyRequest request,RecallBudget budget,CancellationToken ct)
    {
        access.Demand(request.Namespace);ct.ThrowIfCancellationRequested();
        string[] trace=["backend=sqlite;strategy=SqlSemantic;selector=recall-planner;execution-model-calls=0"];
        if(request.Namespace!=access.MemoryNamespace || request.Query.TimeMode!=KnowledgeTimeMode.Current)
            return Task.FromResult(new KnowledgeStrategyResult([],[],["semantic-scope-not-supported"],trace));
        var selected=request.SemanticReferences??[];
        if(selected.Count>4 || selected.Distinct().Count()!=selected.Count || selected.Any(r=>r.Namespace!=access.MemoryNamespace || request.ExcludedLexicalIds?.Contains(r.Id)==true))
            throw new InvalidDataException("Invalid host-bound semantic references.");
        return Task.FromResult(new KnowledgeStrategyResult(selected.Select((r,i)=>new KnowledgeCandidate(r,"sql-semantic",i+1)).ToArray(),[],
            selected.Count==0?["no-planned-semantic-evidence"]:[],trace));
    }
}
