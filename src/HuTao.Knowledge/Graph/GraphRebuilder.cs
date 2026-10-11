using System.Text.Json;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using Microsoft.Data.Sqlite;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Graph;

public sealed record GraphRebuildResult(string Generation, bool Activated, int Assertions, string Reason);
public interface IGraphGenerationProjection : IGraphProjection
{
    Task<bool> ValidateAsync(string space, string generation, IReadOnlyList<GraphAssertionSnapshot> expected, CancellationToken ct = default);
}

/// <summary>Durable SQL snapshots, idempotent graph writes, bounded catch-up and fenced activation.</summary>
public sealed class GraphRebuilder(SqliteKnowledgeDatabase database, IGraphGenerationProjection projection)
{
    private sealed record Job(string SourceGeneration,long Sequence,long Epoch,string Status);
    private static Job ReadJob(SqliteConnection c,SqliteTransaction? t,string space,string generation)
    {
        using var command=Command(c,t,"SELECT source_generation,snapshot_seq,privacy_epoch,status FROM graph_rebuild_jobs WHERE namespace_id=@n AND generation=@g",("@n",space),("@g",generation));
        using var row=command.ExecuteReader();
        if(!row.Read())throw new InvalidDataException("Rebuild job missing.");
        return new(row.GetString(0),row.GetInt64(1),row.GetInt64(2),row.GetString(3));
    }

    public string Prepare(KnowledgeAccessContext access,string space,CancellationToken ct=default)
    {
        access.Demand(space);ct.ThrowIfCancellationRequested();
        var generation="rebuild-"+Guid.NewGuid().ToString("N");
        return database.Write((c,t)=>
        {
            if(Execute(c,t,"""
                INSERT INTO graph_rebuild_jobs(namespace_id,generation,source_generation,snapshot_seq,privacy_epoch,status,created_at,updated_at)
                SELECT @n,@g,graph_generation,commit_seq,privacy_epoch,'ready',@now,@now
                FROM namespace_state JOIN namespaces USING(namespace_id) WHERE namespace_id=@n
                """,("@n",space),("@g",generation),("@now",Now()))!=1)throw new InvalidDataException("Namespace state missing.");
            foreach(var snapshot in GraphSnapshotReader.Read(c,t,space))
            {ct.ThrowIfCancellationRequested();SaveItem(c,t,generation,snapshot);}
            return generation;
        },ct);
    }

    private static void SaveItem(SqliteConnection c,SqliteTransaction t,string generation,GraphAssertionSnapshot snapshot)=>Execute(c,t,"""
        INSERT INTO graph_rebuild_items(namespace_id,generation,assertion_id,revision,snapshot_json)
        VALUES(@n,@g,@id,@r,@json)
        ON CONFLICT(namespace_id,generation,assertion_id) DO UPDATE SET revision=excluded.revision,snapshot_json=excluded.snapshot_json
        """,("@n",snapshot.Namespace),("@g",generation),("@id",snapshot.Id),("@r",snapshot.Revision),("@json",JsonSerializer.Serialize(snapshot)));

    public Task<GraphRebuildResult> RebuildAsync(KnowledgeAccessContext access,string space,CancellationToken ct=default)=>
        ResumeAsync(access,space,Prepare(access,space,ct),ct);

