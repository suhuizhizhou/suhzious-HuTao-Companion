using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using Neo4j.Driver;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Immersion;
using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;
using HuTao.Persona;

internal static class KnowledgeGraphChecks
{
    public static async Task<List<CheckResult>> RunAsync(string uri)
    {
        var results=new List<CheckResult>();void Check(string name,bool ok)=>results.Add(new("graph_"+name,ok,"real Neo4j"));
        var root=Path.Combine(Path.GetTempPath(),"hutao-graph-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var access=KnowledgeAccessContext.ForSession("graph-"+Guid.NewGuid().ToString("N"),"hutao","desktop",[]);var space=access.MemoryNamespace;
        await using var driver=GraphDatabase.Driver(uri,AuthTokens.None,config=>config.WithMaxTransactionRetryTime(TimeSpan.Zero));
        async Task<long> Number(string query,object? parameters=null)
        {
            await using var session=driver.AsyncSession();var cursor=await session.RunAsync(query,parameters??new{ns=space});
            if(!await cursor.FetchAsync())throw new InvalidDataException("Graph check returned no row.");
            return Convert.ToInt64(cursor.Current["value"]);
        }
        try
        {
            await driver.VerifyConnectivityAsync();
            var projection=new Neo4jGraphProjection(driver);await projection.InitializeAsync();
            var db=new SqliteKnowledgeDatabase(Path.Combine(root,"test.db"));var facts=new SqlAssertionRepository(db);var evidence=new SqlEvidenceRepository(db);
            facts.RegisterEntity(access,space,"hutao","character","胡桃");facts.RegisterEntity(access,space,"lion","object","石狮子");facts.RegisterEntity(access,space,"crane","object","纸鹤");
            var text="胡桃照顾石狮子。private-body-marker";
            var reference=evidence.Put(space,new("source",text,"user","source","turn"));
            var source=new AssertionSource(reference,0,text.Length,text);
            facts.Put(access,space,new("care","hutao","cares_for","lion",null,1,new Dictionary<string,string>(),[source]),DateTimeOffset.UtcNow);
            var outbox=new GraphOutboxRepository(db);var worker=new GraphProjectionWorker(db,projection);var now=DateTimeOffset.UtcNow;
            var original=outbox.Claim(access,space,now,TimeSpan.FromSeconds(1))!;var snapshot=worker.LoadLatest(access,original);
            await projection.ApplyAsync(original,snapshot,CancellationToken.None);
            Check("real_nodes_and_relationships",await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,id:'care'})-[:SUBJECT]->(s:HuTaoEntity {id:'hutao'}), (a)-[:OBJECT]->(o:HuTaoEntity {id:'lion'}) RETURN count(a) AS value")==1);
            using(var c=db.Open())using(var cmd=c.CreateCommand())
            {cmd.CommandText="SELECT graph_checkpoint FROM namespace_state WHERE namespace_id=@n";cmd.Parameters.AddWithValue("@n",space);Check("write_before_ack_not_committed_in_sql",Convert.ToInt64(cmd.ExecuteScalar())==0);}
            var replay=outbox.Claim(access,space,now.AddSeconds(2))!;
            await projection.ApplyAsync(replay,worker.LoadLatest(access,replay),CancellationToken.None);
            Check("crash_replay_one_assertion",await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,id:'care'}) RETURN count(a) AS value")==1);
            Check("crash_replay_one_receipt",await Number("MATCH (a:HuTaoApplied {namespace_id:$ns}) RETURN count(a) AS value")==1);
            Check("ack_after_graph_commit",outbox.Acknowledge(access,replay,now.AddSeconds(3)));
            Check("private_body_not_projected",await Number("MATCH (n) WHERE n.namespace_id=$ns AND any(k IN keys(n) WHERE toString(n[k]) CONTAINS 'private-body-marker') RETURN count(n) AS value")==0);
            var updatedText="胡桃现在照顾纸鹤。";var updatedRef=evidence.Put(space,new("source2",updatedText,"user","source2","turn"));
            facts.Put(access,space,new("care2","hutao","cares_for","crane",null,1,new Dictionary<string,string>(),[new(updatedRef,0,updatedText.Length,updatedText)]),DateTimeOffset.UtcNow,"care");
            Check("worker_projects_correction",await worker.PublishOneAsync(access,space));
            Check("old_assertion_marked_superseded",await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,id:'care',status:'superseded',revision:2}) RETURN count(a) AS value")==1);
            await projection.ApplyAsync(original,snapshot,CancellationToken.None);
            Check("old_upsert_cannot_revert_revision",await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,id:'care',status:'superseded',revision:2}) RETURN count(a) AS value")==1);
            await using(var unavailable=GraphDatabase.Driver("bolt://127.0.0.1:1",AuthTokens.None,c=>c.WithConnectionTimeout(TimeSpan.FromMilliseconds(300)).WithMaxTransactionRetryTime(TimeSpan.Zero)))
            {
                var failed=false;
                try {using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));await new GraphProjectionWorker(db,new Neo4jGraphProjection(unavailable)).PublishOneAsync(access,space,deadline.Token);}
                catch(Exception ex) when(ex is Neo4jException or OperationCanceledException){failed=true;}
                Check("real_graph_disconnect_preserves_sql",failed && facts.Query(access,space,new("hutao","cares_for")).Single().Id=="care2");
            }
            var recovered=outbox.Claim(access,space,DateTimeOffset.UtcNow.AddSeconds(10))!;
            await projection.ApplyAsync(recovered,worker.LoadLatest(access,recovered),CancellationToken.None);
            Check("disconnect_event_recovered",outbox.Acknowledge(access,recovered,DateTimeOffset.UtcNow.AddSeconds(11)) && await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,id:'care2',status:'accepted'}) RETURN count(a) AS value")==1);
            string ActiveGeneration()
            {
                using var connection=db.Open();using var command=connection.CreateCommand();
                command.CommandText="SELECT graph_generation FROM namespace_state WHERE namespace_id=@n";command.Parameters.AddWithValue("@n",space);
                return (string)command.ExecuteScalar()!;
            }
            var rebuilt=await new GraphRebuilder(db,projection).RebuildAsync(access,space);
            Check("rebuild_activates_validated_generation",rebuilt.Activated && rebuilt.Assertions==2 && ActiveGeneration()==rebuilt.Generation);
            Check("rebuild_preserves_old_generation",await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,id:'care2'}) RETURN count(a) AS value")==2);
            var changed=false;
            GraphOutboxEvent? retiredLease=null;
            var changing=new BeforeValidationProjection(projection,(_,_)=>
            {
                if(!changed)
                {
                    changed=true;
                    facts.Put(access,space,new("care3","hutao","cares_for","lion",null,1,new Dictionary<string,string>(),[source]),DateTimeOffset.UtcNow);
                    retiredLease=outbox.Claim(access,space,DateTimeOffset.UtcNow);
                }
                return Task.CompletedTask;
            });
            var raced=await new GraphRebuilder(db,changing).RebuildAsync(access,space);
            Check("rebuild_catches_up_concurrent_sql_write",raced.Activated && raced.Assertions==3 && ActiveGeneration()==raced.Generation &&
                await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,generation:$g,id:'care3'}) RETURN count(a) AS value",new{ns=space,g=raced.Generation})==1);
            Check("rebuild_retires_old_outbox_lease",retiredLease is not null && !outbox.Acknowledge(access,retiredLease,DateTimeOffset.UtcNow));
            rebuilt=raced;
            facts.Put(access,space,new("after-rebuild","hutao","made","lion",null,1,new Dictionary<string,string>(),[source]),DateTimeOffset.UtcNow);
            Check("new_events_target_active_generation",outbox.Claim(access,space,DateTimeOffset.UtcNow)!.Generation==rebuilt.Generation);
            var forgetting=new BeforeValidationProjection(projection,(_,_)=>
            {
                using var connection=db.Open();using var command=connection.CreateCommand();
                command.CommandText="UPDATE namespaces SET privacy_epoch=privacy_epoch+1 WHERE namespace_id=@n";command.Parameters.AddWithValue("@n",space);command.ExecuteNonQuery();
                return Task.CompletedTask;
            });
            var forgotten=await new GraphRebuilder(db,forgetting).RebuildAsync(access,space);
            Check("rebuild_privacy_epoch_prevents_activation",!forgotten.Activated && ActiveGeneration()==rebuilt.Generation);
            var corrupting=new BeforeValidationProjection(projection,async(ns,g)=>
            {
                await using var session=driver.AsyncSession();
                var cursor=await session.RunAsync("MATCH (a:HuTaoAssertion {namespace_id:$ns,generation:$g,id:'care2'})- [r:OBJECT]->() DELETE r",new{ns,g});await cursor.ConsumeAsync();
            });
            var invalid=await new GraphRebuilder(db,corrupting).RebuildAsync(access,space);
            Check("rebuild_missing_relationship_prevents_activation",!invalid.Activated && invalid.Reason=="graph-validation-failed" && ActiveGeneration()==rebuilt.Generation);
            using(var cancelled=new CancellationTokenSource())
            {
                cancelled.Cancel();var stopped=false;
                try {await new GraphRebuilder(db,projection).RebuildAsync(access,space,cancelled.Token);}catch(OperationCanceledException){stopped=true;}
                Check("cancelled_rebuild_keeps_active_generation",stopped && ActiveGeneration()==rebuilt.Generation);
            }
            var crashProjection=new CrashAfterWriteProjection(projection);
            var crashRebuilder=new GraphRebuilder(db,crashProjection);
            var prepared=crashRebuilder.Prepare(access,space);var crashed=false;
            try {await crashRebuilder.ResumeAsync(access,space,prepared);}catch(IOException){crashed=true;}
            Check("rebuild_graph_commit_before_sql_progress_crash",crashed && ActiveGeneration()==rebuilt.Generation &&
                await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,generation:$g}) RETURN count(a) AS value",new{ns=space,g=prepared})==1);
            var reopened=new SqliteKnowledgeDatabase(db.FilePath);
            var resumed=await new GraphRebuilder(reopened,projection).ResumeAsync(access,space,prepared);
            Check("rebuild_resumes_persisted_snapshot",resumed.Activated && resumed.Generation==prepared && resumed.Assertions==4);
            var replayedJob=await new GraphRebuilder(reopened,projection).ResumeAsync(access,space,prepared);
            Check("rebuild_completed_resume_is_idempotent",replayedJob.Activated && replayedJob.Reason=="already-active");
            Check("rebuild_replay_idempotent_receipts",await Number("MATCH (a:HuTaoApplied {namespace_id:$ns,generation:$g}) RETURN count(a) AS value",new{ns=space,g=prepared})==4);
            var generations=new GraphRebuilder(db,projection);
            var competing=generations.Prepare(access,space);
            var winning=await generations.RebuildAsync(access,space);
            var losing=await generations.ResumeAsync(access,space,competing);
            Check("competing_rebuild_cannot_replace_new_generation",winning.Activated && !losing.Activated && losing.Reason=="source-generation-or-privacy-changed" && ActiveGeneration()==winning.Generation);
            var busyProjection=new BeforeValidationProjection(projection,async(ns,g)=>
            {
                var busy=await generations.ResumeAsync(access,ns,g);
                Check("rebuild_lease_excludes_second_worker",!busy.Activated && busy.Reason=="job-running");
            });
            Check("rebuild_owner_continues_after_busy_claim",(await new GraphRebuilder(db,busyProjection).RebuildAsync(access,space)).Activated);
            var expired=generations.Prepare(access,space);
            using(var c=db.Open())using(var command=c.CreateCommand())
            {
                command.CommandText="UPDATE graph_rebuild_jobs SET status='running',lease_token='dead-worker',lease_until=0 WHERE namespace_id=@n AND generation=@g";
                command.Parameters.AddWithValue("@n",space);command.Parameters.AddWithValue("@g",expired);command.ExecuteNonQuery();
            }
            Check("rebuild_expired_lease_reclaimed",(await generations.ResumeAsync(access,space,expired)).Activated);
            var writes=0;
            var churnProjection=new BeforeValidationProjection(projection,(_,_)=>
            {
                evidence.Put(space,new("concurrent-turn-"+(++writes),"合成并发轮次","user","synthetic-churn","turn"));return Task.CompletedTask;
            });
            var churning=await new GraphRebuilder(db,churnProjection).RebuildAsync(access,space);
            Check("rebuild_catchup_rounds_bounded",!churning.Activated && writes==3 && churning.Reason=="catchup-budget-exhausted");
            Check("rebuild_churn_resume_without_new_generation",(await generations.ResumeAsync(access,space,churning.Generation)).Activated);
            facts.Put(access,space,new("part","crane","has_part","lion",null,1,new Dictionary<string,string>(),[source]),DateTimeOffset.UtcNow);
            facts.Put(access,space,new("negative","hutao","made","crane",null,-1,new Dictionary<string,string>(),[source]),DateTimeOffset.UtcNow);
            var queryCatalog=new GraphQueryCatalog(db,driver);
            var lag=await queryCatalog.ExpandAsync(access,space,["hutao"],["cares_for","has_part"],new());
            Check("query_lag_returns_explicit_gap",lag.Paths.Count==0 && lag.Gaps.Contains("graph-behind-sql"));
            var current=await new GraphRebuilder(db,projection).RebuildAsync(access,space);
            var paths=await queryCatalog.ExpandAsync(access,space,["hutao"],["cares_for","has_part"],new());
            Check("real_two_semantic_hops",current.Activated && paths.Paths.Any(p=>p.Edges.Count==2 && p.Edges[0].AssertionId=="care2" && p.Edges[1].AssertionId=="part"));
            evidence.Put(space,new("unrelated-chat","今天路上看到一朵花。","user","unrelated-chat","turn"));
            var unchangedGraph=await queryCatalog.ExpandAsync(access,space,["hutao"],["cares_for","has_part"],new());
            Check("raw_chat_without_graph_mutation_does_not_disable_graph",unchangedGraph.Paths.Any(p=>p.Edges.Count==2) && !unchangedGraph.Gaps.Contains("graph-behind-sql"));
            Check("query_excludes_superseded",paths.Paths.All(p=>p.Edges.All(e=>e.AssertionId!="care")));
            var one=await queryCatalog.ExpandAsync(access,space,["hutao"],["cares_for","has_part"],new(),1);
            Check("one_hop_template",one.Paths.Count>0 && one.Paths.All(p=>p.Edges.Count==1));
            var negative=await queryCatalog.ExpandAsync(access,space,["hutao"],["made"],new());
            Check("negative_not_positive_path",negative.Paths.Count>0 && negative.Paths.All(p=>p.Edges.All(e=>e.AssertionId!="negative")));
            var reverse=await queryCatalog.ExpandAsync(access,space,["lion"],["has_part"],new());
            Check("query_respects_direction",reverse.Paths.Count==0);
            var exhausted=await queryCatalog.ExpandAsync(access,space,["hutao"],["cares_for"],new(maxQueries:0));
            Check("query_shared_budget",exhausted.Paths.Count==0 && exhausted.Gaps.Contains("query-budget-exhausted"));
            await using(var session=driver.AsyncSession())
            {
                var cursor=await session.RunAsync("MATCH (a:HuTaoAssertion {namespace_id:$ns,generation:$g,id:'part'})-[:OBJECT]->(o) SET o.namespace_id=$foreign",new{ns=space,g=current.Generation,foreign=space+"-foreign"});await cursor.ConsumeAsync();
            }
            try
            {
                var foreign=await queryCatalog.ExpandAsync(access,space,["hutao"],["cares_for","has_part"],new());
                Check("cross_namespace_endpoint_rejected",foreign.Paths.All(p=>p.Edges.All(e=>e.AssertionId!="part")));
            }
            finally
            {
                await using var session=driver.AsyncSession();
                var cursor=await session.RunAsync("MATCH (o:HuTaoEntity {namespace_id:$foreign,generation:$g}) SET o.namespace_id=$ns",new{ns=space,g=current.Generation,foreign=space+"-foreign"});await cursor.ConsumeAsync();
            }
            var strategy=new GraphExpandStrategy(queryCatalog,facts);
            var request=new KnowledgeStrategyRequest(new("胡桃照顾的东西有什么组成部分"),space,new("hutao","cares_for"),["hutao"],["cares_for","has_part"]);
            var hydrated=await strategy.RetrieveAsync(access,request,new(),CancellationToken.None);
            Check("graph_paths_hydrated_from_sql",hydrated.Paths!.Any(p=>p.Facts.Count==2) && hydrated.Facts.All(f=>f.Sources.Count>0));
            var hybridRetriever=new KnowledgeRetriever(new([strategy,new SqlLexicalStrategy(evidence)]),evidence);
            KnowledgeRetrievalStep[] hybridSteps=[new("relation",KnowledgeStrategy.GraphExpand,request),new("relation",KnowledgeStrategy.SqlLexical,request)];
            var graphBundle=await hybridRetriever.RetrieveAsync(access,hybridSteps,new());
            Check("real_graph_and_sql_unified_bundle",graphBundle.Tasks!.Single().Paths.Any(p=>p.Facts.Count==2) && graphBundle.Evidence.All(e=>evidence.Read(access,e.Reference)?.Text==e.Text) && hybridRetriever.IsCurrent(access,graphBundle));
            var agentCatalog=new KnowledgeStrategyCatalog([strategy,new SqlLexicalStrategy(evidence)]);
            var agentContext=new KnowledgeConversationContext(access,new(agentCatalog,evidence),new(db),new Dictionary<string,string>{{"memory",space}},
                new Dictionary<string,ConversationParticipants>(),agentCatalog.Available);
            var persona=new PersonaProfile{Root=root,Name="胡桃",SystemPrompt="胡桃",Catchphrases=new(),Lore="",Quotes=[]};
            var graphAgent=new ReactAgent(persona,new GraphAgentLlm(),null,[],knowledge:agentContext,
                immersionOptions:new ImmersionOptions{EnableCritic=false,EnableRuleGate=false},diagnostics:new LocalDiagnosticLog(Path.Combine(root,"agent.log")));
            var graphTurn=await graphAgent.GenerateTextAsync("胡桃照顾的东西还有什么组成部分？",false);
            Check("real_graph_sql_agent_answer_chain",graphTurn.Recall?.Knowledge?.Tasks!.Any(t=>t.Paths.Any(p=>p.Facts.Count==2))==true &&
                graphTurn.Recall.Knowledge.Trace.Any(t=>t.Contains("backend=neo4j+sqlite")) && graphTurn.StoryAnswer?.Validated==true && graphTurn.Reply.Contains("照顾纸鹤"));
            var shortBundle=await hybridRetriever.RetrieveAsync(access,[hybridSteps[0]],new(maxCharacters:updatedText.Length));
            Check("graph_bundle_does_not_claim_truncated_path",shortBundle.Tasks!.All(t=>t.Paths.All(p=>p.Facts.Count<2)) && shortBundle.Gaps.Contains("incomplete-path-omitted"));
            await using(var session=driver.AsyncSession())
            {
                var cursor=await session.RunAsync("MATCH (a:HuTaoAssertion {namespace_id:$ns,generation:$g,id:'part'}) SET a.revision=999",new{ns=space,g=current.Generation});await cursor.ConsumeAsync();
            }
            var stale=await strategy.RetrieveAsync(access,request,new(),CancellationToken.None);
            Check("stale_edge_discards_entire_path",stale.Paths!.All(p=>p.Facts.All(f=>f.Id!="part")) && stale.Gaps.Contains("graph-path-failed-sql-validation"));
            await using(var unavailable=GraphDatabase.Driver("bolt://127.0.0.1:1",AuthTokens.None,c=>c.WithConnectionTimeout(TimeSpan.FromMilliseconds(300)).WithMaxTransactionRetryTime(TimeSpan.Zero)))
            {
                var fallback=await new GraphExpandStrategy(new(db,unavailable),facts).RetrieveAsync(access,request,new(),CancellationToken.None);
                Check("real_graph_failure_sql_fallback",fallback.Facts.Count>0 && fallback.Trace.Contains("requested=GraphExpand;fallback=SqlFacts") && fallback.Gaps.Contains("graph-unavailable"));
            }
            // Adversarial graph-only shapes: these test candidate bounds, never authoritative facts.
            await using(var session=driver.AsyncSession())
            {
                var cursor=await session.RunAsync("""
                    MATCH (a:HuTaoAssertion {namespace_id:$ns,generation:$g,id:'care3'}) SET a.status='disputed'
                    WITH a
                    MATCH (s:HuTaoEntity {namespace_id:$ns,generation:$g,id:'lion'}),(o:HuTaoEntity {namespace_id:$ns,generation:$g,id:'crane'})
                    CREATE (back:HuTaoAssertion {namespace_id:$ns,generation:$g,id:'cycle',revision:1,status:'accepted',polarity:1,predicate_id:'has_part'})
                    CREATE (back)-[:SUBJECT]->(s),(back)-[:OBJECT]->(o)
                    """,new{ns=space,g=current.Generation});await cursor.ConsumeAsync();
            }
            var disputed=await queryCatalog.ExpandAsync(access,space,["hutao"],["cares_for"],new());
            Check("disputed_graph_edge_not_traversed",disputed.Paths.All(p=>p.Edges.All(e=>e.AssertionId!="care3")));
            var cycle=await queryCatalog.ExpandAsync(access,space,["crane"],["has_part"],new());
            Check("cycle_does_not_return_to_seed",cycle.Paths.Count==1 && cycle.Paths[0].Edges.Count==1);
            await using(var session=driver.AsyncSession())
            {
                var cursor=await session.RunAsync("""
                    MERGE (s:HuTaoEntity {namespace_id:$ns,generation:$g,id:'dense-root'}) SET s.type='object'
                    WITH s UNWIND range(0,249) AS i
                    CREATE (o:HuTaoEntity {namespace_id:$ns,generation:$g,id:'dense-object-'+toString(i),type:'object'})
                    CREATE (a:HuTaoAssertion {namespace_id:$ns,generation:$g,id:'dense-edge-'+toString(i),revision:1,status:'accepted',polarity:1,predicate_id:'has_part'})
                    CREATE (a)-[:SUBJECT]->(s),(a)-[:OBJECT]->(o)
                    """,new{ns=space,g=current.Generation});await cursor.ConsumeAsync();
            }
            var dense=await queryCatalog.ExpandAsync(access,space,["dense-root"],["has_part"],new());
            Check("high_degree_hard_path_and_hop_caps",dense.Paths.Count==40 && dense.Gaps.Contains("graph-hop-expansion-limit") && dense.Gaps.Contains("graph-expansion-limit"));
            var outside=KnowledgeAccessContext.ForSession("different-user","hutao","desktop",[]);var denied=false;
            try {await queryCatalog.ExpandAsync(outside,space,["hutao"],["cares_for"],new());}catch(UnauthorizedAccessException){denied=true;}
            Check("graph_query_scope_denied_before_io",denied);
            var deletionResult=new KnowledgeDeletionService(db).Forget(access,space,[reference.Id]);
            Check("forget_sql_hides_before_graph_delivery",deletionResult.AssertionsRetracted>0 && facts.Query(access,space,new("hutao","cares_for")).All(f=>f.Sources.All(s=>s.Reference.Id!=reference.Id)));
            Check("forget_graph_may_still_be_pending",await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,id:'care'}) RETURN count(a) AS value")>0);
            // Even a leased pre-deletion event must load the current SQL tombstone.
            var tombstone=worker.LoadLatest(access,original);
            Check("old_event_loads_forgotten_snapshot",tombstone.Status=="forgotten" && tombstone.Revision>snapshot.Revision);
            await projection.ApplyAsync(original,tombstone,CancellationToken.None);
            Check("forget_clears_all_graph_generations",await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,id:'care'}) RETURN count(a) AS value")==0);
            await projection.ApplyAsync(original,snapshot,CancellationToken.None);
            await projection.ApplyAsync(original with {Generation="stale-after-forget"},snapshot,CancellationToken.None);
            Check("forget_fences_old_upsert_and_stale_generation",await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,id:'care'}) RETURN count(a) AS value")==0 &&
                await Number("MATCH (f:HuTaoForgotten {namespace_id:$ns,id:'care'}) RETURN count(f) AS value")==1);
            var afterForget=await new GraphRebuilder(db,projection).RebuildAsync(access,space);
            Check("forget_rebuild_validates_without_resurrection",afterForget.Activated && await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,id:'care'}) RETURN count(a) AS value")==0);
            facts.RegisterEntity(access,space,"factory-target","object","纸灯");
            const string factoryText="胡桃照顾纸灯。";
            var factorySource=evidence.Put(space,new("factory-source",factoryText,"user","factory-source","turn"));
            facts.Put(access,space,new("factory-care","hutao","cares_for","factory-target",null,1,new Dictionary<string,string>(),[new(factorySource,0,factoryText.Length,factoryText)]),DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Path.Combine(root,"persona"));File.WriteAllText(Path.Combine(root,"persona","system-prompt.md"),"胡桃。只回答给出的资料。");
            var factoryDefinition=new CharacterDefinition("hutao","胡桃","","","persona","","","missing.json","你好","","忙",Tools:["document_reader"]);
            var factory=new HuTao.Dialogue.Runtime.AgentRuntimeFactory(root,knowledgeOptions:new(HuTao.Dialogue.Runtime.KnowledgeRuntimeMode.SqlGraph,db.FilePath,
                UserId:space.Split(':')[1],GraphUri:uri),llmFactory:_=>new RuntimeGraphLlm());
            var factoryRuntime=factory.Create(factoryDefinition);
            try
            {
                var deadline=DateTime.UtcNow.AddSeconds(12);
                while(DateTime.UtcNow<deadline && await Number("MATCH (a:HuTaoAssertion {namespace_id:$ns,id:'factory-care',status:'accepted'}) RETURN count(a) AS value")==0)
                    await Task.Delay(100);
                var factoryTurn=await ((ReactAgent)factoryRuntime.Agent).GenerateTextAsync("胡桃照顾纸灯吗？",false);
                Check("factory_real_worker_and_graph_agent",factoryRuntime.Knowledge!.GraphHealth=="ready" && factoryTurn.Recall?.Knowledge?.Trace.Any(t=>t.Contains("backend=neo4j"))==true && factoryTurn.Reply.Contains("纸灯"));
            }
            finally {await factoryRuntime.DisposeAsync();}
            Check("factory_owned_worker_stops_on_dispose",factoryRuntime.Knowledge!.WorkerStopped);
        }
        finally
        {
            await using var session=driver.AsyncSession();
            var cleanup=await session.RunAsync("MATCH (n) WHERE n.namespace_id=$ns DETACH DELETE n",new{ns=space});await cleanup.ConsumeAsync();
            Directory.Delete(root,true);
        }
        return results;
    }

    private sealed class BeforeValidationProjection(Neo4jGraphProjection real,Func<string,string,Task> before) : IGraphGenerationProjection
    {
        public Task ApplyAsync(GraphOutboxEvent item,GraphAssertionSnapshot snapshot,CancellationToken ct)=>real.ApplyAsync(item,snapshot,ct);
        public async Task<bool> ValidateAsync(string space,string generation,IReadOnlyList<GraphAssertionSnapshot> expected,CancellationToken ct=default)
        {
            await before(space,generation);
            return await real.ValidateAsync(space,generation,expected,ct);
        }
    }

    private sealed class CrashAfterWriteProjection(Neo4jGraphProjection real) : IGraphGenerationProjection
    {
        public async Task ApplyAsync(GraphOutboxEvent item,GraphAssertionSnapshot snapshot,CancellationToken ct)
        {await real.ApplyAsync(item,snapshot,ct);throw new IOException("Injected crash after real graph commit.");}
        public Task<bool> ValidateAsync(string space,string generation,IReadOnlyList<GraphAssertionSnapshot> expected,CancellationToken ct=default)=>real.ValidateAsync(space,generation,expected,ct);
    }
    private sealed class RuntimeGraphLlm : ILLMProvider
    {
        public string Name=>"real-graph-factory-fixture";
        public Task<string> CompleteAsync(string systemPrompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(systemPrompt.Contains("Conversation Recall Planner")?
                "{\"tasks\":[{\"id\":\"graph\",\"source\":\"memory\",\"strategy\":\"GraphExpand\",\"query\":\"胡桃 照顾 纸灯\",\"entity_bindings\":[{\"mention\":\"胡桃\"}],\"predicates\":[\"cares_for\"]}]}":
                "{\"answerability\":\"supported\",\"segments\":[{\"text\":\"胡桃照顾纸灯。\",\"kind\":\"quote\",\"evidence_ids\":[\"memory:factory-source\"]}]}");
        }
    }
    private sealed class GraphAgentLlm : ILLMProvider
    {
        public string Name=>"graph-agent-fixture";
        public Task<string> CompleteAsync(string prompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)=>Task.FromResult(prompt.Contains("Conversation Recall Planner")?
            "{\"tasks\":[{\"id\":\"g\",\"source\":\"memory\",\"strategy\":\"GraphExpand\",\"query\":\"胡桃 照顾 组成部分\",\"entity_bindings\":[{\"mention\":\"胡桃\"}],\"predicates\":[\"cares_for\",\"has_part\"]}]}":
            "{\"answerability\":\"partial\",\"segments\":[{\"text\":\"胡桃现在照顾纸鹤。\",\"kind\":\"quote\",\"evidence_ids\":[\"memory:source2\"]}]}");
    }
}
