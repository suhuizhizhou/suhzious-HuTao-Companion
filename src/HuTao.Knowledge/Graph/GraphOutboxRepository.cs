using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using Microsoft.Data.Sqlite;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Graph;

public sealed record GraphOutboxEvent(string EventId,string Namespace,long CommitSequence,string AggregateId,long AggregateRevision,
    string Kind,string Generation,int Attempts,string LeaseToken);

public sealed class GraphOutboxRepository(SqliteKnowledgeDatabase database)
{
    internal static void Enqueue(SqliteConnection c,SqliteTransaction t,string space,string assertionId,long revision,string kind="assertion-upsert")
    {
        if(kind is not ("assertion-upsert" or "assertion-retract"))throw new ArgumentException("Unknown graph event.");
        Execute(c,t,"UPDATE namespace_state SET commit_seq=commit_seq+1 WHERE namespace_id=@n",("@n",space));
        long sequence;string generation;
        using(var cmd=Command(c,t,"SELECT commit_seq,graph_generation FROM namespace_state WHERE namespace_id=@n",("@n",space)))
        using(var row=cmd.ExecuteReader())
        {if(!row.Read())throw new InvalidDataException("Namespace state missing.");sequence=row.GetInt64(0);generation=row.GetString(1);}
        var identity=Hash($"{space}\u001f{sequence}\u001f{assertionId}\u001f{revision}\u001f{kind}\u001f{generation}");
        Execute(c,t,"""
            INSERT INTO graph_outbox(event_id,namespace_id,commit_seq,aggregate_id,aggregate_revision,kind,generation,payload_hash)
            VALUES(@event,@n,@seq,@id,@r,@kind,@generation,@hash)
            """,("@event",identity),("@n",space),("@seq",sequence),("@id",assertionId),("@r",revision),("@kind",kind),("@generation",generation),("@hash",identity));
    }

    public GraphOutboxEvent? Claim(KnowledgeAccessContext access,string space,DateTimeOffset now,TimeSpan? lease=null)
    {
        access.Demand(space);var duration=lease??TimeSpan.FromSeconds(30);
        if(duration<=TimeSpan.Zero || duration>TimeSpan.FromMinutes(5))throw new ArgumentOutOfRangeException(nameof(lease));
        return database.Write((c,t)=>{
            var at=now.ToUnixTimeMilliseconds();var token=Guid.NewGuid().ToString("N");
            GraphOutboxEvent? result=null;
            using(var cmd=Command(c,t,"""
                SELECT event_id,commit_seq,aggregate_id,aggregate_revision,kind,generation,attempts FROM graph_outbox e
                WHERE namespace_id=@n AND delivered_at IS NULL AND diagnostic=0 AND next_attempt_at<=@at
                AND (lease_until IS NULL OR lease_until<=@at)
                AND NOT EXISTS(SELECT 1 FROM graph_outbox earlier WHERE earlier.namespace_id=e.namespace_id AND earlier.delivered_at IS NULL AND earlier.commit_seq<e.commit_seq)
                ORDER BY commit_seq,event_id LIMIT 1
                """,("@n",space),("@at",at)))
            using(var row=cmd.ExecuteReader())
                if(row.Read())result=new(row.GetString(0),space,row.GetInt64(1),row.GetString(2),row.GetInt64(3),row.GetString(4),row.GetString(5),row.GetInt32(6)+1,token);
            if(result is null)return null;
            Execute(c,t,"UPDATE graph_outbox SET lease_token=@token,lease_until=@until,attempts=attempts+1 WHERE event_id=@id",("@token",token),("@until",at+(long)duration.TotalMilliseconds),("@id",result.EventId));
            return result;
        });
    }

    public bool Acknowledge(KnowledgeAccessContext access,GraphOutboxEvent item,DateTimeOffset now)
    {
        access.Demand(item.Namespace);
        return database.Write((c,t)=>{
            long sequence;string generation;
            using(var command=Command(c,t,"""
                UPDATE graph_outbox SET delivered_at=@at,lease_token=NULL,lease_until=NULL,last_error=NULL
                WHERE event_id=@id AND namespace_id=@n AND lease_token=@token AND lease_until>@at AND delivered_at IS NULL
                RETURNING commit_seq,generation
                """,("@at",now.ToUnixTimeMilliseconds()),("@id",item.EventId),("@n",item.Namespace),("@token",item.LeaseToken)))
            using(var row=command.ExecuteReader())
            {if(!row.Read())return false;sequence=row.GetInt64(0);generation=row.GetString(1);}
            Execute(c,t,"UPDATE namespace_state SET graph_checkpoint=MAX(graph_checkpoint,@seq) WHERE namespace_id=@n AND graph_generation=@generation",
                ("@seq",sequence),("@n",item.Namespace),("@generation",generation));
            return true;
        });
    }

    public bool Fail(KnowledgeAccessContext access,GraphOutboxEvent item,DateTimeOffset now,string errorCode,int maxAttempts=5)
    {
        access.Demand(item.Namespace);
        if(string.IsNullOrWhiteSpace(errorCode) || errorCode.Length>120 || maxAttempts<1)throw new ArgumentException("Invalid retry diagnostic.");
        var delay=Math.Min(60000,1000*(1L<<Math.Min(item.Attempts,6)));
        return database.Write((c,t)=>Execute(c,t,"""
            UPDATE graph_outbox SET lease_token=NULL,lease_until=NULL,next_attempt_at=@next,diagnostic=CASE WHEN attempts>=@max THEN 1 ELSE 0 END,last_error=@error
            WHERE event_id=@id AND namespace_id=@n AND lease_token=@token AND lease_until>@at AND delivered_at IS NULL
            """,("@next",now.ToUnixTimeMilliseconds()+delay),("@max",maxAttempts),("@error",errorCode),("@id",item.EventId),("@n",item.Namespace),("@token",item.LeaseToken),("@at",now.ToUnixTimeMilliseconds()))==1);
    }

    public bool RetryDiagnostic(KnowledgeAccessContext access,string space,string eventId)
    {
        access.Demand(space);
        return database.Write((c,t)=>Execute(c,t,"UPDATE graph_outbox SET diagnostic=0,attempts=0,next_attempt_at=0,last_error=NULL WHERE namespace_id=@n AND event_id=@id AND diagnostic=1 AND delivered_at IS NULL",("@n",space),("@id",eventId))==1);
    }
}