    public async Task<GraphRebuildResult> ResumeAsync(KnowledgeAccessContext access,string space,string generation,CancellationToken ct=default)
    {
        access.Demand(space);ct.ThrowIfCancellationRequested();
        var token=Guid.NewGuid().ToString("N");
        var claimed=database.Write((c,t)=>Execute(c,t,"""
            UPDATE graph_rebuild_jobs SET status='running',lease_token=@token,lease_until=@until,last_error=NULL,updated_at=@now
            WHERE namespace_id=@n AND generation=@g AND status IN ('ready','running','interrupted')
                AND (lease_until IS NULL OR lease_until<=@now)
            """,("@n",space),("@g",generation),("@token",token),("@until",Now()+60000),("@now",Now()))==1,ct);
        if(!claimed)
        {
            using var c=database.Open();var job=ReadJob(c,null,space,generation);
            if(job.Status=="activated")
            {
                using var command=Command(c,null,"SELECT graph_generation FROM namespace_state WHERE namespace_id=@n",("@n",space));
                var active=command.ExecuteScalar() as string==generation;
                return new(generation,active,Count(space,generation),active?"already-active":"job-superseded");
            }
            return new(generation,false,Count(space,generation),"job-"+job.Status);
        }
        try
        {
            // Bound continuous writers. An interrupted job keeps its prepared items and can resume later.
            for(var round=0;round<3;round++)
            {
                var valid=CaptureChanges(space,generation,token,ct);
                if(!valid)return Finish(space,generation,token,"invalidated","source-generation-or-privacy-changed");
                while(true)
                {
                    ct.ThrowIfCancellationRequested();Renew(space,generation,token);
                    var batch=ReadItems(space,generation,true,100);
                    if(batch.Count==0)break;
                    foreach(var snapshot in batch)
                    {
                        Renew(space,generation,token);ct.ThrowIfCancellationRequested();
                        long sequence;
                        using(var c=database.Open())sequence=ReadJob(c,null,space,generation).Sequence;
                        var item=new GraphOutboxEvent(Hash($"{generation}:{snapshot.Id}:{snapshot.Revision}"),space,sequence,
                            snapshot.Id,snapshot.Revision,"assertion-upsert",generation,0,"");
                        await projection.ApplyAsync(item,snapshot,ct).ConfigureAwait(false);
                        database.Write((c,t)=>
                        {
                            DemandLease(c,t,space,generation,token);
                            return Execute(c,t,"UPDATE graph_rebuild_items SET projected_revision=@r WHERE namespace_id=@n AND generation=@g AND assertion_id=@id AND revision=@r",
                                ("@r",snapshot.Revision),("@n",space),("@g",generation),("@id",snapshot.Id));
                        },ct);
                    }
                }
                Renew(space,generation,token);
                var expected=ReadItems(space,generation,false,int.MaxValue);
                if(!await projection.ValidateAsync(space,generation,expected,ct).ConfigureAwait(false))
                    return Finish(space,generation,token,"failed","graph-validation-failed");
                var activated=database.Write((c,t)=>
                {
                    DemandLease(c,t,space,generation,token);var job=ReadJob(c,t,space,generation);
                    if(Execute(c,t,"""
                        UPDATE namespace_state SET graph_generation=@g,graph_checkpoint=@seq
                        WHERE namespace_id=@n AND graph_generation=@old AND commit_seq=@seq
                        AND EXISTS(SELECT 1 FROM namespaces WHERE namespace_id=@n AND privacy_epoch=@epoch)
                        """,("@n",space),("@g",generation),("@old",job.SourceGeneration),("@seq",job.Sequence),("@epoch",job.Epoch))!=1)return false;
                    // This snapshot subsumes earlier events, including diagnostic or leased events of the old generation.
                    Execute(c,t,"""
                        UPDATE graph_outbox SET delivered_at=@now,lease_token=NULL,lease_until=NULL,diagnostic=0,last_error=NULL
                        WHERE namespace_id=@n AND commit_seq<=@seq AND delivered_at IS NULL
                        """,("@now",Now()),("@n",space),("@seq",job.Sequence));
                    Execute(c,t,"UPDATE graph_rebuild_jobs SET status='activated',lease_token=NULL,lease_until=NULL,updated_at=@now WHERE namespace_id=@n AND generation=@g",
                        ("@now",Now()),("@n",space),("@g",generation));
                    return true;
                },ct);
                if(activated)return new(generation,true,expected.Count,"activated");
            }
            return Finish(space,generation,token,"interrupted","catchup-budget-exhausted");
        }
        catch(Exception ex)
        {
            // Never overwrite a newer worker's ownership after an expired lease.
            database.Write((c,t)=>Execute(c,t,"""
                UPDATE graph_rebuild_jobs SET status='interrupted',lease_token=NULL,lease_until=NULL,last_error=@error,updated_at=@now
                WHERE namespace_id=@n AND generation=@g AND lease_token=@token
                """,("@n",space),("@g",generation),("@token",token),("@error",ex.GetType().Name),("@now",Now())));
            throw;
        }
    }

