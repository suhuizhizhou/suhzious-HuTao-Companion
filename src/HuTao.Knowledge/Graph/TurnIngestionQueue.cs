using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Graph;

public sealed record TurnIngestionLease(EvidenceRef Reference,string Token);

/// <summary>Only identifiers and bounded diagnostics persist here; original text stays in evidence_units.</summary>
public sealed class TurnIngestionQueue(SqliteKnowledgeDatabase database)
{
    private const string SourceCurrent="""
        EXISTS(SELECT 1 FROM evidence_units e JOIN memory_records m
          ON m.namespace_id=e.namespace_id AND m.memory_id=e.evidence_id AND m.evidence_revision=e.revision
          WHERE e.namespace_id=turn_ingestion.namespace_id AND e.evidence_id=turn_ingestion.evidence_id AND e.revision=turn_ingestion.evidence_revision
            AND e.deleted_at IS NULL AND e.speaker='user' AND m.kind='Turn' AND m.archived=0
            AND NOT EXISTS(SELECT 1 FROM evidence_units newer WHERE newer.namespace_id=e.namespace_id AND newer.evidence_id=e.evidence_id AND newer.revision>e.revision))
        """;
    private static void Sweep(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction t,string space,long now)
    {
        Execute(c,t,"UPDATE turn_ingestion SET status='cancelled',lease_token=NULL,lease_until=NULL,diagnostic='source-unavailable' WHERE namespace_id=@n AND status IN ('pending','processing') AND NOT "+SourceCurrent,("@n",space));
        Execute(c,t,"UPDATE turn_ingestion SET status='failed',lease_token=NULL,lease_until=NULL,diagnostic='attempt-limit' WHERE namespace_id=@n AND status='processing' AND lease_until<=@now AND attempts>=3",("@n",space),("@now",now));
    }
    public IReadOnlyList<string> ReadyIds(KnowledgeAccessContext access,CancellationToken ct=default)
    {
        var space=access.MemoryNamespace;access.Demand(space);
        return database.Write((c,t)=>
        {
            var now=Now();Sweep(c,t,space,now);
            using var command=Command(c,t,"SELECT evidence_id FROM turn_ingestion WHERE namespace_id=@n AND (status='pending' OR (status='processing' AND lease_until<=@now)) ORDER BY created_at,evidence_id LIMIT 8",("@n",space),("@now",now));
            using var rows=command.ExecuteReader();var ids=new List<string>();while(rows.Read())ids.Add(rows.GetString(0));return (IReadOnlyList<string>)ids;
        },ct);
    }
    public TurnIngestionLease? Claim(KnowledgeAccessContext access,CancellationToken ct=default,EvidenceRef? only=null,bool includeDeferred=false)
    {
        var space=access.MemoryNamespace;access.Demand(space);
        if(only is not null && only.Namespace!=space)throw new UnauthorizedAccessException("Queue source outside session.");
        if(includeDeferred && only is null)throw new ArgumentException("Deferred extraction requires an explicit source selection.");
        return database.Write((c,t)=>
        {
            var now=Now();
            Sweep(c,t,space,now);
            string id;long revision;
            using(var command=Command(c,t,"SELECT evidence_id,evidence_revision FROM turn_ingestion WHERE namespace_id=@n AND (@id IS NULL OR (evidence_id=@id AND evidence_revision=@r)) AND attempts<3 AND (status='pending' OR (@deferred=1 AND status='deferred') OR (status='processing' AND lease_until<=@now)) ORDER BY created_at,evidence_id LIMIT 1",("@n",space),("@now",now),("@id",only?.Id),("@r",only?.Revision),("@deferred",includeDeferred?1:0)))
            using(var row=command.ExecuteReader())
            {if(!row.Read())return null;id=row.GetString(0);revision=row.GetInt64(1);}
            var token=Guid.NewGuid().ToString("N");
            Execute(c,t,"UPDATE turn_ingestion SET status='processing',attempts=attempts+1,lease_token=@token,lease_until=@until WHERE namespace_id=@n AND evidence_id=@id AND evidence_revision=@r",
                ("@token",token),("@until",now+30000),("@n",space),("@id",id),("@r",revision));
            return new TurnIngestionLease(new(space,id,revision),token);
        },ct);
    }
    public bool Complete(KnowledgeAccessContext access,TurnIngestionLease lease,string status)
    {
        access.Demand(lease.Reference.Namespace);
        if(lease.Reference.Namespace!=access.MemoryNamespace)throw new UnauthorizedAccessException("Only current memory queue is writable.");
        if(status is not ("completed" or "deferred" or "failed"))throw new ArgumentException("Invalid terminal ingestion state.");
        return database.Write((c,t)=>
        {
            var now=Now();Sweep(c,t,lease.Reference.Namespace,now);
            return Execute(c,t,"UPDATE turn_ingestion SET status=@status,lease_token=NULL,lease_until=NULL WHERE namespace_id=@n AND evidence_id=@id AND evidence_revision=@r AND status='processing' AND lease_token=@token AND lease_until>@now AND "+SourceCurrent,
                ("@status",status),("@n",lease.Reference.Namespace),("@id",lease.Reference.Id),("@r",lease.Reference.Revision),("@token",lease.Token),("@now",now))==1;
        });
    }
}
