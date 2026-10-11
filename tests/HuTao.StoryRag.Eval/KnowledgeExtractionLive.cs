using System.Text.Json;
using HuTao.Dialogue.Llm;
using HuTao.Foundation;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;

internal static class KnowledgeExtractionLive
{
    public static async Task<int> RunAsync(string root,string output,int limit,JsonSerializerOptions json,int start=0)
    {
        if(limit is <1 or >20 || start<0 || start+limit>20)throw new ArgumentOutOfRangeException(nameof(limit));
        ProjectEnvironment.LoadDotEnv([Path.Combine(root,".env")]);
        var key=Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if(string.IsNullOrWhiteSpace(key))throw new InvalidOperationException("DEEPSEEK_API_KEY required.");
        Directory.CreateDirectory(output);
        var databasePath=Path.Combine(root,"evaluation/knowledge-rag/results/corpus/corpus.db");
        if(!File.Exists(databasePath))throw new InvalidOperationException("Run --knowledge-corpus-checks first.");
        var db=new SqliteKnowledgeDatabase(databasePath);
        var access=KnowledgeAccessContext.ForSession("extraction-fixture","hutao","desktop",["corpus:genshin:canonical"]);
        var evidence=new SqlEvidenceRepository(db);
        var cases=new List<EvidenceRef>();
        string[] texts=["我喜欢咖啡。","我不喜欢咖啡。","假设我喜欢咖啡。","我喜欢温水。","我不喜欢乌龙茶。",
            "建议你喝咖啡。","我喜欢做手工。","我早上喝茶，晚上只喝温水。","甲收到信，随后离开港口。","我答应周六一起改简历。"];
        for(var i=0;i<texts.Length;i++)cases.Add(evidence.Put(access.MemoryNamespace,new("extraction-fixture-"+i,texts[i],i==5?"assistant":"user","fixture:"+i,"synthetic")));
        using(var c=db.Open())
        using(var command=c.CreateCommand())
        {
            command.CommandText="""
                SELECT e.evidence_id,e.revision FROM evidence_units e JOIN namespaces n ON n.namespace_id=e.namespace_id
                WHERE e.namespace_id='corpus:genshin:canonical' AND e.corpus_version=n.active_corpus_version AND e.deleted_at IS NULL
                AND e.evidence_id LIKE 'character:hutao:%' AND length(e.text) BETWEEN 20 AND 400 ORDER BY e.evidence_id LIMIT 10
                """;
            using var rows=command.ExecuteReader();while(rows.Read())cases.Add(new("corpus:genshin:canonical",rows.GetString(0),rows.GetInt64(1)));
        }
        if(cases.Count!=20)throw new InvalidOperationException("Expected ten synthetic and ten public character evidence units.");
        var provider=new DeepSeekLlmProvider(key,temperature:0,transportRetries:1);
        var runner=new ExtractionJobRunner(db,provider);var budget=new ExtractionBudget(maxCalls:20,maxReservedTokens:400000);
        ExtractionEntity[] entities=[new("user","person","用户"),new("hutao","character","胡桃"),new("wangsheng","organization","往生堂"),
            new("zhongli","character","钟离"),new("qiuqiu","character","七七"),new("hat","object","帽子"),new("flower","object","梅花")];
        var results=new List<object>();var errors=0;
        foreach(var reference in cases.Skip(start).Take(limit))
        {
            try
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(50));
                var result=await runner.RunAsync(access,reference,entities,reference.Namespace==access.MemoryNamespace?"user":null,budget,deadline.Token);
                var source=evidence.Read(access,reference)!;
                results.Add(new {reference,source.Text,source.Speaker,result});
                Console.WriteLine($"unit={results.Count} cached={result.Cached} accepted={result.Validations.Count(v=>v.Status=="accepted")} candidate={result.Validations.Count(v=>v.Status=="candidate")} rejected={result.Validations.Count(v=>v.Status=="rejected")}");
            }
            catch(Exception ex)
            {
                errors++;results.Add(new {reference,error=ex.GetType().Name});
                Console.WriteLine($"unit={results.Count} error={ex.GetType().Name}");
            }
            long? promptTokens=null,completionTokens=null;
            using(var c=db.Open())using(var cmd=c.CreateCommand())
            {
                cmd.CommandText="SELECT SUM(prompt_tokens),SUM(completion_tokens) FROM extraction_jobs WHERE model=@model AND extractor_version=@version";
                cmd.Parameters.AddWithValue("@model",provider.ModelIdentity);
                cmd.Parameters.AddWithValue("@version",AssertionExtractor.Version);
                using var row=cmd.ExecuteReader();row.Read();promptTokens=row.IsDBNull(0)?null:row.GetInt64(0);completionTokens=row.IsDBNull(1)?null:row.GetInt64(1);
            }
            await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new {model=provider.ModelIdentity,units=results.Count,errors,
                calls_this_run=budget.Calls,reserved_tokens_this_run=budget.ReservedTokens,prompt_tokens_cached_jobs=promptTokens,completion_tokens_cached_jobs=completionTokens,
                cost="unknown: no verified tariff configured",limits=new{units=20,output_tokens_per_call=provider.MaxOutputTokens,transport_attempts_per_call=1,reserved_tokens=400000},results},json));
        }
        return errors==0?0:1;
    }
}