    private bool CaptureChanges(string space,string generation,string token,CancellationToken ct)=>database.Write((c,t)=>
    {
        DemandLease(c,t,space,generation,token);var job=ReadJob(c,t,space,generation);long sequence;
        using(var command=Command(c,t,"""
            SELECT commit_seq FROM namespace_state JOIN namespaces USING(namespace_id)
            WHERE namespace_id=@n AND graph_generation=@old AND privacy_epoch=@epoch
            """,("@n",space),("@old",job.SourceGeneration),("@epoch",job.Epoch)))
        {var value=command.ExecuteScalar();if(value is null)return false;sequence=Convert.ToInt64(value);}
        if(sequence==job.Sequence)return true;
        foreach(var snapshot in GraphSnapshotReader.Read(c,t,space,job.Sequence))
        {ct.ThrowIfCancellationRequested();SaveItem(c,t,generation,snapshot);}
        Execute(c,t,"UPDATE graph_rebuild_jobs SET snapshot_seq=@seq WHERE namespace_id=@n AND generation=@g",("@seq",sequence),("@n",space),("@g",generation));
        return true;
    },ct);

    private List<GraphAssertionSnapshot> ReadItems(string space,string generation,bool pending,int limit)
    {
        using var c=database.Open();using var command=Command(c,null,"""
            SELECT snapshot_json FROM graph_rebuild_items WHERE namespace_id=@n AND generation=@g
            AND (@pending=0 OR projected_revision<revision) ORDER BY assertion_id LIMIT @limit
            """,("@n",space),("@g",generation),("@pending",pending?1:0),("@limit",limit));
        using var rows=command.ExecuteReader();var result=new List<GraphAssertionSnapshot>();
        while(rows.Read())result.Add(JsonSerializer.Deserialize<GraphAssertionSnapshot>(rows.GetString(0))!);
        return result;
    }
    private int Count(string space,string generation)
    {
        using var c=database.Open();using var command=Command(c,null,"SELECT COUNT(*) FROM graph_rebuild_items WHERE namespace_id=@n AND generation=@g",("@n",space),("@g",generation));
        return Convert.ToInt32(command.ExecuteScalar());
    }
    private static void DemandLease(SqliteConnection c,SqliteTransaction t,string space,string generation,string token)
    {
        using var command=Command(c,t,"SELECT 1 FROM graph_rebuild_jobs WHERE namespace_id=@n AND generation=@g AND lease_token=@token AND lease_until>@now AND status='running'",
            ("@n",space),("@g",generation),("@token",token),("@now",Now()));
        if(command.ExecuteScalar() is null)throw new InvalidOperationException("Rebuild lease no longer owned.");
    }
    private void Renew(string space,string generation,string token)=>database.Write((c,t)=>
    {
        DemandLease(c,t,space,generation,token);
        return Execute(c,t,"UPDATE graph_rebuild_jobs SET lease_until=@until,updated_at=@now WHERE namespace_id=@n AND generation=@g",("@until",Now()+60000),("@now",Now()),("@n",space),("@g",generation));
    });
    private GraphRebuildResult Finish(string space,string generation,string token,string status,string reason)
    {
        database.Write((c,t)=>
        {
            DemandLease(c,t,space,generation,token);
            return Execute(c,t,"UPDATE graph_rebuild_jobs SET status=@status,lease_token=NULL,lease_until=NULL,last_error=@reason,updated_at=@now WHERE namespace_id=@n AND generation=@g",
                ("@status",status),("@reason",reason),("@now",Now()),("@n",space),("@g",generation));
        });
        return new(generation,false,Count(space,generation),reason);
    }
}
