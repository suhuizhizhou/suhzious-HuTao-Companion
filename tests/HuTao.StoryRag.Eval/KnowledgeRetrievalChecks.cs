using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;

internal static class KnowledgeRetrievalChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var results=new List<CheckResult>();void Check(string name,bool ok)=>results.Add(new("retrieval_"+name,ok,"SQL evidence bundle"));
        var root=Path.Combine(Path.GetTempPath(),"hutao-retrieval-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var db=new SqliteKnowledgeDatabase(Path.Combine(root,"test.db"));
            var access=KnowledgeAccessContext.ForSession("retrieval","hutao","desktop",[]);var space=access.MemoryNamespace;
            var evidence=new SqlEvidenceRepository(db);var facts=new SqlAssertionRepository(db);
            facts.RegisterEntity(access,space,"user","person","用户");
            var first="我晚上只喝温水。";var second="咖啡会让我心悸，晚上尤其明显。";
            var a=evidence.Put(space,new("first",first,"user","first","turn"));
            var b=evidence.Put(space,new("second",second,"user","second","turn"));
            facts.Put(access,space,new("water","user","drinks",null,"温水",1,new Dictionary<string,string>{{"time_of_day","evening"}},
                [new(a,0,first.Length,first),new(b,0,second.Length,second)]),DateTimeOffset.UtcNow);
            var sqlFacts=new SqlFactsStrategy(facts);var lexical=new SqlLexicalStrategy(evidence);
            var retriever=new KnowledgeRetriever(new([sqlFacts,lexical]),evidence);
            var request=new KnowledgeStrategyRequest(new("晚上喝温水"),space,new("user","drinks"));
            KnowledgeRetrievalStep[] steps=[new("drink",KnowledgeStrategy.SqlLexical,request),new("drink",KnowledgeStrategy.SqlFacts,request)];
            var bundle=await retriever.RetrieveAsync(access,steps,new());
            Check("one_task_combines_strategies",bundle.Tasks!.Count==1 && bundle.Evidence.Count==2 && bundle.Tasks[0].Paths.Single().Facts.Single().Id=="water");
            Check("evidence_exact_sql_source_and_revision",bundle.Evidence.Any(x=>x.Reference==a && x.Text==first) && bundle.Evidence.Any(x=>x.Reference==b && x.Text==second));
            Check("bundle_has_backend_and_fusion_trace",bundle.Trace.Any(t=>t.Contains("SqlFacts")) && bundle.Trace.Any(t=>t.Contains("SqlLexical")) && bundle.Trace.Contains("fusion="+CandidateFusion.Version));
            Check("bundle_current_before_change",retriever.IsCurrent(access,bundle));
            var fused=CandidateFusion.Fuse([new(a,"graph-expand",1),new(a,"graph-expand",1),new(b,"sql-lexical",1),new(b,"graph-expand",1)]);
            Check("rrf_duplicate_paths_cannot_boost",fused[0].Reference==b && Math.Abs(fused.Single(x=>x.Reference==a).Score-1d/61)<1e-10);
            KnowledgeRetrievalStep[] factOnly=[new("drink",KnowledgeStrategy.SqlFacts,request)];
            var emptyFacts=await retriever.RetrieveAsync(access,[new("empty",KnowledgeStrategy.SqlFacts,request with {Facts=new("user","prefers")})],new(maxQueries:1));
            Check("empty_candidates_do_not_spend_hydration_query",emptyFacts.Evidence.Count==0 && emptyFacts.Gaps.Contains("no-supported-facts") && !emptyFacts.Gaps.Contains("sql-hydration-budget-exhausted"));
            using(var blocker=db.Open())
            {
                using var lockCommand=blocker.CreateCommand();
                lockCommand.CommandText="PRAGMA journal_mode=DELETE";lockCommand.ExecuteNonQuery();
                var lockTaken=false;
                try
                {
                    var delayed=new KnowledgeRetriever(new([new HookStrategy(sqlFacts,()=>
                    {
                        Thread.Sleep(130);
                        lockCommand.CommandText="BEGIN EXCLUSIVE";lockCommand.ExecuteNonQuery();lockTaken=true;
                    })]),evidence);
                    var watch=System.Diagnostics.Stopwatch.StartNew();
                    var timed=await delayed.RetrieveAsync(access,factOnly,new(duration:TimeSpan.FromMilliseconds(200)));
                    results.Add(new("retrieval_hydration_and_final_stamp_share_deadline",watch.Elapsed<TimeSpan.FromMilliseconds(350),$"elapsed_ms={watch.Elapsed.TotalMilliseconds:F1}"));
                    Check("deadline_cannot_publish_unhydrated_fact",timed.Evidence.Count==0 && timed.Gaps.Any(g=>g is "sql-hydration-deadline" or "sql-hydration-budget-exhausted"));
                }
                finally
                {
                    if(lockTaken){lockCommand.CommandText="ROLLBACK";lockCommand.ExecuteNonQuery();}
                    lockCommand.CommandText="PRAGMA journal_mode=WAL";lockCommand.ExecuteNonQuery();
                }
            }
            var limited=await retriever.RetrieveAsync(access,factOnly,new(maxCharacters:first.Length));
            Check("multi_source_fact_is_atomic_under_budget",limited.Evidence.Count==0 && limited.Tasks![0].Paths.Count==0 && limited.Gaps.Contains("incomplete-path-omitted"));
            var exhausted=await retriever.RetrieveAsync(access,steps,new(maxQueries:2));
            Check("hydration_cannot_reset_shared_budget",exhausted.Evidence.Count==0 && exhausted.Gaps.Contains("sql-hydration-budget-exhausted"));
            var expired=await retriever.RetrieveAsync(access,factOnly,new(duration:TimeSpan.Zero));
            Check("expired_time_budget_returns_gap",expired.Evidence.Count==0 && expired.Gaps.Contains("query-budget-exhausted"));
            using(var cancelled=new CancellationTokenSource())
            {
                cancelled.Cancel();var thrown=false;
                try {await retriever.RetrieveAsync(access,steps,new(),cancelled.Token);}catch(OperationCanceledException){thrown=true;}
                Check("cancellation_propagates",thrown);
            }
            var denied=false;
            try {await retriever.RetrieveAsync(access,[new("other",KnowledgeStrategy.SqlLexical,request with {Namespace="memory:other:hutao:desktop"})],new());}
            catch(UnauthorizedAccessException){denied=true;}
            Check("host_scope_checked_before_query",denied);
            var epochChange=new HookStrategy(sqlFacts,()=>
            {
                using var c=db.Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE namespaces SET privacy_epoch=privacy_epoch+1 WHERE namespace_id=@n";
                cmd.Parameters.AddWithValue("@n",space);cmd.ExecuteNonQuery();
            });
            var privateRetriever=new KnowledgeRetriever(new([epochChange]),evidence);
            var invalidated=await privateRetriever.RetrieveAsync(access,factOnly,new());
            Check("privacy_change_discards_whole_bundle",invalidated.Evidence.Count==0 && invalidated.Tasks!.All(t=>t.Paths.Count==0) && invalidated.Gaps.Contains("knowledge-changed-during-retrieval"));
            Check("send_time_epoch_guard",!retriever.IsCurrent(access,bundle));
            var staleRetriever=new KnowledgeRetriever(new([new HookStrategy(sqlFacts,()=>evidence.Put(space,new("first","现在晚上改喝别的。","user","first","turn")))]),evidence);
            var stale=await staleRetriever.RetrieveAsync(access,factOnly,new());
            Check("revision_change_discards_whole_bundle",stale.Evidence.Count==0 && stale.Gaps.Contains("knowledge-changed-during-retrieval"));
            Check("batch_read_rejects_stale_revision",evidence.ReadMany(access,[a],new("晚上")).Count==0);
            var unsupported=await retriever.RetrieveAsync(access,factOnly,new());
            Check("lost_source_fact_not_reintroduced",unsupported.Evidence.Count==0 && unsupported.Tasks![0].Paths.Count==0);
            var sceneAccess=KnowledgeAccessContext.ForSession("retrieval","hutao","desktop",["corpus:scene"]);
            const string scene="corpus:scene";
            EvidenceRef Scene(string id,int sequence,string meta,string chapter="hat",string version="v1") =>
                evidence.Put(scene,new(id,"梅花制作步骤"+id,"胡桃","story","character_story",chapter,sequence,MetadataJson:meta),version);
            const string metadata="{\"text_hash\":\"flower\",\"talk_id\":1,\"subquest_index\":1,\"source_page_id\":\"hutao\"}";
            var anchor=Scene("anchor",10,metadata);
            var adjacent=Scene("dry",11,metadata);
            Scene("far",14,metadata);
            Scene("chapter",11,metadata,"other");
            Scene("text",11,metadata.Replace("flower","other"));
            Scene("talk",11,metadata.Replace("\"talk_id\":1","\"talk_id\":2"));
            Scene("quest",11,metadata.Replace("\"subquest_index\":1","\"subquest_index\":2"));
            Scene("page",11,metadata.Replace("hutao","other"));
            Scene("future",11,metadata,version:"v2");
            using(var c=db.Open())using(var cmd=c.CreateCommand())
            {cmd.CommandText="UPDATE namespaces SET active_corpus_version='v1' WHERE namespace_id='corpus:scene'";cmd.ExecuteNonQuery();}
            var neighbors=evidence.Neighbors(sceneAccess,[anchor]);
            Check("scene_window_includes_adjacent_source",neighbors.Any(x=>x.Reference==adjacent));
            Check("scene_window_respects_all_boundaries",neighbors.Count==2 && neighbors.All(x=>x.Reference==anchor || x.Reference==adjacent));
            Check("scene_window_hydrates_sql_text",evidence.ReadMany(sceneAccess,neighbors.Select(x=>x.Reference).ToArray(),new("梅花")).Count==2);
            using(var c=db.Open())using(var cmd=c.CreateCommand())
            {cmd.CommandText="UPDATE namespaces SET active_corpus_version='v2' WHERE namespace_id='corpus:scene'";cmd.ExecuteNonQuery();}
            Check("scene_window_rejects_inactive_anchor",evidence.Neighbors(sceneAccess,[anchor]).Count==0);
            using(var c=db.Open())using(var cmd=c.CreateCommand()) {cmd.CommandText="ALTER TABLE namespace_state RENAME TO unavailable_state";cmd.ExecuteNonQuery();}
            var unavailable=await retriever.RetrieveAsync(access,steps,new());
            Check("sql_outage_explicit_unavailable",unavailable.Evidence.Count==0 && unavailable.Gaps.Contains("sql-unavailable") && !retriever.IsCurrent(access,bundle));
        }
        finally {Directory.Delete(root,true);}
        return results;
    }
    private sealed class HookStrategy(IKnowledgeRetrievalStrategy inner,Action after) : IKnowledgeRetrievalStrategy
    {
        public KnowledgeStrategy Name=>inner.Name;
        public async Task<KnowledgeStrategyResult> RetrieveAsync(KnowledgeAccessContext access,KnowledgeStrategyRequest request,RecallBudget budget,CancellationToken ct)
        {var result=await inner.RetrieveAsync(access,request,budget,ct);after();return result;}
    }
}
