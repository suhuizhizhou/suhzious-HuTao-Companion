using System.Text.Json;
using HuTao.Knowledge.Import;
using HuTao.Knowledge.Memory;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using HuTao.Persona;

internal static class KnowledgeMigrationPreparation
{
    public static async Task<int> RunAsync(string root,string package,string output,JsonSerializerOptions json)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output))throw new IOException("Use a new preparation directory.");
        Directory.CreateDirectory(output);
        var target=Path.Combine(output,"candidate.db");
        KnowledgeBackupPackage.Restore(package,target);
        var database=new SqliteKnowledgeDatabase(target);
        var reports=new List<object>();
        foreach(var source in Directory.EnumerateFiles(Path.Combine(root,"data"),"memory_*.json"))
        {
            var identity=Path.GetFileNameWithoutExtension(source)[7..];
            var scope=identity.EndsWith("_chatroom",StringComparison.Ordinal)?"chatroom":"desktop";
            var persona=scope=="chatroom"?identity[..^9]:identity;
            if(CharacterCatalog.Find(persona) is null)throw new InvalidDataException("Unknown memory owner; manual mapping required.");
            var access=KnowledgeAccessContext.ForSession("local",persona,scope,[]);
            var importer=new MemoryJsonImporter(new SqlConversationMemoryRepository(database,access));
            var preview=importer.Import(source);
            if(preview.UnresolvedSourceIds.Count>0)throw new InvalidDataException("Unresolved memory references; no activation performed.");
            importer.Import(source,false,preview.Sha256);
            var verified=importer.Import(source);
            if(!verified.AlreadyImported)throw new InvalidDataException("Post-import comparison failed.");
            reports.Add(new{file=Path.GetFileName(source),persona,scope,preview.Records,preview.ArchivedRecords,preview.Sha256,verified=verified.AlreadyImported});
        }
        var report=new{candidate=target,memory=reports,activation_performed=false,
            remaining=new[]{"important items and orphan chat-log reconciliation","final source validation under writer exclusion","corpus bindings and atomic host activation","live restart, correction, forgetting, voice/tool acceptance"}};
        await File.WriteAllTextAsync(Path.Combine(output,"preparation.json"),JsonSerializer.Serialize(report,json));
        Console.WriteLine(JsonSerializer.Serialize(report,json));return 0;
    }
}
