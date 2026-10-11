using System.Diagnostics;
using System.Text.Json;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Immersion;
using HuTao.Dialogue.Llm;
using HuTao.Foundation;
using HuTao.Foundation.Diagnostics;
using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Memory;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using HuTao.Persona;
using Neo4j.Driver;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static class KnowledgeAblationSmoke
{
    internal sealed record SeedEntity(string Id,string Type,string Name);
    internal sealed record SeedFact(string Id,string Subject,string Predicate,string? Object,int Source=-1,int Polarity=1,
        string? Literal=null,int[]? Sources=null,Dictionary<string,string>? Qualifiers=null,
        DateTimeOffset? ValidFrom=null,DateTimeOffset? ValidTo=null,string? Supersedes=null);
    private static int[] SourceIndices(SeedFact fact)=>fact.Sources??[fact.Source];
    internal sealed record Scenario(string Id,string[] Originals,string[] Inputs,string[] ReviewCriteria,
        SeedEntity[]? Entities=null,SeedFact[]? Facts=null,int[][]? ExpectedEvidence=null,string? Family=null,string Partition="development");
    internal static Scenario[] LoadFixtures(string path,JsonSerializerOptions json)
    {
        if(new FileInfo(path).Length>1000000)throw new InvalidDataException("Fixture file too large.");
        var options=new JsonSerializerOptions(json){UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
        var cases=JsonSerializer.Deserialize<Scenario[]>(File.ReadAllText(path),options)??throw new InvalidDataException("Missing cases.");
        if(cases.Length is <1 or >100 || cases.Select(c=>c.Id).Distinct().Count()!=cases.Length)throw new InvalidDataException("Invalid case identities.");
        foreach(var c in cases)
        {
            if(c.Partition is not ("development" or "graph-candidate" or "non-graph-control") || c.Family?.Length>100)
                throw new InvalidDataException("Invalid fixture partition.");
            if(c.Id is null || !Regex.IsMatch(c.Id,"^[a-z][a-z0-9-]{0,63}$") || c.Originals is null || c.Originals.Length is <1 or >20 ||
                c.Originals.Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>4000) || c.Inputs is null || c.Inputs.Length is <2 or >4 ||
                c.Inputs.Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>2000) || c.ReviewCriteria is not {Length:>0} ||
                c.Entities is null || c.Entities.Length>30 || c.Facts is null || c.Facts.Length>40 ||
                c.ExpectedEvidence is null || c.ExpectedEvidence.Length!=c.Inputs.Length ||
                c.ExpectedEvidence.Any(ids=>ids is null || ids.Distinct().Count()!=ids.Length || ids.Any(i=>i<0||i>=c.Originals.Length)))
                throw new InvalidDataException("Invalid scenario data.");
            var types=new Dictionary<string,string>{{"user","person"},{"character","character"}};
            foreach(var entity in c.Entities)
                if(string.IsNullOrWhiteSpace(entity.Id)||string.IsNullOrWhiteSpace(entity.Name)||!types.TryAdd(entity.Id,entity.Type))throw new InvalidDataException("Invalid entity identity.");
            var factIds=new HashSet<string>();var previous=new Dictionary<string,SeedFact>();
            foreach(var fact in c.Facts)
            {
                var sources=SourceIndices(fact);
                if(string.IsNullOrWhiteSpace(fact.Id)||!factIds.Add(fact.Id)||sources.Length is <1 or >6 || sources.Distinct().Count()!=sources.Length ||
                    sources.Any(i=>i<0||i>=c.Originals.Length) || fact.Sources is not null && fact.Source!=-1 ||
                    !types.TryGetValue(fact.Subject,out var subject) || (fact.Object is null)==(fact.Literal is null))throw new InvalidDataException("Invalid fact reference.");
                var target="literal";
                if(fact.Object is not null && !types.TryGetValue(fact.Object,out target))throw new InvalidDataException("Missing fact object.");
                if(fact.Literal is not null && (string.IsNullOrWhiteSpace(fact.Literal)||fact.Literal.Length>1000) ||
                    fact.ValidFrom is not null && fact.ValidTo is not null && fact.ValidFrom>=fact.ValidTo ||
                    fact.Qualifiers is {Count:>8} || fact.Qualifiers?.Any(q=>string.IsNullOrWhiteSpace(q.Key)||q.Key.Length>100||string.IsNullOrWhiteSpace(q.Value)||q.Value.Length>200)==true)
                    throw new InvalidDataException("Invalid fact dimensions.");
                PredicateCatalog.Demand(fact.Predicate,subject,target,fact.Polarity);
                if(fact.Supersedes is not null)
                {
                    if(!previous.TryGetValue(fact.Supersedes,out var old)||old.Subject!=fact.Subject||old.Predicate!=fact.Predicate||
                        !Enumerable.SequenceEqual((old.Qualifiers??[]).OrderBy(q=>q.Key,StringComparer.Ordinal),(fact.Qualifiers??[]).OrderBy(q=>q.Key,StringComparer.Ordinal)))
                        throw new InvalidDataException("Correction must reference an earlier matching fact dimension.");
                    previous.Remove(fact.Supersedes);
                }
                previous.Add(fact.Id,fact);
            }
        }
        return cases;
    }
    internal static void SeedExternal(SqlAssertionRepository facts,KnowledgeAccessContext access,Scenario scenario)
    {
        foreach(var entity in scenario.Entities??[])facts.RegisterEntity(access,access.MemoryNamespace,entity.Id,entity.Type,entity.Name);
        foreach(var edge in scenario.Facts??[])
            facts.Put(access,access.MemoryNamespace,new(edge.Id,edge.Subject,edge.Predicate,edge.Object,edge.Literal,edge.Polarity,edge.Qualifiers??[],
                SourceIndices(edge).Select(i=>new AssertionSource(new(access.MemoryNamespace,scenario.Id+":"+i,1),0,scenario.Originals[i].Length,scenario.Originals[i])).ToArray(),
                edge.ValidFrom,edge.ValidTo),DateTimeOffset.UtcNow,edge.Supersedes);
    }
    private static readonly Scenario[] Cases=[
        new("corrected-drink",["我晚上喝乌龙茶。","改一下，我现在晚上只喝温水。","我早上喝茶。"],["我晚上喝什么来着？","那早上呢？"],["晚上温水而非乌龙茶","早上茶","不能把两种时间条件合并"]),
        new("negative-preference",["我不喜欢咖啡。","我喜欢桂花茶。"],["我喜欢喝咖啡，对吧？","那换成什么更合我心意？"],["纠正错误前提，不反转否定","桂花茶","不臆造健康原因"]),
        new("personal-event",["我在给妹妹织一条围巾。","妹妹的生日是十月二十日。"],["我最近准备的礼物是给谁的？","她的生日是什么时候？"],["妹妹和围巾","十月二十日","指代正确"]),
        new("unknown-boundary",["我最近在学做陶器。"],["你还记得我的生日是哪天吗？","没告诉你也没关系，先陪我聊聊最近学的东西吧。"],["承认生日未知","陶器","不编造生日或往事"]),
        new("topic-switch",["我刚把给妹妹织的围巾拆了重织。","我晚上只喝温水。"],["围巾又织错了，有点泄气。","先不聊这个，17乘23是多少？"],["回应眼前情绪且不强塞饮品","391","换题后不被旧记忆抢走话题"]),
        new("two-hop-care",["我认识阿遥。","阿遥在照顾一只名叫团团的猫。"],["我认识的人正在照顾谁？","它叫什么名字？"],["阿遥照顾猫团团","不把猫归属或照顾原因编出来","D实际图路径与SQL复核可见"])
    ];
    public static async Task<int> RunAsync(string root,string output,string group,int limit,JsonSerializerOptions json,string? graphUri=null,int start=0,string? fixturePath=null)
    {
        var cases=fixturePath is null?Cases:LoadFixtures(fixturePath,json);
        var heldoutFixture = fixturePath is not null && cases.All(c =>
            c.Partition is "graph-candidate" or "non-graph-control" &&
            !string.IsNullOrWhiteSpace(c.Family));
        if(group is not ("A" or "B" or "C" or "D") || limit<1 || start<0 || start+limit>cases.Length)throw new ArgumentException("Invalid group or development scenario range.");
        if(group=="D" && string.IsNullOrWhiteSpace(graphUri))throw new ArgumentException("D requires an explicit test --graph-uri.");
        if(Directory.Exists(output))throw new IOException("Ablation output must be a new directory.");
        await using var driver=group=="D"?GraphDatabase.Driver(graphUri!,AuthTokens.None,c=>c.WithConnectionTimeout(TimeSpan.FromSeconds(2)).WithMaxTransactionRetryTime(TimeSpan.Zero)):null;
        Neo4jGraphProjection? projection=null;
        if(driver is not null){await driver.VerifyConnectivityAsync();projection=new(driver);await projection.InitializeAsync();}
        ProjectEnvironment.LoadDotEnv([Path.Combine(root,".env")]);
        var key=Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")??throw new InvalidOperationException("DEEPSEEK_API_KEY required.");
        Directory.CreateDirectory(output);
        const int maxCalls=60;const long maxTokens=1000000;
        var provider=new KnowledgeLiveEvaluation.BudgetedProvider(new DeepSeekLlmProvider(key,temperature:0.2,transportRetries:0),maxCalls,maxTokens);
        var persona=PersonaLoader.Load(Path.Combine(root,"data/persona/hutao"));
        var anchor=new DateTimeOffset(2026,10,8,0,0,0,TimeSpan.Zero);
        var results=new List<object>();var failures=0;var runId="ablation-"+Guid.NewGuid().ToString("N");
        var graphArtifacts=new List<object>();
        var frozen=JsonSerializer.Serialize(cases.Skip(start).Take(limit),json);
        var fixtureHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(frozen)));
        await File.WriteAllTextAsync(Path.Combine(output,"fixtures.json"),frozen);
        foreach(var scenario in cases.Skip(start).Take(limit))
        {
            var directory=Path.Combine(output,scenario.Id);Directory.CreateDirectory(directory);
            var state=new MemoryFileState{Records=scenario.Originals.Select((text,i)=>new MemoryRecord{Id=scenario.Id+":"+i,Text=text,Speaker="user",Kind=MemoryKind.Turn,
                ObservedAt=anchor.AddMinutes(i),RecordedAt=anchor.AddMinutes(i),Importance=0.6,Fingerprint=ConversationMemoryStore.Fingerprint("user",text)}).ToList()};
            var memoryPath=Path.Combine(directory,"memory.json");
            SqliteKnowledgeDatabase? database=null;SqlConversationMemoryRepository? repository=null;KnowledgeConversationContext? context=null;
            var access=KnowledgeAccessContext.ForSession(runId,"hutao",scenario.Id,[]);
            if(group=="A")await File.WriteAllTextAsync(memoryPath,JsonSerializer.Serialize(state,MemoryFileState.Json));
            else
            {
                database=new(Path.Combine(directory,"knowledge.db"));repository=new(database,access);repository.Save(state,0);
                if(JsonSerializer.Serialize(repository.Load().State.Records,MemoryFileState.Json)!=JsonSerializer.Serialize(state.Records,MemoryFileState.Json))
                    throw new InvalidDataException("A/B original IDs, contents or metadata differ after SQL persistence.");
            }
            var store=new ConversationMemoryStore(memoryPath,new MemoryOptions{Enabled=true},new LocalDiagnosticLog(Path.Combine(directory,"memory.log")),repository);
            if(group is "C" or "D")
            {
                var evidence=new SqlEvidenceRepository(database!);var facts=new SqlAssertionRepository(database!);
                facts.RegisterEntity(access,access.MemoryNamespace,"user","person","用户");facts.RegisterEntity(access,access.MemoryNamespace,"character","character","胡桃");
                void Fact(int index,string id,string predicate,string literal,int polarity,Dictionary<string,string>? qualifiers=null,string? replaces=null)
                {
                    var record=state.Records[index];
                    facts.Put(access,access.MemoryNamespace,new(id,"user",predicate,null,literal,polarity,qualifiers??[],[new(new(access.MemoryNamespace,record.Id,1),0,record.Text.Length,record.Text)]),DateTimeOffset.UtcNow,replaces);
                }
                if(fixturePath is null && scenario.Id=="corrected-drink")
                {
                    var evening=new Dictionary<string,string>{{"time_of_day","evening"}};
                    Fact(0,"old-drink","drinks","乌龙茶",1,evening);Fact(1,"current-drink","drinks","温水",1,evening,"old-drink");
                    Fact(2,"morning-drink","drinks","茶",1,new(){{"time_of_day","morning"}});
                }
                if(fixturePath is null && scenario.Id=="negative-preference"){Fact(0,"negative","prefers","咖啡",-1);Fact(1,"positive","prefers","桂花茶",1);}
                if(fixturePath is null && scenario.Id=="two-hop-care")
                {
                    facts.RegisterEntity(access,access.MemoryNamespace,"ayao","person","阿遥");
                    facts.RegisterEntity(access,access.MemoryNamespace,"tuantuan","animal","团团");
                    void Edge(int index,string id,string subject,string predicate,string target)
                    {
                        var record=state.Records[index];
                        facts.Put(access,access.MemoryNamespace,new(id,subject,predicate,target,null,1,new Dictionary<string,string>(),
                            [new(new(access.MemoryNamespace,record.Id,1),0,record.Text.Length,record.Text)]),DateTimeOffset.UtcNow);
                    }
                    Edge(0,"knows-ayao","user","knows","ayao");Edge(1,"cares-tuantuan","ayao","cares_for","tuantuan");
                }
                SeedExternal(facts,access,scenario);
                var strategies=new List<IKnowledgeRetrievalStrategy>{new SqlLexicalStrategy(evidence),new SqlFactsStrategy(facts),new SqlSemanticStrategy()};
                if(projection is not null)
                {
                    using var setupDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var rebuilt=await new GraphRebuilder(database!,projection).RebuildAsync(access,access.MemoryNamespace,setupDeadline.Token);
                    if(!rebuilt.Activated)throw new InvalidOperationException("Graph fixture did not activate: "+rebuilt.Reason);
                    graphArtifacts.Add(new{namespace_id=access.MemoryNamespace,rebuild=rebuilt});
                    await File.WriteAllTextAsync(Path.Combine(output,"graph-artifacts.json"),JsonSerializer.Serialize(graphArtifacts,json));
                    strategies.Add(new GraphExpandStrategy(new(database!,driver!),facts));
                }
                var catalog=new KnowledgeStrategyCatalog(strategies);
                context=new(access,new(catalog,evidence),new(database!),new Dictionary<string,string>{{"memory",access.MemoryNamespace}},
                    new Dictionary<string,ConversationParticipants>{{"memory",new("user","character")}},catalog.Available);
            }
            var agent=new ReactAgent(persona,provider,null,[],memory:store,knowledge:context,
                immersionOptions:new ImmersionOptions{EnableCritic=false,EnableRuleGate=true,MaxGenerationAttempts=1,MaxRetrievalRetries=0},
                diagnostics:new LocalDiagnosticLog(Path.Combine(directory,"runtime.log")));
            var turns=new List<object>();
            for(var turnIndex=0;turnIndex<scenario.Inputs.Length;turnIndex++)
            {
                var input=scenario.Inputs[turnIndex];
                var watch=Stopwatch.StartNew();var before=provider.Calls;
                try
                {
                    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(75));
                    var result=await agent.GenerateTextAsync(input,false,deadline.Token);
                    var expected=scenario.ExpectedEvidence?[turnIndex].Select(i=>scenario.Id+":"+i).ToArray();
                    var retrieved=(result.Recall?.Knowledge?.Evidence.Select(e=>e.Reference.Id)??result.Recall?.Memories.Select(m=>m.Record.Id)??[]).ToHashSet();
                    turns.Add(new{input,result.Reply,validation=result.AnswerValidationLevel.ToString(),conversation_support=result.ConversationSupport,recall=result.Recall,expected_evidence_ids=expected,
                        expected_evidence_covered=expected is {Length:>0}?(bool?)expected.All(retrieved.Contains):null,elapsed_ms=watch.ElapsedMilliseconds,api_calls=provider.Calls-before});
                }
                catch(Exception ex){failures++;turns.Add(new{input,error=ex.GetType().Name,elapsed_ms=watch.ElapsedMilliseconds,api_calls=provider.Calls-before});}
            }
            store.Flush();results.Add(new{scenario.Id,scenario.Family,scenario.Partition,scenario.ReviewCriteria,turns});
            await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{stage="abcd-development-smoke-v2",group,dense=false,
                fixture_sha256=fixtureHash,fixture_source=fixturePath is null?"builtin-development":(heldoutFixture?"external-heldout-unreviewed":"external-agent-authored-unreviewed"),
                graph=driver is not null,graph_artifacts=graphArtifacts,public_corpus=false,tts=false,seed_facts="C/D have identical reviewed synthetic facts; A/B identical originals",not_heldout=!heldoutFixture,human_scores_pending=true,
                model="DeepSeek configured provider",temperature=0.2,transport_retries=0,max_calls=maxCalls,max_reserved_tokens=maxTokens,
                failures,calls=provider.Calls,reserved_tokens=provider.ReservedTokens,usage=provider.Usage,cost="unknown",scenarios=results},json));
            Console.WriteLine($"Ablation {group} {scenario.Id}; calls={provider.Calls}; failures={failures}");
        }
        return failures==0?0:1;
    }
}
