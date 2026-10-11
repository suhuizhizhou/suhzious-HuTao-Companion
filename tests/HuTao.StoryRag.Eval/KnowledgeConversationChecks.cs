using System.Text.Json;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Immersion;
using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;
using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Import;
using HuTao.Knowledge.Rag;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using HuTao.Persona;

internal static class KnowledgeConversationChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var results=new List<CheckResult>();void Check(string name,bool ok)=>results.Add(new("knowledge_conversation_"+name,ok,"real SQL / ReactAgent; fixture LLM"));
        var root=Path.Combine(Path.GetTempPath(),"hutao-knowledge-agent-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var db=new SqliteKnowledgeDatabase(Path.Combine(root,"test.db"));var evidence=new SqlEvidenceRepository(db);var facts=new SqlAssertionRepository(db);
            var access=KnowledgeAccessContext.ForSession("conversation","hutao","desktop",["corpus:conversation-fixture"]);var space=access.MemoryNamespace;
            facts.RegisterEntity(access,space,"user","person","用户");facts.RegisterEntity(access,space,"hutao","character","胡桃");
            var original="我在给妹妹织围巾，晚上只喝温水。";
            var reference=evidence.Put(space,new("preference",original,"user","preference","turn"));
            facts.Put(access,space,new("drink","user","drinks",null,"温水",1,new Dictionary<string,string>{{"time_of_day","evening"}},[new(reference,0,original.Length,original)]),DateTimeOffset.UtcNow);
            var line=new StoryDialogueLine {ChapterId="hat",LineId=1,Speaker="胡桃",Text="帽子上的花是我亲手做的。",SourceFile="fixture.jsonl",Resolved=true};
            new StorySqlImporter(db).ImportLines("corpus:conversation-fixture",[line],"fixture-v1");
            var catalog=new KnowledgeStrategyCatalog([new SqlLexicalStrategy(evidence),new SqlFactsStrategy(facts)]);
            var retrieval=new KnowledgeRetriever(catalog,evidence);
            var context=new KnowledgeConversationContext(access,retrieval,new(db),new Dictionary<string,string>{{"memory",space},{"story","corpus:conversation-fixture"}},
                new Dictionary<string,ConversationParticipants>{{"memory",new("user","hutao")}},catalog.Available);
            var persona=new PersonaProfile {Root=root,Name="胡桃",SystemPrompt="胡桃",Catchphrases=new(),Lore="",Quotes=[]};
            var question=evidence.Put(space,new("current-question","我晚上喝什么温水？","user","current-question","turn"));
            KnowledgeConversationTask[] fallbackTasks=[new("f","memory",KnowledgeStrategy.SqlFacts,"温水",[],[],[new("我")],["prefers"])];
            var fallback=await ReactRetrievalLoop.RunKnowledgeAsync(context,fallbackTasks,new RecallBudget(),default,question.Id);
            Check("fact_miss_has_bounded_lexical_fallback",fallback.Bundle.Evidence.Any(e=>e.Reference==reference) && fallback.Bundle.Trace.Contains("fact-miss-bounded-lexical-fallback"));
            Check("current_user_original_excluded_from_lexical_evidence",fallback.Bundle.Evidence.All(e=>e.Reference!=question));
            var sharedBudget=new RecallBudget();
            var planned=await ReactRetrievalLoop.RunKnowledgeAsync(context,
                [fallbackTasks[0],new("valid","memory",KnowledgeStrategy.SqlFacts,"饮品",[],[],[new("我")],["drinks"])],sharedBudget,default,question.Id);
            Check("optional_fallback_does_not_starve_planned_fact_query",planned.Bundle.Evidence.Any(e=>e.Reference==reference) &&
                planned.Steps.Single(s=>s.TaskId=="valid").EvidenceIds.Contains(reference.Id) &&
                planned.Bundle.Trace.Contains("fact-miss-fallback-deferred-for-planned-tasks"));
            Check("planned_query_recovery_keeps_shared_budget",sharedBudget.QueriesRemaining==1 && !planned.Bundle.Gaps.Contains("query-budget-exhausted"));
            var selection=evidence.PersonalSelectionCandidates(access,[question.Id]);
            Check("semantic_candidates_include_nonlexical_original",selection.Any(e=>e.Reference==reference && e.Text==original) && evidence.Search(access,"礼物",onlyNamespace:space,excludedIds:[question.Id]).Count==0);
            Check("semantic_candidates_exclude_current_question_and_public",selection.All(e=>e.Reference.Namespace==space && e.Reference!=question));
            var prior=evidence.Put(space,new("selection-version","我正在学雕刻。","user","selection-version","turn"));
            var latest=evidence.Put(space,new("selection-version","我正在学素描。","user","selection-version","turn"));
            var assistant=evidence.Put(space,new("selection-assistant","你正在学陶器。","assistant","selection-assistant","turn"));
            var otherSpace=KnowledgeAccessContext.ForSession("other","hutao","desktop",[]).MemoryNamespace;
            evidence.Put(otherSpace,new("selection-other","别人的合成原文","user","selection-other","turn"));
            var latestSelection=evidence.PersonalSelectionCandidates(access);
            Check("semantic_candidates_latest_user_revision_only",latestSelection.Any(e=>e.Reference==latest) && latestSelection.All(e=>e.Reference!=prior && e.Reference!=assistant && e.Reference.Namespace==space));
            new KnowledgeDeletionService(db).Forget(access,space,[latest.Id,assistant.Id]);
            Check("semantic_candidates_honor_forgetting",evidence.PersonalSelectionCandidates(access).All(e=>e.Reference.Id!=latest.Id));
            Check("semantic_candidates_hard_limit",evidence.PersonalSelectionCandidates(access,limit:1).Count==1);
            var semanticCatalog=new KnowledgeStrategyCatalog([new SqlLexicalStrategy(evidence),new SqlFactsStrategy(facts),new SqlSemanticStrategy()]);
            var semanticRetriever=new KnowledgeRetriever(semanticCatalog,evidence);
            var prepared=semanticRetriever.PrepareSemanticPlanning(access,question.Id,default);
            Check("semantic_planning_candidate_budget",prepared.Queries==1 && prepared.Characters<=4000 && prepared.Items.Count<=32 && prepared.Items.All(e=>e.Reference!=question));
            KnowledgeRetrievalStep[] semanticSteps=[new("meaning",KnowledgeStrategy.SqlSemantic,new(new("礼物是给谁的？"),space,ExcludedLexicalIds:[question.Id],SemanticReferences:[reference]))];
            var semantic=await semanticRetriever.RetrieveAsync(access,semanticSteps,new());
            Check("semantic_selection_hydrates_exact_original",semantic.Evidence.Single().Reference==reference && semantic.Evidence.Single().Text==original);
            Check("semantic_execution_has_no_model_call",semantic.Trace.Any(t=>t.Contains("execution-model-calls=0")));
            var noBudget=await semanticRetriever.RetrieveAsync(access,semanticSteps,new(maxQueries:0));
            Check("semantic_selection_requires_shared_hydration_budget",noBudget.Evidence.Count==0 && noBudget.Gaps.Contains("sql-hydration-budget-exhausted"));
            var noCharacters=await semanticRetriever.RetrieveAsync(access,semanticSteps,new(maxCharacters:1));
            Check("semantic_selection_evidence_uses_character_budget",noCharacters.Evidence.Count==0 && noCharacters.Gaps.Contains("evidence-budget-exhausted"));
            var semanticContext=new KnowledgeConversationContext(access,semanticRetriever,new(db),context.Sources,context.Participants,semanticCatalog.Available);
            var storyOnlyContext=new KnowledgeConversationContext(access,semanticRetriever,new(db),new Dictionary<string,string>{{"story","corpus:conversation-fixture"}},new Dictionary<string,ConversationParticipants>(),semanticCatalog.Available);
            var storyOnlyPlanner=new SemanticPlannerLlm("{\"intent\":\"剧情\",\"tasks\":[]}");
            var storyOnlyRecall=await new ConversationRecall(storyOnlyPlanner,persona.EffectiveLexicon,[],storyOnlyContext).RunAsync("讲讲帽子上的花",[],DateTimeOffset.UtcNow,default);
            Check("story_only_planning_does_not_load_personal_candidates",!storyOnlyPlanner.Prompt.Contains(original) && storyOnlyRecall.Knowledge!.Trace.Contains("planning-candidates;queries=0;characters=0;execution-budget-ms=2000"));
            var semanticPlan="""
                {"intent":"礼物","tasks":[{"id":"m","source":"memory","strategy":"SqlSemantic","query":"礼物是给谁的","selected_ids":["preference"]}]}
                """;
            var planner=new SemanticPlannerLlm(semanticPlan);
            var comfortPlan=semanticPlan.Replace("\"intent\":\"礼物\"","\"intent\":\"陪伴\",\"response_purpose\":\"comfort\"");
            var comfortRecall=await new ConversationRecall(new SemanticPlannerLlm(comfortPlan),persona.EffectiveLexicon,[],semanticContext)
                .RunAsync("围巾又织错了，有点泄气",[],DateTimeOffset.UtcNow,default,question.Id);
            Check("comfort_purpose_preserves_retrieved_evidence",comfortRecall.ResponsePurpose==ConversationResponsePurpose.Comfort &&
                comfortRecall.Knowledge!.Evidence.Single().Reference==reference && comfortRecall.Story!.Primary.Status==StoryStatus.Tentative);
            var invalidPurpose=await new ConversationRecall(new SemanticPlannerLlm(comfortPlan.Replace("\"comfort\"","\"skip-review\"")),persona.EffectiveLexicon,[],semanticContext)
                .RunAsync("围巾又织错了，有点泄气",[],DateTimeOffset.UtcNow,default,question.Id);
            Check("unknown_response_purpose_rejected",invalidPurpose.Knowledge!.Trace.Contains("planner-failure=response-purpose"));
            var plannedSemantic=await new ConversationRecall(planner,persona.EffectiveLexicon,[],semanticContext).RunAsync("礼物是给谁的？",[],DateTimeOffset.UtcNow,default,question.Id);
            Check("semantic_selection_in_single_planner",planner.Calls==1 && plannedSemantic.Knowledge!.Evidence.Single().Reference==reference && planner.Prompt.Contains("semantic_candidates"));
            Check("semantic_planning_and_execution_timing",new[]{"planning-candidate-sql","recall-planning-total","retrieval-execution","recall-wall"}.All(phase=>plannedSemantic.Knowledge!.Trace.Any(t=>t.StartsWith("timing;phase="+phase+";elapsed_ms=",StringComparison.Ordinal))));
            var incomplete=semanticPlan.Replace("SqlSemantic","SqlFacts");
            var repairedPlan=await new ConversationRecall(new SemanticPlannerLlm(incomplete),persona.EffectiveLexicon,[],semanticContext).RunAsync("礼物是给谁的？",[],DateTimeOffset.UtcNow,default,question.Id);
            Check("missing_bindings_use_valid_planned_originals",repairedPlan.Knowledge!.Evidence.Single().Reference==reference && repairedPlan.Knowledge.Trace.Contains("planner-adjustment=structured-read-fallback;task-index=0;from=SqlFacts;to=SqlSemantic"));
            var lexicalRepair=incomplete.Replace("礼物是给谁的","温水").Replace("\"selected_ids\":[\"preference\"]","\"selected_ids\":[]");
            var lexicalPlan=await new ConversationRecall(new SemanticPlannerLlm(lexicalRepair),persona.EffectiveLexicon,[],semanticContext).RunAsync("那换成什么？",[],DateTimeOffset.UtcNow,default,question.Id);
            Check("missing_bindings_preserve_safe_lexical_query",lexicalPlan.Knowledge!.Evidence.Any(e=>e.Reference==reference) && lexicalPlan.Steps.Single().Query=="温水" && lexicalPlan.Knowledge.Trace.Contains("planner-adjustment=structured-read-fallback;task-index=0;from=SqlFacts;to=SqlLexical"));
            var badPredicatePlan=incomplete.Replace("\"selected_ids\"","\"predicates\":[\"invented-private-predicate\"],\"selected_ids\"");
            var rejectedPredicate=await new ConversationRecall(new SemanticPlannerLlm(badPredicatePlan),persona.EffectiveLexicon,[],semanticContext).RunAsync("那换成什么？",[],DateTimeOffset.UtcNow,default,question.Id);
            Check("read_fallback_cannot_bypass_predicate_allowlist",rejectedPredicate.Knowledge!.Trace.Contains("planner-failure=predicates") && !rejectedPredicate.Knowledge.Trace.Any(t=>t.Contains("invented-private-predicate")));
            var missingFactsPlan="""
                {"intent":"喜好","tasks":[
                  {"id":"m1","source":"memory","strategy":"SqlFacts","query":"喜欢什么","entity_bindings":[{"mention":"我"}],"predicates":["prefers"],"selected_ids":["preference"]},
                  {"id":"m2","source":"memory","strategy":"SqlFacts","query":"不喜欢什么","entity_bindings":[{"mention":"我"}],"predicates":["prefers"],"selected_ids":["preference"]}]}
                """;
            var rescued=await new ConversationRecall(new SemanticPlannerLlm(missingFactsPlan),persona.EffectiveLexicon,[],semanticContext).RunAsync("还记得我说过什么？",[],DateTimeOffset.UtcNow,default,question.Id);
            Check("planned_selection_rescues_empty_facts_with_shared_budget",rescued.Knowledge!.Evidence.Any(e=>e.Reference==reference) && rescued.Knowledge.Trace.Contains("fact-miss-planned-semantic-fallback"));
            var slowPlanner=new SemanticPlannerLlm(semanticPlan,delayMs:2200);
            var slowPlanned=await new ConversationRecall(slowPlanner,persona.EffectiveLexicon,[],semanticContext).RunAsync("礼物是给谁的？",[],DateTimeOffset.UtcNow,default,question.Id);
            double Phase(string name)=>double.Parse(slowPlanned.Knowledge!.Trace.Single(t=>t.StartsWith("timing;phase="+name+";elapsed_ms=",StringComparison.Ordinal)).Split("elapsed_ms=")[1],System.Globalization.CultureInfo.InvariantCulture);
            Check("slow_planning_is_counted_without_extending_execution",slowPlanner.Calls==1 && slowPlanned.Knowledge!.Evidence.Single().Reference==reference && Phase("recall-planning-total")>=2200 && Phase("recall-wall")>=Phase("recall-planning-total") && Phase("retrieval-execution")<2000);
            foreach(var invalidIds in new[]{"\"invented\"","\"preference\",\"preference\"","\"current-question\"","\"selection-other\""})
            {
                var invalidPlan=semanticPlan.Replace("[\"preference\"]","["+invalidIds+"]");
                var rejected=await new ConversationRecall(new SemanticPlannerLlm(invalidPlan),persona.EffectiveLexicon,[],semanticContext).RunAsync("礼物是给谁的？",[],DateTimeOffset.UtcNow,default,question.Id);
                Check("semantic_planner_rejects_unbound_ids_"+results.Count,rejected.Planning.StartsWith("knowledge-fallback:InvalidDataException",StringComparison.Ordinal) && rejected.Knowledge!.Trace.Contains("planner-failure=semantic-ids") && !rejected.Knowledge.Trace.Any(t=>t.Contains("invented")));
            }
            var racingSource=evidence.Put(space,new("semantic-race","我正在做木雕。","user","semantic-race","turn"));
            var racingPlanner=new SemanticPlannerLlm(semanticPlan.Replace("preference","semantic-race"),()=>new KnowledgeDeletionService(db).Forget(access,space,[racingSource.Id]));
            var raceSelection=await new ConversationRecall(racingPlanner,persona.EffectiveLexicon,[],semanticContext).RunAsync("礼物是给谁的？",[],DateTimeOffset.UtcNow,default,question.Id);
            Check("semantic_selection_forget_during_planning_discards_result",raceSelection.Knowledge!.Evidence.Count==0 && raceSelection.Knowledge.Gaps.Contains("evidence-missing-or-invalid"));
            using(var stopSelection=new CancellationTokenSource())
            {
                stopSelection.Cancel();var stoppedSelection=false;
                try{evidence.PersonalSelectionCandidates(access,ct:stopSelection.Token);}catch(OperationCanceledException){stoppedSelection=true;}
                Check("semantic_candidates_preserve_cancellation",stoppedSelection);
            }
            var options=new ImmersionOptions {EnableCritic=false,EnableRuleGate=false,MaxRetrievalRetries=0};
            var plan="""
                {"intent":"聊手工和晚上饮品","tasks":[
                 {"id":"m","source":"memory","strategy":"SqlFacts","query":"晚上喝什么","entity_bindings":[{"mention":"我"}],"predicates":["drinks"],"required_facts":["温水"],"depends_on":[]},
                 {"id":"s","source":"story","strategy":"SqlLexical","query":"胡桃 帽子 亲手 花","required_facts":["亲手"],"depends_on":["m"]}]}
                """;
            var reply=JsonSerializer.Serialize(new {answerability="supported",segments=new[]{
                new {text="你晚上只喝温水。",kind="fact",evidence_ids=new[]{"memory:preference"}},
                new {text="帽子上的花是我亲手做的。",kind="fact",evidence_ids=new[]{line.EvidenceId}}}});
            var llm=new FixtureLlm(plan,reply);
            var agent=new ReactAgent(persona,llm,null,[],knowledge:context,immersionOptions:options,diagnostics:new LocalDiagnosticLog(Path.Combine(root,"runtime.log")));
            var turn=await agent.GenerateTextAsync("歇会儿，晚上喝什么？你帽子上的花又是怎么做的？",false);
            Check("agent_uses_sql_memory_and_story",turn.Recall?.Knowledge?.Evidence.Count==2 && turn.StoryAnswer?.Validated==true && turn.Reply.Contains("温水") && turn.Reply.Contains("亲手"));
            Check("actual_dependency_and_backend_trace",turn.Recall?.Steps.Single(s=>s.TaskId=="s").DependsOn!.SequenceEqual(["m"])==true && turn.Recall.Knowledge!.Trace.Any(t=>t.Contains("backend=sqlite")));
            Check("original_story_id_and_metadata_preserved",turn.Story?.Evidence.Single().Anchor.EvidenceId==line.EvidenceId && turn.Story.Evidence[0].Anchor.SourceFile==line.SourceFile);
            Check("composer_receives_revision_and_provenance",llm.ActorPrompts.Last().Contains("knowledge_references") && llm.ActorPrompts.Last().Contains("conversation_memory") && turn.Story?.ConversationMemories.Single().Record.ObservationTimeKnown==false);
            Check("no_second_planner_or_hidden_selector",llm.PlannerCalls==1 && llm.OtherCalls==1 && llm.ReviewerCalls==1);
            var ungroundedPlan=plan.Replace("\"query\":\"胡桃 帽子 亲手 花\"","\"query\":\"胡桃 帽子 亲手 花\",\"entity_bindings\":[{\"mention\":\"梅花\",\"from_task\":\"m\"}]");
            var ungrounded=await new ConversationRecall(new FixtureLlm(ungroundedPlan,reply),persona.EffectiveLexicon,[],context)
                .RunAsync("晚上饮品和帽子",[],DateTimeOffset.UtcNow,default);
            Check("ungrounded_dependency_cannot_fallback_to_independent_search",ungrounded.Steps.Single(s=>s.TaskId=="s").Gaps!.Contains("ungrounded-upstream-entity") &&
                ungrounded.Knowledge!.Evidence.All(e=>e.Reference.Namespace==space));
            StoryAnswerResult ValidateText(string text,string citation="memory:preference",string kind="fact")=>
                StoryAnswerComposer.Validate(JsonSerializer.Serialize(new {answerability="supported",segments=new[]{new{text,kind,evidence_ids=new[]{citation}}}}),turn.Story!);
            Check("polarity_reversal_rejected",ValidateText("你晚上不喝温水。").Issues.Contains("assertion_polarity_reversed"));
            Check("time_condition_change_rejected",ValidateText("你早上只喝温水。").Issues.Contains("assertion_time_condition_changed"));
            Check("user_experience_not_character",ValidateText("我晚上只喝温水。").Issues.Contains("user_experience_as_character"));
            Check("causal_inference_needs_source",ValidateText("因为做了花，所以才有帽子。",line.EvidenceId).Issues.Contains("causal_claim_without_source"));
            Check("forged_citation_rejected",ValidateText("你晚上只喝温水。","memory:invented").Issues.Contains("unknown_citation"));
            Check("thought_cannot_hide_preference_claim",ValidateText("你晚上只喝温水。",kind:"thought").Issues.Contains("fact_in_unattributed_segment"));
            Check("paraphrase_not_claimed_strictly_verified",turn.AnswerValidationLevel==AnswerValidationLevel.Relaxed);
            Check("verbatim_quote_strict",ValidateText(line.Text,line.EvidenceId,"quote").ValidationLevel==AnswerValidationLevel.Strict);
            var tagged=ValidateText("[emotion=cheerful;intensity=0.8]你晚上只喝温水。");
            Check("bubble_tag_not_treated_as_fact_number",tagged.Validated && tagged.Segments[0].Text=="你晚上只喝温水。");
            Check("advice_discourse_not_event_causation",ValidateText("所以这会儿，倒杯温水去吧。").Issues.All(i=>i!="causal_claim_without_source"));
            var repairedKnowledge=StoryAnswerComposer.TryLocalRepair(reply,turn.Story!);
            Check("knowledge_repair_does_not_invent_uncertainty",repairedKnowledge is not null && !repairedKnowledge.Reply.Contains("不一定全准"));
            StoryAnswerSegment[] claimDraft=[new("生日是明天。必须送到。","neutral",[],"thought")];
            const string claimRows="""{"segments":[{"index":0,"claims":[{"text":"生日是明天。","support":"supported"},{"text":"必须送到。","support":"unsupported"}]}]}""";
            Check("claim_partition_rejects_unsupported_tail",ClaimPartitionReviewer.Aggregate(claimRows,claimDraft).Contains("unsupported"));
            Check("claim_partition_rejects_rewritten_text",ClaimPartitionReviewer.Aggregate(claimRows.Replace("必须送到。","最好送到。"),claimDraft)=="{}");
            Check("claim_partition_rejects_omitted_text",ClaimPartitionReviewer.Aggregate("""{"segments":[{"index":0,"claims":[{"text":"生日是明天。","support":"supported"}]}]}""",claimDraft)=="{}");
            Check("claim_partition_rejects_duplicate_fields",ClaimPartitionReviewer.Aggregate(claimRows.Replace("\"index\":0","\"index\":0,\"index\":0"),claimDraft)=="{}");
            foreach(var mixedAction in new[]{"（凑过来看针脚）本堂主陪你慢慢来。","（点头）我陪你。（微笑）","（点头）陪你。)","（点头\n微笑）"})
            {
                var actionJson=JsonSerializer.Serialize(new {answerability="supported",segments=new[]{new{text=mixedAction,kind="action",emotion="neutral",evidence_ids=Array.Empty<string>()}}});
                Check("mixed_action_rejected_"+results.Count,StoryAnswerComposer.Validate(actionJson,turn.Story!).Issues.Contains("action_format"));
                Check("mixed_action_not_silently_wrapped_"+results.Count,StoryAnswerComposer.TryLocalRepair(actionJson,turn.Story!) is null);
            }
            var bareAction=JsonSerializer.Serialize(new {answerability="supported",segments=new[]{new{text="轻轻点头",kind="action",emotion="neutral",evidence_ids=Array.Empty<string>()}}});
            Check("bare_action_gets_single_envelope",StoryAnswerComposer.TryLocalRepair(bareAction,turn.Story!) is {Validated:true} actionRepair && actionRepair.Segments.Single().Text=="（轻轻点头）");
            foreach(var malformed in new[]{"{}","not-json","{\"segments\":[]}",
                "{\"segments\":[{\"index\":999999999999999999999,\"support\":\"supported\"}]}",
                "{\"segments\":[{\"index\":0,\"support\":\"supported\",\"rewrite\":\"invented\"}]}",
                "{\"segments\":[{\"index\":0,\"support\":\"subjective\"}]}",
                "{\"segments\":[{\"index\":0,\"support\":\"unsupported\"}]}"})
                Check("review_rejects_invalid_or_unsupported_"+results.Count,(await EvidenceSemanticReviewer.ReviewAsync(new ReviewLlm(malformed),ValidateText("你晚上只喝温水。"),turn.Recall!.Knowledge!,default)).Count>0);
            using(var reviewCancellation=new CancellationTokenSource())
            {
                reviewCancellation.Cancel();var propagated=false;
                try {await EvidenceSemanticReviewer.ReviewAsync(new ReviewLlm("{}"),ValidateText("你晚上只喝温水。"),turn.Recall!.Knowledge!,reviewCancellation.Token);}
                catch(OperationCanceledException){propagated=true;}
                Check("review_preserves_user_cancellation",propagated);
            }
            var ttsLlm=new FixtureLlm(plan,reply);var brokenTts=new BrokenSpeech();
            var spokenAgent=new ReactAgent(persona,ttsLlm,null,[],knowledge:context,immersionOptions:options,speechSynthesizer:brokenTts,
                diagnostics:new LocalDiagnosticLog(Path.Combine(root,"speech.log")));
            var spoken=await spokenAgent.RespondAsync("晚上喝什么，你帽子上的花怎么做的？");
            Check("tts_failure_does_not_repeat_retrieval_or_generation",spoken.Audio is null && brokenTts.Calls==1 && ttsLlm.PlannerCalls==1 && ttsLlm.OtherCalls==1 && spoken.Reply.Contains("温水"));
            var maliciousPlan=plan.Replace("\"query\":\"晚上喝什么\"","\"query\":\"晚上喝什么\",\"namespace\":\"memory:someone:hutao:desktop\"");
            var invalidRecall=new ConversationRecall(new FixtureLlm(maliciousPlan,reply),persona.EffectiveLexicon,[],context);
            var invalid=await invalidRecall.RunAsync("晚上喝温水",[],DateTimeOffset.UtcNow,default);
            Check("model_namespace_rejected_before_execution",invalid.Planning=="knowledge-fallback:InvalidDataException" && invalid.Steps.All(s=>s.TaskId!.StartsWith("fallback")));
            var badPredicate=new ConversationRecall(new FixtureLlm(plan.Replace("\"drinks\"","\"execute_sql\""),reply),persona.EffectiveLexicon,[],context);
            Check("model_unknown_predicate_rejected",(await badPredicate.RunAsync("晚上喝温水",[],DateTimeOffset.UtcNow,default)).Planning=="knowledge-fallback:InvalidDataException");
            var fresh=new ConversationRecall(new FixtureLlm("{\"intent\":\"计算\",\"tasks\":[]}",""),persona.EffectiveLexicon,[],context);
            var unrelated=await fresh.RunAsync("换个话题，帮我计算1+1",[],DateTimeOffset.UtcNow,default);
            Check("no_retrieval_for_unrelated_task",unrelated.Knowledge!.Evidence.Count==0 && unrelated.Story is null && unrelated.Memories.Count==0);
            var focusLlm=new FixtureLlm("{\"tasks\":[]}","答案是391。");
            var focusAgent=new ReactAgent(persona,focusLlm,null,[],immersionOptions:options);
            focusAgent.RestoreHistory([new("user","旧话题蓝色风筝"),new("assistant","继续聊风筝")]);
            await focusAgent.GenerateTextAsync("先不聊这个，17乘23是多少？",false);
            Check("topic_boundary_reaches_generation",focusLlm.ActorHistories.Last().All(m=>!m.Content.Contains("风筝")));
            await focusAgent.GenerateTextAsync("再加九呢？",false);
            Check("topic_boundary_persists_for_followup",focusLlm.ActorHistories.Last().Any(m=>m.Content.Contains("17乘23")) && focusLlm.ActorHistories.Last().All(m=>!m.Content.Contains("风筝")));
            Check("topic_boundary_keeps_persisted_history",focusAgent.History.Any(m=>m.Content.Contains("风筝")));
            Check("quoted_topic_instruction_is_not_boundary",!ConversationFocus.StartsNewTopic("他对我说换个话题，你怎么看？") && !ConversationFocus.StartsNewTopic("“换个话题”是什么意思？"));
            Check("explicit_topic_boundary_variants",new[]{"换个话题，聊音乐","先不谈这个。","不说这个！","好了，换一个话题："}.All(ConversationFocus.StartsNewTopic));
            var correction=new FixtureLlm(plan,reply,()=>evidence.Put(space,new("preference","现在晚上不喝温水了。","user","preference","turn")));
            var racing=new ReactAgent(persona,correction,null,[],knowledge:context,immersionOptions:options,diagnostics:new LocalDiagnosticLog(Path.Combine(root,"race.log")));
            var cancelled=await racing.GenerateTextAsync("晚上喝什么？你的帽子怎么做的？",false);
            Check("revision_changed_during_generation_discards_draft",cancelled.Observation=="knowledge-changed" && !cancelled.Reply.Contains("温水") && racing.History.All(h=>h.Role!="assistant" || !h.Content.Contains("温水")));
            // Use the still-valid public source so this tests the privacy epoch barrier independently.
            var storyPlan="{\"tasks\":[{\"id\":\"s\",\"source\":\"story\",\"strategy\":\"SqlLexical\",\"query\":\"帽子 亲手\"}]}";
            var forgetting=new FixtureLlm(storyPlan,reply,()=>
            {
                using var c=db.Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE namespaces SET privacy_epoch=privacy_epoch+1 WHERE namespace_id='corpus:conversation-fixture'";cmd.ExecuteNonQuery();
            });
            var forgetAgent=new ReactAgent(persona,forgetting,null,[],knowledge:context,immersionOptions:options,diagnostics:new LocalDiagnosticLog(Path.Combine(root,"forget.log")));
            var forgottenTrace=Path.Combine(root,"forgotten-trace.log");
            BackendTrace.Configure(forgottenTrace,enabled:true);
            TextResult forgotten;
            try {forgotten=await forgetAgent.GenerateTextAsync("帽子怎么做的？",false);}
            finally {BackendTrace.ResetToEnvironment();}
            Check("privacy_changed_during_generation_discards_draft",forgotten.Observation=="knowledge-changed" && forgotten.StoryAnswer is null);
            Check("privacy_changed_discards_pending_detailed_trace",!File.Exists(forgottenTrace));
            var earlyTracePath=Path.Combine(root,"early-trace.log");var earlyObserver=new FailingTraceObserver();
            var earlyAgent=new ReactAgent(persona,new FixtureLlm("{\"tasks\":[]}","合成回复"),null,[],knowledge:context,immersionOptions:options,observationCollector:earlyObserver);
            BackendTrace.Configure(earlyTracePath,enabled:true);
            try
            {
                try{await earlyAgent.GenerateTextAsync("合成工具失败",false);}catch(IOException){}
                var traceText=File.Exists(earlyTracePath)?File.ReadAllText(earlyTracePath):"";
                Check("trace_starts_before_tool_observation",earlyObserver.HadTrace && traceText.Contains("synthetic-tool-enter"));
                Check("early_tool_failure_closes_whole_turn_trace",traceText.Contains("回合失败: IOException") && traceText.Contains("文字回合总耗时") && traceText.Contains("不含 TTS"));
            }
            finally{BackendTrace.ResetToEnvironment();}
            var deliveryAgent=new ReactAgent(persona,new FixtureLlm("{\"tasks\":[]}","这是一条合成测试回复。"),null,[],knowledge:context,immersionOptions:options,
                diagnostics:new LocalDiagnosticLog(Path.Combine(root,"delivery.log")));
            var delivery=await deliveryAgent.GenerateTextAsync("换个话题",false);
            var privateSource=evidence.Put(space,new("delivery-forget","这是待删除的合成记录。","user","delivery-forget","turn"));
            new KnowledgeDeletionService(db).Forget(access,space,[privateSource.Id]);
            var stopped=false;try{delivery.Segments[0].DemandDelivery();}catch(OperationCanceledException){stopped=true;}
            Check("segment_guard_covers_history_even_without_retrieval",stopped && deliveryAgent.History.Count==0);
            deliveryAgent.RestoreHistory([new("user","旧日志中已被忘记的内容")]);
            Check("unversioned_history_cannot_restore_after_forgetting",deliveryAgent.History.Count==0);
            var currentStamp=deliveryAgent.PrivacyStamp;
            deliveryAgent.RestoreHistory([new("user","新的合成对话")],currentStamp);
            Check("versioned_current_history_can_restore",deliveryAgent.History.Count==1);
            var important=new HuTao.Dialogue.Storage.ImportantMemoryStore(Path.Combine(root,"unused-important.json"),repository:new HuTao.Knowledge.Memory.SqlImportantItemRepository(db,access));
            var privateMemory=new HuTao.Knowledge.Memory.ConversationMemoryStore(Path.Combine(root,"unused-memory.json"),repository:new HuTao.Knowledge.Memory.SqlConversationMemoryRepository(db,access));
            var deletionLlm=new FixtureLlm("{\"tasks\":[]}","已经删除指定事项了。");
            var deletionAgent=new ReactAgent(persona,deletionLlm,null,[],knowledge:context,importantMemory:important,memory:privateMemory,immersionOptions:options,
                diagnostics:new LocalDiagnosticLog(Path.Combine(root,"explicit-forget.log")));
            var rememberAgent=new ReactAgent(persona,new FixtureLlm("{\"tasks\":[]}","已经记住指定事项。"),null,[],knowledge:context,importantMemory:important,memory:privateMemory,immersionOptions:options,
                diagnostics:new LocalDiagnosticLog(Path.Combine(root,"explicit-remember.log")));
            await rememberAgent.GenerateTextAsync("记住紫色风铃",false);
            Check("remember_turns_linked_to_important_provenance",privateMemory.Count==2 && privateMemory.Snapshot().Single(m=>m.Speaker=="user").Sources.Single().StartsWith("important:",StringComparison.Ordinal));
            var deletionReply=await deletionAgent.GenerateTextAsync("忘记紫色风铃",false);
            Check("explicit_forget_confirms_under_new_epoch",deletionReply.Observation!="knowledge-changed" && deletionReply.Reply.Contains("删除"));
            Check("forget_request_not_retrieved_or_rememorized",deletionLlm.PlannerCalls==0 && privateMemory.Count==0 && deletionAgent.History.All(m=>!m.Content.Contains("紫色风铃")) && deletionLlm.ActorPrompts.All(p=>!p.Contains("紫色风铃")));
        }
        finally {Directory.Delete(root,true);}
        return results;
    }
    private sealed class FixtureLlm(string plan,string reply,Action? duringActor=null) : ILLMProvider
    {
        public string Name=>"knowledge-conversation-fixture";
        public int PlannerCalls {get;private set;}
        public int OtherCalls {get;private set;}
        public int ReviewerCalls {get;private set;}
        public List<string> ActorPrompts {get;}=[];
        public List<ChatMessage[]> ActorHistories {get;}=[];
        public Task<string> CompleteAsync(string systemPrompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)
        {
            ct.ThrowIfCancellationRequested();
            if(systemPrompt.Contains("Conversation Recall Planner")) {PlannerCalls++;return Task.FromResult(plan);}
            if(systemPrompt.Contains("Evidence Support Checker"))
            {
                ReviewerCalls++;
                var count=0;
                try{using var document=JsonDocument.Parse(reply);count=document.RootElement.GetProperty("segments").GetArrayLength();}
                catch(JsonException){count=new SpeechSegmentParser(null).Parse(reply).Count;}
                return Task.FromResult(JsonSerializer.Serialize(new {segments=Enumerable.Range(0,count).Select(i=>new {index=i,support="supported"}).ToArray()}));
            }
            OtherCalls++;ActorPrompts.Add(systemPrompt);ActorHistories.Add(history.ToArray());duringActor?.Invoke();return Task.FromResult(reply);
        }
    }
    private sealed class SemanticPlannerLlm(string plan,Action? duringPlanning=null,int delayMs=0):ILLMProvider
    {
        public string Name=>"semantic-planning-fixture";
        public int Calls {get;private set;}
        public string Prompt {get;private set;}="";
        public async Task<string> CompleteAsync(string systemPrompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)
        {
            ct.ThrowIfCancellationRequested();Calls++;Prompt=systemPrompt;
            if(!systemPrompt.Contains("Conversation Recall Planner"))throw new InvalidOperationException("Unexpected second model call.");
            if(delayMs>0)await Task.Delay(delayMs,ct);
            duringPlanning?.Invoke();return plan;
        }
    }
    private sealed class ReviewLlm(string reply) : ILLMProvider
    {
        public string Name=>"malformed-review-fixture";
        public Task<string> CompleteAsync(string systemPrompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)
        {ct.ThrowIfCancellationRequested();return Task.FromResult(reply);}
    }
    private sealed class BrokenSpeech : ICharacterSpeechSynthesizer
    {
        public bool IsAvailable=>true;
        public int Calls {get;private set;}
        public Task<TtsResult?> SynthesizeAsync(string text,string emotion,double intensity,CancellationToken ct=default)
        {Calls++;throw new IOException("Synthetic TTS failure.");}
    }
    private sealed class FailingTraceObserver:IToolObservationCollector
    {
        public bool HadTrace {get;private set;}
        public Task<string> CollectAsync(IReadOnlyDictionary<string,IAgentTool> tools,string? input,CancellationToken ct=default)
        {
            HadTrace=BackendTrace.Current is not null;
            BackendTrace.Current?.Note("synthetic-tool-enter");
            throw new IOException("synthetic");
        }
    }
}
