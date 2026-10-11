using System.Text.Json;
using Microsoft.Data.Sqlite;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Storage;

public sealed record RecoveryNamespaceSnapshot(string Namespace,long PrivacyEpoch,long CommitSequence,int Tombstones);
public sealed record KnowledgeRecoveryReport(string State,int EvidenceDeleted,int AssertionsRetracted,IReadOnlyList<RecoveryNamespaceSnapshot> SourceSnapshot,string LineageId);

/// <summary>Offline recovery preparation. A snapshot of deletions is not authorization to activate an older database.</summary>
public static class KnowledgeRecovery
{
    public static void DemandActivationReady(SqliteKnowledgeDatabase database)
    {
        using var c=database.Open();using var command=Command(c,null,"SELECT COUNT(*) FROM recovery_gate");
        if(Convert.ToInt64(command.ExecuteScalar())!=0)
            throw new InvalidOperationException("Recovered database requires final synchronization before activation.");
    }

    internal static KnowledgeRecoveryReport ReconcileDeletions(SqliteKnowledgeDatabase target,string currentPath,CancellationToken ct=default,bool stagingBranches=false)
    {
        if(string.Equals(Path.GetFullPath(currentPath),target.FilePath,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Recovery source and target must differ.");
        using var current=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.GetFullPath(currentPath),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        current.Open();using var snapshot=current.BeginTransaction(deferred:true);
        var lineage=ReadLineage(current,snapshot);
        using(var gate=Command(current,snapshot,"SELECT COUNT(*) FROM recovery_gate"))
            if(!stagingBranches && Convert.ToInt64(gate.ExecuteScalar())!=0)throw new InvalidDataException("A pending recovery cannot serve as the current authority.");
        var spaces=new List<RecoveryNamespaceSnapshot>();
        using(var command=Command(current,snapshot,"SELECT n.namespace_id,n.privacy_epoch,s.commit_seq FROM namespaces n JOIN namespace_state s USING(namespace_id) WHERE n.domain='memory' ORDER BY n.namespace_id"))
        using(var rows=command.ExecuteReader())while(rows.Read())spaces.Add(new(rows.GetString(0),rows.GetInt64(1),rows.GetInt64(2),0));
        var tombstones=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        for(var i=0;i<spaces.Count;i++)
        {
            ct.ThrowIfCancellationRequested();var space=spaces[i];var ids=new List<string>();
            using var command=Command(current,snapshot,"SELECT evidence_id FROM deletion_tombstones WHERE namespace_id=@n ORDER BY evidence_id",("@n",space.Namespace));
            using var rows=command.ExecuteReader();while(rows.Read()){ct.ThrowIfCancellationRequested();ids.Add(rows.GetString(0));}
            tombstones.Add(space.Namespace,ids);spaces[i]=space with {Tombstones=ids.Count};
        }
        return target.Write((c,t)=>
        {
            if(ReadLineage(c,t)!=lineage)throw new InvalidDataException("Backup and current database have different lineages.");
            // A different or older authority cannot silently bless the private spaces in this backup.
            using(var command=Command(c,t,"SELECT n.namespace_id,n.privacy_epoch,s.commit_seq FROM namespaces n JOIN namespace_state s USING(namespace_id) WHERE n.domain='memory'"))
            using(var rows=command.ExecuteReader())while(rows.Read())
            {
                var source=spaces.SingleOrDefault(s=>s.Namespace==rows.GetString(0));
                if(!stagingBranches && (source is null || source.PrivacyEpoch<rows.GetInt64(1) || source.CommitSequence<rows.GetInt64(2)))
                    throw new InvalidDataException("Current source is missing a private namespace or predates the backup.");
            }
            var deleted=0;var retracted=0;
            foreach(var space in spaces)
            {
                ct.ThrowIfCancellationRequested();
                Execute(c,t,"INSERT OR IGNORE INTO namespaces(namespace_id,domain) VALUES(@n,'memory')",("@n",space.Namespace));
                Execute(c,t,"INSERT OR IGNORE INTO namespace_state(namespace_id) VALUES(@n)",("@n",space.Namespace));
                foreach(var batch in tombstones[space.Namespace].Chunk(1000))
                {
                    var result=KnowledgeDeletionService.ForgetInTransaction(c,t,space.Namespace,batch,ct);
                    deleted+=result.EvidenceDeleted;retracted+=result.AssertionsRetracted;
                }
                Execute(c,t,"UPDATE namespaces SET privacy_epoch=MAX(privacy_epoch,@epoch) WHERE namespace_id=@n",("@n",space.Namespace),("@epoch",space.PrivacyEpoch));
            }
            Execute(c,t,"INSERT OR REPLACE INTO recovery_gate VALUES(1,'pending-final-sync',@snapshot,@now)",("@snapshot",JsonSerializer.Serialize(spaces)),("@now",Now()));
            return new KnowledgeRecoveryReport("pending-final-sync",deleted,retracted,spaces,lineage);
        },ct);
    }

    public static string ReadLineage(SqliteKnowledgeDatabase database)
    {using var c=database.Open();return ReadLineage(c,null);}

    private static string ReadLineage(SqliteConnection c,SqliteTransaction? t)
    {
        using var exists=Command(c,t,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='database_identity'");
        if(Convert.ToInt64(exists.ExecuteScalar())!=1)throw new InvalidDataException("Database predates lineage tracking; upgrade authority and create a new backup first.");
        using var command=Command(c,t,"SELECT lineage_id FROM database_identity WHERE singleton=1");
        if(command.ExecuteScalar() is not string id || id.Length!=32 || id.Any(ch=>!char.IsAsciiHexDigitLower(ch)))
            throw new InvalidDataException("Database lineage is missing or invalid.");
        return id;
    }
}
