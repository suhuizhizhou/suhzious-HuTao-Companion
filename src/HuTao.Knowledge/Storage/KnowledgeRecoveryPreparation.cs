using System.Text.Json;

namespace HuTao.Knowledge.Storage;

public sealed record RecoveryPreparationReport(string State,KnowledgeBackupManifest Candidate,KnowledgeBackupManifest Quarantine,
    KnowledgeRecoveryReport DeletionsToBaseline,KnowledgeRecoveryReport DeletionsToCurrent,KnowledgeRecoveryDiffReport Differences,
    KnowledgeBackupManifest CurrentAtRead,KnowledgeBackupManifest BackupOriginal);

/// <summary>Materializes current writes and preserves divergent backup data separately, with union-of-deletions and no activation.</summary>
public static class KnowledgeRecoveryPreparation
{
    public static RecoveryPreparationReport Prepare(string backupPackage,string currentDatabase,string newDirectory,CancellationToken ct=default)
    {
        var destination=Path.GetFullPath(newDirectory);
        if(File.Exists(destination)||Directory.Exists(destination))throw new IOException("Recovery output directory must be new.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging=destination+".preparing-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(staging);
        try
        {
            ct.ThrowIfCancellationRequested();
            var currentPath=Path.Combine(staging,"current-working.db");
            KnowledgeBackupPackage.SnapshotFile(currentDatabase,currentPath);
            var currentAtRead=KnowledgeBackupPackage.Inspect(currentPath);
            var current=new SqliteKnowledgeDatabase(currentPath);
            KnowledgeRecovery.DemandActivationReady(current);
            var baselinePath=Path.Combine(staging,"baseline-working.db");
            var original=KnowledgeBackupPackage.Restore(backupPackage,baselinePath);
            if(original.SchemaVersion<9)throw new InvalidDataException("Backup lacks lineage identity.");
            var baseline=new SqliteKnowledgeDatabase(baselinePath);
            // Only our owned, disconnected staging files use branch mode. Neither becomes a source of activation authority.
            var toBaseline=KnowledgeRecovery.ReconcileDeletions(baseline,currentPath,ct,stagingBranches:true);
            var toCurrent=KnowledgeRecovery.ReconcileDeletions(current,baselinePath,ct,stagingBranches:true);
            var differences=KnowledgeRecoveryDiff.Compare(baselinePath,currentPath,ct);
            var candidatePath=Path.Combine(staging,"candidate.db");var quarantinePath=Path.Combine(staging,"quarantine.db");
            current.Backup(candidatePath);baseline.Backup(quarantinePath);
            var report=new RecoveryPreparationReport("pending-conflict-review-and-final-sync",KnowledgeBackupPackage.Inspect(candidatePath),
                KnowledgeBackupPackage.Inspect(quarantinePath),toBaseline,toCurrent,differences,currentAtRead,original);
            // Do not publish working copies (including WALs) or raw pre-reconciliation private data.
            foreach(var owned in new[]{currentPath,baselinePath})
                foreach(var file in new[]{owned,owned+"-wal",owned+"-shm"})if(File.Exists(file))File.Delete(file);
            using(var stream=new FileStream(Path.Combine(staging,"report.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,report,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}
            ct.ThrowIfCancellationRequested();Directory.Move(staging,destination);return report;
        }
        finally
        {
            // Exact GUID directory created by this invocation, never a user-supplied existing directory.
            if(Directory.Exists(staging))Directory.Delete(staging,recursive:true);
        }
    }
}
