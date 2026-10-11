using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Immersion;
using HuTao.Dialogue.Llm;
using HuTao.Foundation;
using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;
using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Import;
using HuTao.Knowledge.Rag;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using HuTao.Persona;

internal static class KnowledgeLiveEvaluation
{
    public static async Task<int> RunAsync(string root,string output,int limit,JsonSerializerOptions json)
    {
        if(limit is <1 or >5)throw new ArgumentOutOfRangeException(nameof(limit),"Wiring smoke is capped at five scenarios.");
        ProjectEnvironment.LoadDotEnv([Path.Combine(root,".env")]);
        var key=Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if(string.IsNullOrWhiteSpace(key))throw new InvalidOperationException("DEEPSEEK_API_KEY required.");
        Directory.CreateDirectory(output);
        var publicDb=new SqliteKnowledgeDatabase(Path.Combine(root,"evaluation/knowledge-rag/results/corpus/corpus.db"));
        var publicLines=new List<StoryDialogueLine>();
        using(var c=publicDb.Open())using(var command=c.CreateCommand())
        {
            command.CommandText="""
                SELECT e.metadata_json FROM evidence_units e JOIN namespaces n ON n.namespace_id=e.namespace_id
                WHERE e.namespace_id='corpus:genshin:canonical' AND e.corpus_version=n.active_corpus_version AND e.deleted_at IS NULL
                    AND e.evidence_id LIKE 'character:hutao:%' AND json_extract(e.metadata_json,'$.text_hash') IN (
                        SELECT json_extract(anchor.metadata_json,'$.text_hash') FROM evidence_units anchor
                        WHERE anchor.namespace_id=e.namespace_id AND anchor.corpus_version=e.corpus_version AND anchor.evidence_id LIKE 'character:hutao:%'
                        AND (anchor.text LIKE '%帽%' OR anchor.text LIKE '%梅花%'))
                ORDER BY e.evidence_id LIMIT 200
                """;
            using var rows=command.ExecuteReader();while(rows.Read())publicLines.Add(JsonSerializer.Deserialize<StoryDialogueLine>(rows.GetString(0))!);
        }
        if(publicLines.Count==0)throw new InvalidDataException("Public source corpus is unavailable.");
        var provider=new BudgetedProvider(new DeepSeekLlmProvider(key,temperature:0.2,transportRetries:1));
        string[][] cases=[
            ["晚上写累了，想喝点东西，你觉得我喝什么好？"],
            ["我之前改过晚上的饮品习惯，现在我晚上喝什么？"],
            ["给妹妹的围巾又拆了，歇会儿吧。你帽子上的梅花怎么做的？"],
            ["你帽子上的梅花是怎么做出来的？","那要等多久？"],
            ["我晚上喝什么来着？","换个话题，帮我计算17乘以23。"]];
        var report=new List<object>();var failures=0;
        for(var index=0;index<limit;index++)
        {
            var directory=Path.Combine(output,"scenario-"+(index+1));Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,"knowledge.db");
            if(File.Exists(path))throw new IOException("Live output database must be new; use a new output directory.");
            var db=new SqliteKnowledgeDatabase(path);var evidence=new SqlEvidenceRepository(db);var facts=new SqlAssertionRepository(db);
            var access=KnowledgeAccessContext.ForSession("live-synthetic","hutao","desktop",["corpus:live-public"]);var space=access.MemoryNamespace;
            facts.RegisterEntity(access,space,"user","person","用户");facts.RegisterEntity(access,space,"hutao","character","胡桃");
            AssertionSource Source(string id,string text,int days)
            {
                var reference=evidence.Put(space,new(id,text,"user",id,"synthetic",ObservedAt:DateTimeOffset.UtcNow.AddDays(days)));
                return new(reference,0,text.Length,text);
            }
            var old=Source("old-drink","我晚上喝乌龙茶。",-5);
            var updated=Source("updated-drink","改一下，我现在晚上只喝温水，早上才喝茶。",-1);
            Source("coffee","我喝咖啡会心悸。",-3);
            Source("gift","我在给妹妹织围巾，昨天织错又拆掉了。",-2);
            var evening=new Dictionary<string,string>{{"time_of_day","evening"}};
            facts.Put(access,space,new("old","user","drinks",null,"乌龙茶",1,evening,[old]),DateTimeOffset.UtcNow);
            facts.Put(access,space,new("updated","user","drinks",null,"温水",1,evening,[updated]),DateTimeOffset.UtcNow,"old");
            facts.Put(access,space,new("morning","user","drinks",null,"茶",1,new Dictionary<string,string>{{"time_of_day","morning"}},[updated]),DateTimeOffset.UtcNow);
            new StorySqlImporter(db).ImportLines("corpus:live-public",publicLines,"public-hat-subset-v1");
            var catalog=new KnowledgeStrategyCatalog([new SqlLexicalStrategy(evidence),new SqlFactsStrategy(facts)]);
            var context=new KnowledgeConversationContext(access,new(catalog,evidence),new(db),
                new Dictionary<string,string>{{"memory",space},{"story","corpus:live-public"}},
                new Dictionary<string,ConversationParticipants>{{"memory",new("user","hutao")}},catalog.Available);
            var persona=PersonaLoader.Load(Path.Combine(root,"data/persona/hutao"));
            var agent=new ReactAgent(persona,provider,null,[],knowledge:context,
                immersionOptions:new ImmersionOptions{EnableCritic=false,EnableRuleGate=true,MaxGenerationAttempts=1,MaxRetrievalRetries=0},
                diagnostics:new LocalDiagnosticLog(Path.Combine(directory,"runtime.log")));
            var turns=new List<object>();
            foreach(var input in cases[index])
            {
                var watch=Stopwatch.StartNew();var before=provider.Calls;
                try
                {
                    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(75));
                    var result=await agent.GenerateTextAsync(input,false,deadline.Token);
                    turns.Add(new {input,result.Reply,validation=result.AnswerValidationLevel.ToString(),result.StoryAnswer?.Path,
                        issues=result.StoryAnswer?.Issues,raw_reply=result.StoryAnswer?.RawReply,result.Recall?.Planning,steps=result.Recall?.Steps,bundle=result.Recall?.Knowledge,
                        elapsed_ms=watch.ElapsedMilliseconds,api_calls=provider.Calls-before});
                }
                catch(Exception ex) {failures++;turns.Add(new{input,error=ex.GetType().Name,elapsed_ms=watch.ElapsedMilliseconds,api_calls=provider.Calls-before});}
            }
            report.Add(new {scenario=index+1,turns});
            await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new {stage="sql-agent-wiring-smoke",mode="sql",
                corpus="public hat subset, not full corpus",not_blind_evaluation=true,not_ablation=true,failures,scenarios=report,
                calls=provider.Calls,usage=provider.Usage,cost="unknown",max_calls=30,reserved_tokens=provider.ReservedTokens,max_reserved_tokens=400000},json));
            Console.WriteLine($"Scenario {index+1}/{limit}; calls={provider.Calls}; failures={failures}");
        }
        return failures==0?0:1;
    }
    internal sealed class BudgetedProvider(IMeteredLLMProvider inner,int maxCalls=30,long maxReservedTokens=400000) : ILLMProvider
    {
        public string Name=>inner.Name;
        public int Calls {get;private set;}
        public long ReservedTokens {get;private set;}
        public List<object> Usage {get;}=[];
        public async Task<string> CompleteAsync(string prompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)
        {
            var reserve=Encoding.UTF8.GetByteCount(prompt)+history.Sum(h=>Encoding.UTF8.GetByteCount(h.Content)+32)+inner.MaxOutputTokens+256;
            if(Calls>=maxCalls || ReservedTokens+reserve>maxReservedTokens)throw new InvalidOperationException("Live request budget exhausted.");
            Calls++;ReservedTokens+=reserve;var watch=Stopwatch.StartNew();
            try
            {
                var result=await inner.CompleteWithUsageAsync(prompt,history,ct);
                Usage.Add(new{call=Calls,result.PromptTokens,result.CompletionTokens,result.HttpAttempts,result.Model,raw_reply=result.Text,elapsed_ms=watch.ElapsedMilliseconds});
                return result.Text;
            }
            catch(Exception ex) {Usage.Add(new{call=Calls,error=ex.GetType().Name,prompt_tokens=(int?)null,completion_tokens=(int?)null});throw;}
        }
    }
}
