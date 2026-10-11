using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Graph;

namespace HuTao.Knowledge.Retrieval;

public sealed class GraphExpandStrategy(GraphQueryCatalog graph, SqlAssertionRepository repository) : IKnowledgeRetrievalStrategy
{
    public KnowledgeStrategy Name => KnowledgeStrategy.GraphExpand;
    public async Task<KnowledgeStrategyResult> RetrieveAsync(KnowledgeAccessContext access, KnowledgeStrategyRequest request, RecallBudget budget, CancellationToken ct)
    {
        access.Demand(request.Namespace); ct.ThrowIfCancellationRequested();
        if(!Enum.IsDefined(request.Query.TimeMode) || request.Query.TimeMode==KnowledgeTimeMode.Current &&
            (request.Query.AsOf is not null || request.Query.KnownAt is not null))throw new ArgumentException("Invalid graph time mode.");
        async Task<KnowledgeStrategyResult> Fallback(string reason)
        {
            var result = await new SqlFactsStrategy(repository).RetrieveAsync(access, request, budget, ct).ConfigureAwait(false);
            return result with { Gaps = result.Gaps.Prepend(reason).ToArray(), Trace = result.Trace.Prepend("requested=GraphExpand;fallback=SqlFacts").ToArray() };
        }
        if (request.Query.TimeMode != KnowledgeTimeMode.Current) return await Fallback("graph-history-requires-sql");
        if (request.GraphSeeds is not { Count: > 0 } || request.GraphPredicates is not { Count: > 0 }) return await Fallback("missing-resolved-graph-binding");
        var found = await graph.ExpandAsync(access, request.Namespace, request.GraphSeeds, request.GraphPredicates, budget, ct: ct).ConfigureAwait(false);
        if (found.Paths.Count == 0) return await Fallback(found.Gaps.FirstOrDefault() ?? "no-graph-path");
        if (!budget.ReserveQuery()) return new([], [], ["sql-hydration-budget-exhausted"], ["backend=neo4j;unhydrated-paths-discarded"]);
        var ids = found.Paths.SelectMany(p => p.Edges).Select(e => e.AssertionId).Distinct(StringComparer.Ordinal).ToArray();
        var verified = repository.Query(access, request.Namespace, new("", Limit:100, AssertionIds:ids),ct,budget.SqlReadRemaining).ToDictionary(x => x.Id);
        ct.ThrowIfCancellationRequested();
        var paths = new List<KnowledgeFactPath>(); var gaps = found.Gaps.ToList();
        foreach (var path in found.Paths)
        {
            if (path.Edges.Any(e => !verified.TryGetValue(e.AssertionId, out var fact) || fact.Revision != e.Revision ||
                fact.Status != "accepted" || fact.Polarity != 1 || fact.SubjectId != e.SubjectId ||
                fact.PredicateId != e.PredicateId || fact.ObjectId != e.ObjectId))
            { gaps.Add("graph-path-failed-sql-validation"); continue; }
            paths.Add(new(request.Namespace, path.Edges.Select(e => verified[e.AssertionId]).ToArray()));
        }
        var facts = paths.SelectMany(x => x.Facts).DistinctBy(x => (x.Id,x.Revision)).ToArray();
        var candidates = facts.SelectMany((fact, index) => fact.Sources.Select(s => new KnowledgeCandidate(s.Reference,"graph-expand",index+1,[fact.Id])))
            .GroupBy(x => x.Reference).Select(g => new KnowledgeCandidate(g.Key,"graph-expand",g.Min(x => x.Rank),g.SelectMany(x => x.AssertionIds!).Distinct().ToArray())).ToArray();
        return new(candidates, facts, gaps.Distinct().ToArray(), ["backend=neo4j+sqlite;strategy=GraphExpand;generation="+found.Generation], paths);
    }
}
