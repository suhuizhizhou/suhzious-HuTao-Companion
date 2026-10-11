using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Storage;

namespace HuTao.Knowledge.Retrieval;

/// <summary>Namespace and resolved entity bindings are supplied by the host, not deserialized from a model plan.</summary>
public sealed record KnowledgeStrategyRequest(KnowledgeQuery Query,string Namespace,FactQuery? Facts=null,
    IReadOnlyList<string>? GraphSeeds=null,IReadOnlyList<string>? GraphPredicates=null,IReadOnlyList<string>? ExcludedLexicalIds=null,
    IReadOnlyList<EvidenceRef>? SemanticReferences=null);
public sealed record KnowledgeFactPath(string Namespace,IReadOnlyList<AssertionFact> Facts);
public sealed record KnowledgeStrategyResult(IReadOnlyList<KnowledgeCandidate> Candidates,IReadOnlyList<AssertionFact> Facts,
    IReadOnlyList<string> Gaps,IReadOnlyList<string> Trace,IReadOnlyList<KnowledgeFactPath>? Paths=null);

public interface IKnowledgeRetrievalStrategy
{
    KnowledgeStrategy Name { get; }
    Task<KnowledgeStrategyResult> RetrieveAsync(KnowledgeAccessContext access,KnowledgeStrategyRequest request,RecallBudget budget,CancellationToken ct);
}

public sealed class SqlLexicalStrategy(SqlEvidenceRepository repository) : IKnowledgeRetrievalStrategy
{
    public KnowledgeStrategy Name=>KnowledgeStrategy.SqlLexical;
    public Task<KnowledgeStrategyResult> RetrieveAsync(KnowledgeAccessContext access,KnowledgeStrategyRequest request,RecallBudget budget,CancellationToken ct)
    {
        access.Demand(request.Namespace);ct.ThrowIfCancellationRequested();
        if(request.Query.TimeMode!=KnowledgeTimeMode.Current)
            return Task.FromResult(new KnowledgeStrategyResult([],[],["historical-lexical-not-supported"],["backend=sqlite;strategy=SqlLexical"]));
        if(!budget.ReserveQuery())return Task.FromResult(new KnowledgeStrategyResult([],[],["query-budget-exhausted"],["backend=sqlite;strategy=SqlLexical"]));
        var candidates=repository.Search(access,request.Query.Text,onlyNamespace:request.Namespace,ct:ct,excludedIds:request.ExcludedLexicalIds,duration:budget.SqlReadRemaining);
        if(request.Namespace.StartsWith("corpus:",StringComparison.Ordinal) && candidates.Count>0 && budget.QueriesRemaining>1 && budget.ReserveQuery())
            candidates=candidates.Concat(repository.Neighbors(access,candidates.Take(5).Select(c=>c.Reference).ToArray(),ct,budget.SqlReadRemaining)).ToArray();
        return Task.FromResult(new KnowledgeStrategyResult(candidates,[],candidates.Count==0?["no-lexical-evidence"]:[],["backend=sqlite;strategy=SqlLexical;tokenizer=story-bigrams-v1"]));
    }
}

public sealed class SqlFactsStrategy(SqlAssertionRepository repository) : IKnowledgeRetrievalStrategy
{
    public KnowledgeStrategy Name=>KnowledgeStrategy.SqlFacts;
    public Task<KnowledgeStrategyResult> RetrieveAsync(KnowledgeAccessContext access,KnowledgeStrategyRequest request,RecallBudget budget,CancellationToken ct)
    {
        access.Demand(request.Namespace);ct.ThrowIfCancellationRequested();
        if(request.Facts is null)return Task.FromResult(new KnowledgeStrategyResult([],[],["missing-resolved-fact-binding"],["backend=sqlite;strategy=SqlFacts"]));
        if(!budget.ReserveQuery())return Task.FromResult(new KnowledgeStrategyResult([],[],["query-budget-exhausted"],["backend=sqlite;strategy=SqlFacts"]));
        var query=request.Facts with {TimeMode=request.Query.TimeMode,AsOf=request.Query.AsOf,KnownAt=request.Query.KnownAt};
        var facts=repository.Query(access,request.Namespace,query,ct,budget.SqlReadRemaining);
        ct.ThrowIfCancellationRequested();
        var candidates=facts.SelectMany((fact,index)=>fact.Sources.Select(source=>new KnowledgeCandidate(source.Reference,"sql-facts",index+1,[fact.Id])))
            .GroupBy(c=>c.Reference).Select(g=>new KnowledgeCandidate(g.Key,"sql-facts",g.Min(c=>c.Rank),g.SelectMany(c=>c.AssertionIds??[]).Distinct(StringComparer.Ordinal).ToArray())).ToArray();
        var gaps=new List<string>();
        if(facts.Count==0)gaps.Add("no-supported-facts");
        foreach(var fact in facts.Where(f=>f.Status=="disputed"))gaps.Add("disputed:"+fact.Id);
        return Task.FromResult(new KnowledgeStrategyResult(candidates,facts,gaps,["backend=sqlite;strategy=SqlFacts;validity=bitemporal-v1"]));
    }
}

public sealed class KnowledgeStrategyCatalog(IEnumerable<IKnowledgeRetrievalStrategy> strategies)
{
    private readonly IReadOnlyDictionary<KnowledgeStrategy,IKnowledgeRetrievalStrategy> _strategies=strategies.ToDictionary(s=>s.Name);
    public IReadOnlyCollection<KnowledgeStrategy> Available=>_strategies.Keys.ToArray();
    public IKnowledgeRetrievalStrategy Demand(KnowledgeStrategy name)=>_strategies.TryGetValue(name,out var strategy)
        ?strategy:throw new InvalidOperationException("Requested knowledge strategy is unavailable.");
}
