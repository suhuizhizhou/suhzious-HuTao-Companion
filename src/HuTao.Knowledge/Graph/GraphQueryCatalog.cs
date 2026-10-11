using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using Neo4j.Driver;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Graph;

public sealed record GraphPathEdge(string AssertionId, long Revision, string SubjectId, string PredicateId, string ObjectId);
public sealed record GraphPath(IReadOnlyList<GraphPathEdge> Edges);
public sealed record GraphQueryResult(IReadOnlyList<GraphPath> Paths, IReadOnlyList<string> Gaps, string Generation);

/// <summary>Forward semantic hops only. All returned identifiers still require authoritative SQL hydration.</summary>
public sealed class GraphQueryCatalog(SqliteKnowledgeDatabase database, IDriver driver)
{
    public async Task<GraphQueryResult> ExpandAsync(KnowledgeAccessContext access, string space,
        IReadOnlyList<string> seeds, IReadOnlyList<string> predicates, RecallBudget budget, int hops = 2,
        CancellationToken ct = default)
    {
        access.Demand(space); ct.ThrowIfCancellationRequested();
        if (hops is < 1 or > 2 || seeds.Count is < 1 or > 10 || seeds.Any(string.IsNullOrWhiteSpace) ||
            predicates.Count is < 1 or > 10 || predicates.Any(x => !PredicateCatalog.Entries.ContainsKey(x)))
            throw new ArgumentException("Graph query exceeds the host template bounds.");
        (string Generation, long Sequence, long Checkpoint, long Epoch,long RequiredSequence) State()
        {
            return database.Read(c=>
            {
            using var command = Command(c, null, """
                SELECT graph_generation,commit_seq,graph_checkpoint,privacy_epoch,
                    COALESCE((SELECT MAX(e.commit_seq) FROM graph_outbox e WHERE e.namespace_id=s.namespace_id AND e.generation=s.graph_generation),0)
                FROM namespace_state s JOIN namespaces USING(namespace_id) WHERE namespace_id=@n
                """, ("@n", space));
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new InvalidDataException("Namespace state missing.");
            return (row.GetString(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3),row.GetInt64(4));
            },ct,budget.SqlReadRemaining);
        }
        if(budget.Remaining<=TimeSpan.Zero)return new([], ["query-budget-exhausted"], "");
        (string Generation,long Sequence,long Checkpoint,long Epoch,long RequiredSequence) state;
        try{state=State();}
        catch(TimeoutException){return new([], ["sql-read-deadline"], "");}
        if (state.Checkpoint < state.RequiredSequence) return new([], ["graph-behind-sql"], state.Generation);
        if (budget.Remaining <= TimeSpan.Zero) return new([], ["query-budget-exhausted"], state.Generation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget.Remaining < TimeSpan.FromSeconds(2) ? budget.Remaining : TimeSpan.FromSeconds(2));
        var paths = new List<GraphPath>(); var gaps = new List<string>();
        var frontier = seeds.Distinct(StringComparer.Ordinal).Select(x => (Entity: x, Edges: new List<GraphPathEdge>())).ToList();
        var expanded = 0;
        try
        {
            await using var session = driver.AsyncSession();
            for (var depth = 0; depth < hops && frontier.Count > 0 && paths.Count < 40 && expanded < 200; depth++)
            {
                if (!budget.ReserveQuery()) { gaps.Add("query-budget-exhausted"); break; }
                var queryTime=budget.Remaining;
                if(queryTime<TimeSpan.FromMilliseconds(1)){gaps.Add("query-budget-exhausted");break;}
                if(queryTime>TimeSpan.FromMilliseconds(500))queryTime=TimeSpan.FromMilliseconds(500);
                var ids = frontier.Select(x => x.Entity).Distinct(StringComparer.Ordinal).ToArray();
                var limit = Math.Min(100, 200 - expanded);
                var edges = await session.ExecuteReadAsync(async tx =>
                {
                    var cursor = await tx.RunAsync("""
                        MATCH (s:HuTaoEntity {namespace_id:$ns,generation:$g})<-[:SUBJECT]-(a:HuTaoAssertion {namespace_id:$ns,generation:$g})
                        WHERE s.id IN $ids
                        WITH DISTINCT s,a LIMIT $limit
                        OPTIONAL MATCH (a)-[:OBJECT]->(o:HuTaoEntity {namespace_id:$ns,generation:$g})
                        RETURN a.id AS id,a.revision AS revision,s.id AS subject,a.predicate_id AS predicate,o.id AS object,
                            a.status AS status,a.polarity AS polarity,a.valid_from AS validFrom,a.valid_to AS validTo
                        LIMIT $limit
                        """, new { ns = space, g = state.Generation, ids, limit });
                    return await cursor.ToListAsync();
                }, config => config.WithTimeout(queryTime)).WaitAsync(deadline.Token).ConfigureAwait(false);
                // LIMIT applies before semantic filtering, so dense rejected edges cannot cause unbounded traversal.
                expanded += edges.Count;
                if (edges.Count == limit) gaps.Add("graph-hop-expansion-limit");
                var next = new List<(string Entity, List<GraphPathEdge> Edges)>();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var row in edges)
                {
                    if (row["object"] is null || row["status"].As<string>() != "accepted" || row["polarity"].As<long>() != 1 ||
                        !predicates.Contains(row["predicate"].As<string>()) ||
                        row["validFrom"] is not null && row["validFrom"].As<long>() > now ||
                        row["validTo"] is not null && row["validTo"].As<long>() <= now) continue;
                    var edge = new GraphPathEdge(row["id"].As<string>(), row["revision"].As<long>(), row["subject"].As<string>(),
                        row["predicate"].As<string>(), row["object"].As<string>());
                    foreach (var parent in frontier.Where(x => x.Entity == edge.SubjectId))
                    {
                        if (edge.ObjectId == edge.SubjectId || parent.Edges.Any(x => x.SubjectId == edge.ObjectId || x.AssertionId == edge.AssertionId)) continue;
                        var chain = parent.Edges.Append(edge).ToList();
                        if (paths.Any(p => p.Edges.SequenceEqual(chain))) continue;
                        paths.Add(new(chain)); next.Add((edge.ObjectId, chain));
                        if (paths.Count >= 40) break;
                    }
                    if (paths.Count >= 40) break;
                }
                frontier = next;
            }
            if (paths.Count >= 40 || expanded >= 200) gaps.Add("graph-expansion-limit");
            if (State() != state) return new([], ["sql-changed-during-graph-query"], state.Generation);
            return new(paths, gaps, state.Generation);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new([], ["graph-timeout"], state.Generation); }
        catch (Neo4jException)
        { return new([], ["graph-unavailable"], state.Generation); }
        catch (TimeoutException)
        { return new([], ["sql-read-deadline"], state.Generation); }
    }
}
