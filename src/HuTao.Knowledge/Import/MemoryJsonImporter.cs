using System.Security.Cryptography;
using System.Text.Json;
using HuTao.Knowledge.Memory;

namespace HuTao.Knowledge.Import;

public sealed record MemoryImportReport(string Sha256,int Records,IReadOnlyList<string> UnresolvedSourceIds,bool AlreadyImported,bool DryRun)
{
    public int ArchivedRecords {get;init;}
}

/// <summary>Explicit migration only. Never silently imports JSON while the SQL backend is running.</summary>
public sealed class MemoryJsonImporter(IConversationMemoryRepository repository)
{
    public MemoryImportReport Import(string path,bool dryRun=true,string? expectedSha256=null)
    {
        var bytes=File.ReadAllBytes(path);
        var archivePath=Path.ChangeExtension(path,null)+".archive.jsonl";
        var archiveBytes=File.Exists(archivePath)?File.ReadAllBytes(archivePath):null;
        var sha256=Convert.ToHexString(SHA256.HashData(bytes));
        if(archiveBytes is not null)
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("memory-with-archive-v1\0"u8);
            hash.AppendData(SHA256.HashData(bytes));hash.AppendData(SHA256.HashData(archiveBytes));
            sha256=Convert.ToHexString(hash.GetHashAndReset());
        }
        if(expectedSha256 is not null && !string.Equals(expectedSha256,sha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Memory source changed since review; repeat the migration dry-run.");
        // The legacy store writes Encoding.UTF8, including its BOM. Hash the original bytes,
        // but remove the optional preamble before passing UTF-8 JSON to the serializer.
        var json=bytes.AsSpan();
        if(json.StartsWith(new byte[]{0xef,0xbb,0xbf}))json=json[3..];
        var state=JsonSerializer.Deserialize<MemoryFileState>(json,MemoryFileState.Json)??throw new InvalidDataException("Invalid memory file.");
        if(state.Version!=1)throw new InvalidDataException("Unsupported memory file version.");
        var archivedIds=new HashSet<string>(StringComparer.Ordinal);
        if(archiveBytes is not null)
        {
            using var reader=new StreamReader(new MemoryStream(archiveBytes),System.Text.Encoding.UTF8,detectEncodingFromByteOrderMarks:true);
            while(reader.ReadLine() is { } line)
            {
                if(string.IsNullOrWhiteSpace(line))continue;
                var record=JsonSerializer.Deserialize<MemoryRecord>(line,MemoryFileState.Json)??throw new InvalidDataException("Invalid archive record.");
                if(!archivedIds.Add(record.Id))throw new InvalidDataException("Duplicate archived memory IDs require review.");
                state.Records.Add(record);
            }
        }
        var ids=state.Records.Select(r=>r.Id).ToHashSet(StringComparer.Ordinal);
        if(ids.Count!=state.Records.Count)throw new InvalidDataException("Duplicate memory IDs.");
        var unresolved=state.Records.SelectMany(r=>r.Sources.Concat(r.SupersededById is null?[]:new[]{r.SupersededById}))
            .Where(id=>!ids.Contains(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var current=repository is SqlConversationMemoryRepository sql?sql.LoadForMigration():repository.Load();
        var same=Canonical(current.State)==Canonical(state) && current.ArchivedIds.SetEquals(archivedIds);
        var report=new MemoryImportReport(sha256,state.Records.Count,unresolved,same,dryRun){ArchivedRecords=archivedIds.Count};
        if(dryRun || same)return report;
        if(unresolved.Length>0)throw new InvalidDataException("Unresolved memory sources; review dry-run before import.");
        if(current.Revision!=0 || current.State.Records.Count>0)throw new InvalidOperationException("Import requires an empty target; SQL already owns state.");
        if(repository is SqlConversationMemoryRepository target)target.ImportSnapshot(state,archivedIds);
        else if(archivedIds.Count>0)throw new InvalidOperationException("Target repository cannot preserve archived memory.");
        else repository.Save(state,current.Revision);
        return report;
    }

    public void Export(string destination)
    {
        // Archive guard and contents must describe the same SQLite read snapshot.
        var snapshot=repository is SqlConversationMemoryRepository sql?sql.LoadForLegacyExport():repository.Load();
        DemandLegacyCompatible(snapshot);
        var fullPath=Path.GetFullPath(destination);
        var temporary=Path.Combine(Path.GetDirectoryName(fullPath)!,".memory-export-"+Guid.NewGuid().ToString("N")+".tmp");
        var created=false;
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                created=true;
                JsonSerializer.Serialize(stream,snapshot.State,MemoryFileState.Json);
                stream.Flush(flushToDisk:true);
            }
            File.Move(temporary,fullPath,overwrite:false);
        }
        finally {if(created && File.Exists(temporary))File.Delete(temporary);}
    }

    public static void DemandLegacyCompatible(MemoryRepositorySnapshot snapshot)
    {
        var ids=snapshot.State.Records.Select(r=>r.Id).ToHashSet(StringComparer.Ordinal);
        if(snapshot.State.Records.Any(r=>r.SourceRevisions.Any(source=>source.Value!=1 ||
                snapshot.EvidenceRevisions.TryGetValue(source.Key,out var current) && current!=source.Value) ||
            r.Sources.Any(source=>!ids.Contains(source)) || r.SupersededById is not null && !ids.Contains(r.SupersededById)))
            throw new InvalidOperationException("Legacy JSON cannot preserve this revision-level provenance; use a versioned SQL backup/export.");
    }

    private static string Canonical(MemoryFileState original)
    {
        var state=JsonSerializer.Deserialize<MemoryFileState>(JsonSerializer.Serialize(original,MemoryFileState.Json),MemoryFileState.Json)!;
        state.LastConsolidatedAt=state.LastConsolidatedAt?.ToUniversalTime();
        state.Records=state.Records.OrderBy(r=>r.Id,StringComparer.Ordinal).ToList();
        foreach(var record in state.Records)
        {
            record.ObservedAt=DateTimeOffset.FromUnixTimeMilliseconds(record.ObservedAt.ToUnixTimeMilliseconds());
            record.RecordedAt=record.RecordedAt.ToUniversalTime();
            record.ValidUntil=MillisecondTime(record.ValidUntil);
            record.LastAccessedAt=MillisecondTime(record.LastAccessedAt);
            record.ConsolidatedAt=MillisecondTime(record.ConsolidatedAt);
            record.Sources=record.Sources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        }
        state.LastConsolidatedAt=MillisecondTime(state.LastConsolidatedAt);
        return JsonSerializer.Serialize(state,MemoryFileState.Json);
    }
    private static DateTimeOffset? MillisecondTime(DateTimeOffset? value)=>value is null?null:DateTimeOffset.FromUnixTimeMilliseconds(value.Value.ToUnixTimeMilliseconds());
}
