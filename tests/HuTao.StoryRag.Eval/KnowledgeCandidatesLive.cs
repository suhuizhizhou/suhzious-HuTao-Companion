using System.Diagnostics;
using System.Text.Json;
using HuTao.Dialogue.Runtime;
using HuTao.Foundation;
using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Memory;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;

internal static class KnowledgeCandidatesLive
{
    public static async Task<int> RunAsync(string root,string output,JsonSerializerOptions json)
    {
        ProjectEnvironment.LoadDotEnv([Path.Combine(root,".env")]);
        if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")))throw new InvalidOperationException("DEEPSEEK_API_KEY required.");
        Directory.CreateDirectory(output);var path=Path.Combine(output,"knowledge.db");
        if(File.Exists(path))throw new IOException("Use a new output directory.");
        var db=new SqliteKnowledgeDatabase(path);var access=KnowledgeAccessContext.ForSession("candidate-synthetic","hutao","desktop",[]);
        new SqlAssertionRepository(db).RegisterEntity(access,access.MemoryNamespace,"user","person","用户");
        var store=new ConversationMemoryStore(Path.Combine(output,"unused.json"),repository:new SqlConversationMemoryRepository(db,access));
        var reports=new List<object>();var failures=0;
        string[] Command(string action,params string[] extra)=>[action,"--db",path,"--user","candidate-synthetic","--persona","hutao","--scope","desktop",..extra];
        foreach(var input in new[]{"我照顾一只叫大咪的猫。","改一下，我现在晚上只喝温水，早上才喝茶。","假如我喜欢咖啡就好了。"})
        {
            var record=store.ObserveTurn("user",input,DateTimeOffset.UtcNow,deduplicate:false)!;
            var queue=new TurnIngestionQueue(db);var lease=queue.Claim(access)!;queue.Complete(access,lease,"deferred");
            var watch=Stopwatch.StartNew();var extraction=new StringWriter();
            var code=await KnowledgeAdminCommand.RunAsync(Command("extract","--source",record.Id,"--revision","1"),extraction);
            if(code!=0){failures++;reports.Add(new{input,code,error=extraction.ToString()});}
            else
            {
                var extracted=JsonDocument.Parse(extraction.ToString()).RootElement.Clone();var review=new StringWriter();
                var reviewCode=await KnowledgeAdminCommand.RunAsync(Command("review","--job",extracted.GetProperty("JobId").GetString()!),review);
                if(reviewCode!=0)failures++;
                reports.Add(new{input,code,extracted,review=reviewCode==0?JsonDocument.Parse(review.ToString()).RootElement.Clone():(JsonElement?)null,elapsed_ms=watch.ElapsedMilliseconds});
            }
            await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{stage="candidate-cli-live-smoke",synthetic_only=true,not_blind_evaluation=true,not_ablation=true,failures,max_calls=3,max_reserved_tokens=192000,cost="unknown",cases=reports},json));
            Console.WriteLine($"Candidate {reports.Count}/3; command failures={failures}");
        }
        return failures==0?0:1;
    }
}
