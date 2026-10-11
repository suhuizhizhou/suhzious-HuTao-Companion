using Neo4j.Driver;

namespace HuTao.Knowledge.Graph;

/// <summary>Only IDs, types and assertion metadata are projected; no personal text or literal values.</summary>
public sealed class Neo4jGraphProjection(IDriver driver) : IGraphGenerationProjection
{
    public async Task<bool> ValidateAsync(string space, string generation, IReadOnlyList<GraphAssertionSnapshot> expected, CancellationToken ct = default)
    {
        if (expected.Any(x => x.Namespace != space)) throw new ArgumentException("Mixed projection namespaces.");
        expected=expected.Where(x=>x.Status!="forgotten").ToArray();
        await using var session = driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var count = await tx.RunAsync("MATCH (a:HuTaoAssertion {namespace_id:$ns,generation:$g}) RETURN count(a) AS count", new { ns = space, g = generation });
            if (!await count.FetchAsync() || count.Current["count"].As<long>() != expected.Count) return false;
            foreach (var batch in expected.Chunk(100))
            {
                ct.ThrowIfCancellationRequested();
                var cursor = await tx.RunAsync("""
                    MATCH (a:HuTaoAssertion {namespace_id:$ns,generation:$g}) WHERE a.id IN $ids
                    OPTIONAL MATCH (a)-[r:SUBJECT|OBJECT]->(e)
                    RETURN a,collect({kind:type(r),ns:e.namespace_id,g:e.generation,id:e.id,type:e.type}) AS links
                    """, new { ns = space, g = generation, ids = batch.Select(x => x.Id).ToArray() });
                var remaining = batch.ToDictionary(x => x.Id);
                while (await cursor.FetchAsync())
                {
                    var node = cursor.Current["a"].As<INode>();
                    if (!remaining.Remove(node["id"].As<string>(), out var snapshot)) return false;
                    bool Same(string key, object? value) => value is null ? !node.Properties.ContainsKey(key) : node.Properties.TryGetValue(key, out var actual) && Equals(actual, value);
                    if (!Same("revision", snapshot.Revision) || !Same("predicate_id", snapshot.PredicateId) ||
                        !Same("polarity", (long)snapshot.Polarity) || !Same("status", snapshot.Status) ||
                        !Same("valid_from", snapshot.ValidFrom) || !Same("valid_to", snapshot.ValidTo) || !Same("recorded_at", snapshot.RecordedAt)) return false;
                    var links = cursor.Current["links"].As<List<Dictionary<string, object>>>();
                    if (links.Count != (snapshot.ObjectId is null ? 1 : 2)) return false;
                    bool Link(string kind, string id, string? type) => links.Count(l =>
                        l.GetValueOrDefault("kind") as string == kind && l.GetValueOrDefault("ns") as string == space &&
                        l.GetValueOrDefault("g") as string == generation && l.GetValueOrDefault("id") as string == id &&
                        l.GetValueOrDefault("type") as string == type) == 1;
                    if (!Link("SUBJECT", snapshot.SubjectId, snapshot.SubjectType) ||
                        snapshot.ObjectId is not null && !Link("OBJECT", snapshot.ObjectId, snapshot.ObjectType)) return false;
                }
                if (remaining.Count != 0) return false;
            }
            return true;
        }, config => config.WithTimeout(TimeSpan.FromSeconds(10))).WaitAsync(ct);
    }

    public async Task InitializeAsync(CancellationToken ct=default)
    {
        await using var session=driver.AsyncSession();
        foreach(var (label,name,keys) in new[]{("HuTaoEntity","hutao_entity_identity","n.namespace_id,n.id,n.generation"),
            ("HuTaoAssertion","hutao_assertion_identity","n.namespace_id,n.id,n.generation"),
            ("HuTaoProjection","hutao_projection_identity","n.namespace_id,n.generation"),
            ("HuTaoApplied","hutao_applied_identity","n.namespace_id,n.event_id,n.generation"),
            ("HuTaoForgotten","hutao_forgotten_identity","n.namespace_id,n.id"),
            ("HuTaoPrivacyFence","hutao_privacy_fence","n.namespace_id")})
        {
            ct.ThrowIfCancellationRequested();
            var cursor=await session.RunAsync($"CREATE CONSTRAINT {name} IF NOT EXISTS FOR (n:{label}) REQUIRE ({keys}) IS UNIQUE").WaitAsync(ct);
            await cursor.ConsumeAsync().WaitAsync(ct);
        }
    }

    public async Task ApplyAsync(GraphOutboxEvent item,GraphAssertionSnapshot snapshot,CancellationToken ct)
    {
        if(item.Namespace!=snapshot.Namespace || item.AggregateId!=snapshot.Id || snapshot.Revision<item.AggregateRevision)
            throw new InvalidDataException("Graph snapshot does not match event.");
        var parameters=new Dictionary<string,object?> {
            ["ns"]=item.Namespace,["generation"]=item.Generation,["id"]=snapshot.Id,["revision"]=snapshot.Revision,
            ["subject"]=snapshot.SubjectId,["subjectType"]=snapshot.SubjectType,["predicate"]=snapshot.PredicateId,
            ["object"]=snapshot.ObjectId,["objectType"]=snapshot.ObjectType,["polarity"]=snapshot.Polarity,["status"]=snapshot.Status,
            ["validFrom"]=snapshot.ValidFrom,["validTo"]=snapshot.ValidTo,["recordedAt"]=snapshot.RecordedAt,
            ["sequence"]=item.CommitSequence,["event"]=item.EventId
        };
        await using var session=driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx=>{
            // Serialize forgetting and late writes across every generation, including stale rebuild snapshots.
            var fence=await tx.RunAsync("MERGE (f:HuTaoPrivacyFence {namespace_id:$ns}) SET f.sequence=coalesce(f.sequence,0)+1",parameters);
            await fence.ConsumeAsync();
            if(snapshot.Status=="forgotten")
            {
                var forget=await tx.RunAsync("""
                    MERGE (f:HuTaoForgotten {namespace_id:$ns,id:$id})
                    SET f.revision=CASE WHEN coalesce(f.revision,0)<$revision THEN $revision ELSE f.revision END
                    WITH f OPTIONAL MATCH (a:HuTaoAssertion {namespace_id:$ns,id:$id}) DETACH DELETE a
                    """,parameters);
                await forget.ConsumeAsync();
                var orphans=await tx.RunAsync("MATCH (e:HuTaoEntity {namespace_id:$ns}) WHERE NOT (e)--() DELETE e",parameters);
                await orphans.ConsumeAsync();
            }
            else
            {
            var mutation=await tx.RunAsync("""
                OPTIONAL MATCH (forgotten:HuTaoForgotten {namespace_id:$ns,id:$id})
                WITH forgotten WHERE forgotten IS NULL
                MERGE (a:HuTaoAssertion {namespace_id:$ns,id:$id,generation:$generation})
                ON CREATE SET a.revision=0
                WITH a WHERE a.revision <= $revision
                SET a.revision=$revision,a.predicate_id=$predicate,a.polarity=$polarity,a.status=$status,
                    a.valid_from=$validFrom,a.valid_to=$validTo,a.recorded_at=$recordedAt
                WITH a OPTIONAL MATCH (a)-[old:SUBJECT|OBJECT]->() DELETE old
                WITH DISTINCT a
                MERGE (s:HuTaoEntity {namespace_id:$ns,id:$subject,generation:$generation}) SET s.type=$subjectType
                MERGE (a)-[:SUBJECT]->(s)
                FOREACH (_ IN CASE WHEN $object IS NULL THEN [] ELSE [1] END |
                    MERGE (o:HuTaoEntity {namespace_id:$ns,id:$object,generation:$generation}) SET o.type=$objectType
                    MERGE (a)-[:OBJECT]->(o))
                """,parameters);
            await mutation.ConsumeAsync();
            }
            var checkpoint=await tx.RunAsync("""
                MERGE (p:HuTaoProjection {namespace_id:$ns,generation:$generation})
                ON CREATE SET p.applied_sequence=0
                SET p.applied_sequence=CASE WHEN p.applied_sequence<$sequence THEN $sequence ELSE p.applied_sequence END
                MERGE (:HuTaoApplied {namespace_id:$ns,generation:$generation,event_id:$event})
                """,parameters);
            await checkpoint.ConsumeAsync();
        },config=>config.WithTimeout(TimeSpan.FromSeconds(2))).WaitAsync(ct);
    }
}
