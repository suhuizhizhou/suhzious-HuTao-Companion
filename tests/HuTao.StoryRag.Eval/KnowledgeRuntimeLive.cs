using System.Diagnostics;
using System.Text.Json;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Llm;
using HuTao.Dialogue.Runtime;
using HuTao.Foundation;
using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Storage;
using HuTao.Persona;

internal static class KnowledgeRuntimeLive
{
    public static async Task<int> RunAsync(string root,string output,JsonSerializerOptions json)
    {
        ProjectEnvironment.LoadDotEnv([Path.Combine(root,".env")]);
        var key=Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if(string.IsNullOrWhiteSpace(key))throw new InvalidOperationException("DEEPSEEK_API_KEY required.");
        Directory.CreateDirectory(output);
        var path=Path.Combine(output,"knowledge.db");
        if(File.Exists(path))throw new IOException("Use a fresh runtime evaluation directory.");
        var db=new SqliteKnowledgeDatabase(path);
        var provider=new KnowledgeLiveEvaluation.BudgetedProvider(new DeepSeekLlmProvider(key,temperature:0.2,transportRetries:1));
        var definition=new CharacterDefinition("hutao","胡桃","","",Path.Combine(root,"data/persona/hutao"),"","","missing.json","你好","","忙碌",Tools:["document_reader"]);
        var oldMemory=Environment.GetEnvironmentVariable("HU_TAO_MEMORY");
        Environment.SetEnvironmentVariable("HU_TAO_MEMORY","true");
        var turns=new List<object>();var failures=0;
        try
        {
            await using var runtime=new AgentRuntimeFactory(output,knowledgeOptions:new(KnowledgeRuntimeMode.Sql,path,UserId:"runtime-synthetic"),llmFactory:_=>provider).Create(definition);
            foreach(var input in new[]{"我早上喝茶，晚上只喝乌龙茶。","改一下，我现在晚上只喝温水。","我晚上喝什么来着？早上呢？"})
            {
                var clock=Stopwatch.StartNew();var before=provider.Calls;
                try
                {
                    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(75));
                    var result=await ((ReactAgent)runtime.Agent).GenerateTextAsync(input,false,deadline.Token);
                    var access=runtime.Knowledge!.Context.Access;
                    var facts=new SqlAssertionRepository(db).Query(access,access.MemoryNamespace,new("user","drinks"));
                    turns.Add(new{input,result.Reply,validation=result.AnswerValidationLevel.ToString(),result.Recall?.Planning,steps=result.Recall?.Steps,bundle=result.Recall?.Knowledge,ingestion=runtime.Knowledge.LastIngestionStatus,facts,elapsed_ms=clock.ElapsedMilliseconds,api_calls=provider.Calls-before});
                }
                catch(Exception ex){failures++;turns.Add(new{input,error=ex.GetType().Name,elapsed_ms=clock.ElapsedMilliseconds});}
                await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{stage="runtime-live-learning-smoke",synthetic_only=true,not_blind_evaluation=true,not_ablation=true,failures,turns,calls=provider.Calls,usage=provider.Usage,reserved_tokens=provider.ReservedTokens,max_calls=30,max_reserved_tokens=400000,cost="unknown"},json));
                Console.WriteLine($"Runtime turn {turns.Count}/3; calls={provider.Calls}; failures={failures}");
            }
        }
        finally{Environment.SetEnvironmentVariable("HU_TAO_MEMORY",oldMemory);}
        return failures==0?0:1;
    }
}
