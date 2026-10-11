using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using HuTao.Knowledge.Graph;

internal static class KnowledgeFactChecks
{
    public static List<CheckResult> Run()
    {
        var results=new List<CheckResult>();
        void Check(string name,bool ok)=>results.Add(new("fact_"+name,ok,""));
        void Reject(string name,Action action)
        {try {action();Check(name,false);}catch(Exception ex) when(ex is InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){Check(name,true);}}
        var root=Path.Combine(Path.GetTempPath(),"hutao-facts-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var db=new SqliteKnowledgeDatabase(Path.Combine(root,"test.db"));
            var access=KnowledgeAccessContext.ForSession("facts","hutao","desktop",[]);var space=access.MemoryNamespace;
            var evidence=new SqlEvidenceRepository(db);var facts=new SqlAssertionRepository(db);
            facts.RegisterEntity(access,space,"user","person","用户");
            AssertionSource Source(string id,string text)
            {var reference=evidence.Put(space,new(id,text,"user",id,"turn"));return new(reference,0,text.Length,text);}
            var tea=Source("tea","我早上喝茶，晚上喝乌龙茶，咖啡会心悸。");
            var water=Source("water","改了，晚上只喝温水。");
            facts.RegisterEntity(access,space,"hutao","character","胡桃");
            facts.RegisterEntity(access,space,"walnut","object","胡桃");
            facts.RegisterEntity(access,space,"person-a","person","小明");
            facts.RegisterEntity(access,space,"person-b","person","小明");
            var resolver=new EntityResolver(db);
            Check("same_name_not_merged",resolver.Resolve(access,space,"胡桃").Status=="ambiguous");
            Check("type_disambiguates",resolver.Resolve(access,space,"胡桃","character").Candidates.Single().Id=="hutao");
            Check("same_type_same_name_ambiguous",resolver.Resolve(access,space,"小明").Candidates.Count==2);
            Check("pronoun_requires_participants",resolver.Resolve(access,space,"我").Status=="unknown");
            Check("pronoun_speaker_perspective",resolver.Resolve(access,space,"我",participants:new("user","hutao")).Candidates.Single().Id=="user" && resolver.Resolve(access,space,"我",participants:new("hutao","user")).Candidates.Single().Id=="hutao");
            var alias=Source("alias","桃桃是胡桃的称呼。");
            resolver.AddAlias(access,space,"hutao","桃桃",alias);
            Check("attributed_alias",resolver.Resolve(access,space,"桃桃").Candidates.Single().Id=="hutao");
            Reject("invented_alias",()=>resolver.AddAlias(access,space,"hutao","不存在的称呼",alias));
            Reject("invalid_predicate_type",()=>PredicateCatalog.Demand("made","object","person",1));
            var extractionEntities=new[]{new ExtractionEntity("user","person","用户"),new ExtractionEntity("hutao","character","胡桃")};
            AssertionValidation Validate(string text,ExtractedAssertion candidate,string speaker="user")=>AssertionValidator.Validate("extracted",new(new(space,"fixture",1),text,speaker,"turn","fixture",""),candidate,extractionEntities,"user");
            ExtractedAssertion Preference(string quote,int polarity=1)=>new("user","prefers",null,"咖啡",polarity,[],0,quote.Length,quote);
            Check("extract_explicit_preference_accepted",Validate("我喜欢咖啡。",Preference("我喜欢咖啡。")).Status=="accepted");
            Check("extract_negation_preserved",Validate("我不喜欢咖啡。",Preference("我不喜欢咖啡。",-1)).Status=="accepted");
            Check("extract_negation_reversal_not_accepted",Validate("我不喜欢咖啡。",Preference("我不喜欢咖啡。")).Status!="accepted");
            Check("extract_truncated_negation_not_accepted",Validate("我不喜欢咖啡。",Preference("喜欢咖啡。") with {StartUtf16=2,EndUtf16=7}).Status=="candidate");
            Check("extract_hypothesis_not_accepted",Validate("假设我喜欢咖啡。",Preference("假设我喜欢咖啡。")).Status!="accepted");
            Check("extract_assistant_preference_rejected",Validate("我喜欢咖啡。",Preference("我喜欢咖啡。"),"assistant").Status=="rejected");
            Check("extract_fake_entity_rejected",Validate("我喜欢咖啡。",Preference("我喜欢咖啡。") with {SubjectId="invented"}).Status=="rejected");
            Check("extract_fake_quote_rejected",Validate("我喜欢咖啡。",Preference("我喜欢温水。")).Status=="rejected");
            Check("extract_causal_claim_needs_support",Validate("我认识胡桃。",new("user","causes","hutao",null,1,[],0,6,"我认识胡桃。")).Status=="rejected");
            var chronology="甲收到信，随后离开港口。";
            var causal=AssertionValidator.Validate("causal",new(new(space,"events",1),chronology,"user","turn","events",""),
                new("receiving","causes","leaving",null,1,[],0,chronology.Length,chronology),
                [new("receiving","event","收到信"),new("leaving","event","离开")],"user");
            Check("event_sequence_not_accepted_as_cause",causal.Status=="candidate");
            var evening=new Dictionary<string,string>{{"time_of_day","evening"}};
            var morning=new Dictionary<string,string>{{"time_of_day","morning"}};
            var time1=DateTimeOffset.UtcNow;
            facts.Put(access,space,new("evening-tea","user","prefers",null,"乌龙茶",1,evening,[tea]),time1);
            facts.Put(access,space,new("morning-tea","user","prefers",null,"茶",1,morning,[tea]),time1);
            facts.Put(access,space,new("coffee","user","reacts_to",null,"咖啡心悸",1,new Dictionary<string,string>(),[tea]),time1);
            var time2=DateTimeOffset.UtcNow.AddMilliseconds(1);
            facts.Put(access,space,new("evening-water","user","prefers",null,"温水",1,evening,[water],ValidFrom:time2),time2,"evening-tea");
            var known=time2.AddMinutes(1);
            var current=facts.Query(access,space,new("user",TimeMode:KnowledgeTimeMode.Historical,AsOf:known,KnownAt:known));
            Check("correction_only_same_dimension",current.Any(f=>f.Id=="evening-water") && current.Any(f=>f.Id=="morning-tea") && current.Any(f=>f.Id=="coffee") && current.All(f=>f.Id!="evening-tea"));
            var old=facts.Query(access,space,new("user","prefers",evening,KnowledgeTimeMode.Historical,time1,time1));
            Check("known_at_preserves_old_statement",old.Count==1 && old[0].Literal=="乌龙茶");
            var past=facts.Query(access,space,new("user","prefers",evening,KnowledgeTimeMode.Historical,time1,known));
            Check("as_of_recalls_superseded_fact",past.Count==1 && past[0].Literal=="乌龙茶" && past[0].Status=="superseded");
            Check("unknown_time_not_fabricated",current.Single(f=>f.Id=="morning-tea").ValidFrom is null);
            Reject("correction_dimension_mismatch",()=>facts.Put(access,space,new("bad","user","prefers",null,"水",1,morning,[water]),known,"evening-water"));
            Reject("forged_quote",()=>facts.Put(access,space,new("fake","user","prefers",null,"水",1,morning,[water with {Quote="伪造原文"}]),known));
            Reject("cross_scope_source",()=>facts.Put(access,space,new("cross","user","prefers",null,"水",1,morning,[water with {Reference=new("memory:other:hutao:desktop","water",1)}]),known));
            Reject("missing_entity",()=>facts.Put(access,space,new("missing","unknown","prefers",null,"水",1,morning,[water]),known));
            Reject("unknown_predicate",()=>facts.Put(access,space,new("unknown-p","user","execute_sql",null,"水",1,morning,[water]),known));
            var other=Source("conflict","我早上不喜欢茶。");
            facts.Put(access,space,new("morning-not-tea","user","prefers",null,"茶",-1,morning,[other]),known);
            var disputed=facts.Query(access,space,new("user","prefers",morning,KnowledgeTimeMode.Historical,known,known));
            Check("unexplained_conflict_kept_disputed",disputed.Count==2 && disputed.All(f=>f.Status=="disputed"));
            var likes=Source("parallel-likes","我喜欢咖啡，也喜欢温水。");
            facts.Put(access,space,new("likes-coffee","user","prefers",null,"咖啡",1,new Dictionary<string,string>(),[likes]),known);
            facts.Put(access,space,new("likes-water","user","prefers",null,"温水",1,new Dictionary<string,string>(),[likes]),known);
            var parallel=facts.Query(access,space,new("user","prefers",new Dictionary<string,string>(),KnowledgeTimeMode.Historical,known,known));
            Check("multiple_likes_are_not_conflicts",parallel.Count(f=>f.Id.StartsWith("likes-",StringComparison.Ordinal))==2 && parallel.Where(f=>f.Id.StartsWith("likes-",StringComparison.Ordinal)).All(f=>f.Status=="accepted"));
            var exclusiveSource=Source("exclusive-source","我晚上只喝茶。");
            var exclusiveEvening=new Dictionary<string,string>(evening){{"exclusive","true"}};
            facts.Put(access,space,new("exclusive-tea","user","drinks",null,"茶",1,exclusiveEvening,[exclusiveSource]),known);
            facts.Put(access,space,new("nonexclusive-water","user","drinks",null,"温水",1,evening,[water]),known.AddMilliseconds(1));
            var exclusiveConflict=facts.Query(access,space,new("user","drinks",evening,KnowledgeTimeMode.Historical,known.AddSeconds(1),known.AddSeconds(1)));
            Check("exclusive_conflict_and_subset_query",exclusiveConflict.Count==2 && exclusiveConflict.All(f=>f.Status=="disputed"));
            var catalog=new KnowledgeStrategyCatalog([new SqlLexicalStrategy(evidence),new SqlFactsStrategy(facts)]);
            var request=new KnowledgeStrategyRequest(new("晚上喝什么",KnowledgeTimeMode.Historical,known,known),space,new("user","prefers",evening));
            var factual=catalog.Demand(KnowledgeStrategy.SqlFacts).RetrieveAsync(access,request,new RecallBudget(),CancellationToken.None).GetAwaiter().GetResult();
            Check("sql_facts_named_strategy",factual.Facts.Single().Literal=="温水" && factual.Candidates.Single().AssertionIds!.Contains("evening-water"));
            var shared=new RecallBudget(maxQueries:1);
            catalog.Demand(KnowledgeStrategy.SqlLexical).RetrieveAsync(access,new(new("温水"),space),shared,CancellationToken.None).GetAwaiter().GetResult();
            var exhausted=catalog.Demand(KnowledgeStrategy.SqlFacts).RetrieveAsync(access,request,shared,CancellationToken.None).GetAwaiter().GetResult();
            Check("strategies_share_query_budget",exhausted.Candidates.Count==0 && exhausted.Gaps.Contains("query-budget-exhausted"));
            var unavailable=false;try {catalog.Demand(KnowledgeStrategy.GraphExpand);}catch(InvalidOperationException){unavailable=true;}
            Check("unavailable_graph_not_mislabeled",unavailable);
            db.Write((c,t)=>{using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="UPDATE evidence_units SET deleted_at=1,text='' WHERE evidence_id='water'";return cmd.ExecuteNonQuery();});
            Check("withdrawn_source_invalidates_fact",facts.Query(access,space,new("user","prefers",evening,KnowledgeTimeMode.Historical,known,known)).Count==0);
            Check("current_mode_excludes_retired",facts.Query(access,space,new("user")).All(f=>f.Id!="evening-tea"));
        }
        finally {Directory.Delete(root,true);}
        return results;
    }
}
