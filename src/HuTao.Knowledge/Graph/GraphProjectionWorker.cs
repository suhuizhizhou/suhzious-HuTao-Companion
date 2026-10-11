using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Graph;

public sealed record GraphAssertionSnapshot(string Namespace,string Id,long Revision,string SubjectId,string SubjectType,
    string PredicateId,string? ObjectId,string? ObjectType,int Polarity,string Status,long? ValidFrom,long? ValidTo,long RecordedAt);
public interface IGraphProjection
{
    Task ApplyAsync(GraphOutboxEvent item,GraphAssertionSnapshot snapshot,CancellationToken ct);
}

public sealed class GraphProjectionWorker(SqliteKnowledgeDatabase database,IGraphProjection projection)
{
    public GraphAssertionSnapshot LoadLatest(KnowledgeAccessContext access,GraphOutboxEvent item)
    {
        access.Demand(item.Namespace);
        using var c=database.Open();using var t=c.BeginTransaction(deferred:true);
        using(var ev=Command(c,t,"SELECT 1 FROM graph_outbox WHERE event_id=@event AND namespace_id=@n AND aggregate_id=@id AND aggregate_revision=@r AND generation=@g",("@event",item.EventId),("@n",item.Namespace),("@id",item.AggregateId),("@r",item.AggregateRevision),("@g",item.Generation)))
            if(ev.ExecuteScalar() is null)throw new InvalidDataException("Projection event does not match SQL.");
        using var cmd=Command(c,t,"""
            SELECT a.revision,a.subject_id,s.type,a.predicate_id,a.object_id,o.type,a.polarity,CASE WHEN a.validation_method='forgotten' THEN 'forgotten' ELSE a.status END,a.valid_from,a.valid_to,a.recorded_at
            FROM assertions a JOIN entities s ON s.namespace_id=a.namespace_id AND s.entity_id=a.subject_id
            LEFT JOIN entities o ON o.namespace_id=a.namespace_id AND o.entity_id=a.object_id
            WHERE a.namespace_id=@n AND a.assertion_id=@id ORDER BY a.revision DESC LIMIT 1
            """,("@n",item.Namespace),("@id",item.AggregateId));
        using var row=cmd.ExecuteReader();if(!row.Read())throw new InvalidDataException("Assertion missing during graph projection.");
        var snapshot=new GraphAssertionSnapshot(item.Namespace,item.AggregateId,row.GetInt64(0),row.GetString(1),row.GetString(2),row.GetString(3),
            row.IsDBNull(4)?null:row.GetString(4),row.IsDBNull(5)?null:row.GetString(5),row.GetInt32(6),row.GetString(7),
            row.IsDBNull(8)?null:row.GetInt64(8),row.IsDBNull(9)?null:row.GetInt64(9),row.GetInt64(10));
        if(snapshot.Revision<item.AggregateRevision)throw new InvalidDataException("Assertion revision behind outbox.");
        return snapshot;
    }

    public async Task<bool> PublishOneAsync(KnowledgeAccessContext access,string space,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();var outbox=new GraphOutboxRepository(database);
        var item=outbox.Claim(access,space,DateTimeOffset.UtcNow);if(item is null)return false;
        try
        {
            var snapshot=LoadLatest(access,item);
            await projection.ApplyAsync(item,snapshot,ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if(!outbox.Acknowledge(access,item,DateTimeOffset.UtcNow))throw new InvalidOperationException("Projection lease expired before acknowledgment.");
            return true;
        }
        catch(Exception ex)
        {
            outbox.Fail(access,item,DateTimeOffset.UtcNow,ex.GetType().Name);
            throw;
        }
    }
}
