using System.Text.Json;
using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;

internal static class KnowledgeFixturePreflight
{
    public static async Task<int> RunAsync(string path,string output,JsonSerializerOptions json)
    {
        var cases=KnowledgeAblationSmoke.LoadFixtures(path,json);
        if(Directory.Exists(output))throw new IOException("Preflight output must be new.");
        Directory.CreateDirectory(output);var rows=new List<object>();
        foreach(var scenario in cases)
        {
            var db=new SqliteKnowledgeDatabase(Path.Combine(output,scenario.Id+".db"));
            var access=KnowledgeAccessContext.ForSession("fixture-preflight","hutao",scenario.Id,[]);
            var evidence=new SqlEvidenceRepository(db);var facts=new SqlAssertionRepository(db);
            facts.RegisterEntity(access,access.MemoryNamespace,"user","person","用户");
            facts.RegisterEntity(access,access.MemoryNamespace,"character","character","胡桃");
            for(var i=0;i<scenario.Originals.Length;i++)
                evidence.Put(access.MemoryNamespace,new(scenario.Id+":"+i,scenario.Originals[i],"user",scenario.Id+":"+i,"turn"));
            KnowledgeAblationSmoke.SeedExternal(facts,access,scenario);
            var current=scenario.Facts!.Length==0?Array.Empty<AssertionFact>():
                facts.Query(access,access.MemoryNamespace,new("",Limit:100,AssertionIds:scenario.Facts.Select(f=>f.Id).ToArray()));
            rows.Add(new{scenario.Id,current_facts=current.Select(f=>new{f.Id,f.Status,f.Literal,f.ObjectId,f.QualifiersJson,f.ValidFrom,f.ValidTo,sources=f.Sources.Select(s=>s.Reference.Id)})});
        }
        await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{schema_and_sql_seed="passed",semantic_labels="not independently reviewed",cases=rows},json));
        Console.WriteLine($"PASS real SQL fixture preflight: {cases.Length} scenarios");return 0;
    }
}
