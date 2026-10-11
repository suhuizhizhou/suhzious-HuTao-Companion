using HuTao.Foundation.Abstractions;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using HuTao.Knowledge.Facts;
using System.Text.Json;

internal static class KnowledgeExtractionChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var results=new List<CheckResult>();
        void Check(string name,bool ok)=>results.Add(new("extraction_"+name,ok,""));
        var root=Path.Combine(Path.GetTempPath(),"hutao-extraction-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var db=new SqliteKnowledgeDatabase(Path.Combine(root,"test.db"));
            var access=KnowledgeAccessContext.ForSession("extract","hutao","desktop",[]);
            var repository=new SqlEvidenceRepository(db);
            var source=repository.Put(access.MemoryNamespace,new("one","我喜欢咖啡。","user","one","turn"));
            var provider=new FixtureProvider();var runner=new ExtractionJobRunner(db,provider);var budget=new ExtractionBudget(1);
            var entities=new[]{new ExtractionEntity("user","person","用户")};
            var first=await runner.RunAsync(access,source,entities,"user",budget);
            var cached=await runner.RunAsync(access,source,entities,"user",budget);
            Check("cache_avoids_second_call",provider.Calls==1 && budget.Calls==1 && cached.Cached && !first.Cached);
            Check("cache_preserves_sources",cached.Validations.Single().Assertion!.Sources.Single().Reference==source);
            Check("validated_preference",first.Validations.Single().Status=="accepted");
            var second=repository.Put(access.MemoryNamespace,new("two","我喜欢咖啡。","user","two","turn"));
            var exhausted=false;try {await runner.RunAsync(access,second,entities,"user",budget);}catch(InvalidOperationException){exhausted=true;}
            Check("hard_call_budget",exhausted && provider.Calls==1);
            provider.Cancel=true;
            try {await runner.RunAsync(access,second,entities,"user",new ExtractionBudget());}catch(OperationCanceledException){}
            var interruptedCalls=provider.Calls;
            provider.Cancel=false;
            var uncertain=false;try {await runner.RunAsync(access,second,entities,"user",new ExtractionBudget());}catch(InvalidOperationException){uncertain=true;}
            Check("uncertain_attempt_not_rebilled",uncertain && provider.Calls==interruptedCalls);
            using var c=db.Open();using var command=c.CreateCommand();
            command.CommandText="SELECT count(*) FROM extraction_jobs WHERE status='interrupted' AND output_json IS NULL";
            Check("cancelled_job_no_partial_result",Convert.ToInt64(command.ExecuteScalar())==1);
            command.CommandText="SELECT prompt_tokens FROM extraction_jobs WHERE status='completed'";
            Check("unavailable_usage_is_null",command.ExecuteScalar() is DBNull);
            foreach(var malformed in new[]{"not-json","{}",new string('x',32001)})
            {
                var failureId="metered-failure-"+results.Count;
                var failureSource=repository.Put(access.MemoryNamespace,new(failureId,"我喜欢咖啡。","user",failureId,"turn"));
                var failureRunner=new ExtractionJobRunner(db,new MeteredMalformedProvider(malformed));
                var failed=false;
                try{await failureRunner.RunAsync(access,failureSource,entities,"user",new ExtractionBudget(1));}
                catch(Exception ex) when(ex is JsonException or InvalidDataException){failed=true;}
                using var failureCommand=c.CreateCommand();
                failureCommand.CommandText="SELECT status,prompt_tokens,completion_tokens,output_json FROM extraction_jobs WHERE evidence_id=@id";
                failureCommand.Parameters.AddWithValue("@id",failureId);
                using var failureRow=failureCommand.ExecuteReader();
                Check("metered_invalid_output_keeps_usage_"+results.Count,failed && failureRow.Read() && failureRow.GetString(0)=="failed" && failureRow.GetInt64(1)==123 && failureRow.GetInt64(2)==17 && failureRow.IsDBNull(3));
            }
            var conditional=repository.Put(access.MemoryNamespace,new("conditional","我早上喝茶，晚上只喝温水。","user","conditional","turn"));
            var callsBefore=provider.Calls;var ruleBudget=new ExtractionBudget();
            var conditionals=await runner.RunAsync(access,conditional,entities,"user",ruleBudget);
            Check("conditional_split_without_api",provider.Calls==callsBefore && ruleBudget.Calls==0 && conditionals.Validations.Count==2 && conditionals.Validations.All(v=>v.Status=="accepted"));
            Check("conditional_dimensions_preserved",conditionals.Candidates.Any(a=>a.Literal=="茶" && a.Qualifiers["time_of_day"]=="morning") &&
                conditionals.Candidates.Any(a=>a.Literal=="温水" && a.Qualifiers["time_of_day"]=="evening" && a.Qualifiers["exclusive"]=="true"));
            var ruleEvidence=repository.Read(access,conditional)!;
            var invalidCondition=conditionals.Candidates[0] with {Qualifiers=new Dictionary<string,string>{{"time","早上"}}};
            Check("noncanonical_drink_condition_rejected",AssertionValidator.Validate("invalid-time",ruleEvidence,invalidCondition,entities,"user").Reason=="unsupported-drink-condition");
            foreach(var uncertainText in new[]{"我晚上喝茶吗","我晚上喝茶?","我晚上喝茶的话","我晚上喝茶可能睡不着","我晚上喝茶才怪","我晚上喝茶，能睡着吗"})
                Check("question_or_condition_not_published_"+results.Count,DeterministicAssertionRules.Extract(ruleEvidence with {Text=uncertainText},"user").Count==0);
            Check("assistant_not_user_fact",DeterministicAssertionRules.Extract(ruleEvidence with {Speaker="assistant"},"user").Count==0);
            var care=repository.Put(access.MemoryNamespace,new("care","胡桃照顾石狮子。","user","care","turn"));
            provider.ResponseOverride="""
                {"entities":[{"local_id":"new:lion","name":"石狮子","type":"object","start_utf16":4,"end_utf16":7,"quote":"石狮子"}],
                "assertions":[{"subject_id":"hutao","predicate_id":"cares_for","object_id":"new:lion","literal":null,"polarity":1,"qualifiers":{},"start_utf16":0,"end_utf16":8,"quote":"胡桃照顾石狮子。"}]}
                """;
            var proposed=await runner.RunAsync(access,care,[..entities,new("hutao","character","胡桃")],"user",new ExtractionBudget());
            Check("new_entity_kept_reviewable",proposed.EntityProposals!.Single().Status=="candidate" && proposed.Validations.Single().Status=="candidate");
            Check("new_entity_relationship_remapped",proposed.Validations.Single().Assertion!.ObjectId==proposed.EntityProposals!.Single().Entity!.Id);
            var material=repository.Read(access,care)!;
            var mention=new ExtractedEntity("new:lion","石狮子","object",4,7,"石狮子");
            var same=EntityProposalValidator.Validate(material,mention);
            var other=EntityProposalValidator.Validate(material with {Reference=new(access.MemoryNamespace,"different-source",1)},mention);
            Check("same_name_different_source_not_merged",same.Entity!.Id!=other.Entity!.Id);
            Check("fake_mention_rejected",EntityProposalValidator.Validate(material,mention with {Name="石头",Quote="石头"}).Status=="rejected");
            Check("pronoun_not_new_entity",EntityProposalValidator.Validate(material,mention with {Name="她"}).Status=="rejected");
            command.CommandText="SELECT count(*) FROM entities";
            Check("candidate_not_auto_published",Convert.ToInt64(command.ExecuteScalar())==0);
            var facts=new SqlAssertionRepository(db);
            facts.RegisterEntity(access,access.MemoryNamespace,"user","person","用户");
            facts.RegisterEntity(access,access.MemoryNamespace,"hutao","character","胡桃");
            var publisher=new ExtractionPublisher(db);
            var firstIds=first.Validations.Select(v=>v.Assertion!.Id).ToHashSet(StringComparer.Ordinal);
            var staleSequence=repository.ReadStamps(access,[access.MemoryNamespace])[access.MemoryNamespace].CommitSequence;
            var publication=publisher.Publish(access,first.JobId,firstIds,"deterministic-rules");
            Check("publication_idempotent",publication.Assertions==1 && publisher.Publish(access,first.JobId,firstIds,"deterministic-rules").AlreadyPublished);
            var proposedIds=proposed.Validations.Select(v=>v.Assertion!.Id).ToHashSet(StringComparer.Ordinal);
            var rejected=false;try {publisher.Publish(access,proposed.JobId,proposedIds,"fixture-review");}catch(InvalidDataException){rejected=true;}
            Check("candidate_requires_review",rejected);
            publisher.Publish(access,proposed.JobId,proposedIds,"fixture-review",approveCandidates:true);
            Check("reviewed_entity_and_fact_published",facts.Query(access,access.MemoryNamespace,new("hutao","cares_for")).Count==1);
            command.CommandText="SELECT output_json FROM extraction_jobs WHERE job_id=@job";command.Parameters.Clear();command.Parameters.AddWithValue("@job",conditionals.JobId);
            var original=(string)command.ExecuteScalar()!;
            var damaged=JsonSerializer.Deserialize<StoredExtraction>(original)!;
            var validations=damaged.Validations.ToArray();validations[1]=validations[1] with {Assertion=validations[1].Assertion! with {PredicateId="invalid-predicate"}};
            command.CommandText="UPDATE extraction_jobs SET output_json=@json WHERE job_id=@job";command.Parameters.AddWithValue("@json",JsonSerializer.Serialize(damaged with {Validations=validations}));command.ExecuteNonQuery();
            var conditionalIds=conditionals.Validations.Select(v=>v.Assertion!.Id).ToHashSet(StringComparer.Ordinal);
            var rolledBack=false;try {publisher.Publish(access,conditionals.JobId,conditionalIds,"deterministic-rules");}catch(InvalidDataException){rolledBack=true;}
            Check("publication_batch_failure_atomic",rolledBack && facts.Query(access,access.MemoryNamespace,new("user","drinks")).Count==0);
            using(var check=db.Open())using(var query=check.CreateCommand())
            {
                query.CommandText="SELECT count(*) FROM graph_outbox WHERE aggregate_id=@one OR aggregate_id=@two";
                query.Parameters.AddWithValue("@one",conditionals.Validations[0].Assertion!.Id);query.Parameters.AddWithValue("@two",conditionals.Validations[1].Assertion!.Id);
                Check("publication_failure_rolls_back_outbox",Convert.ToInt64(query.ExecuteScalar())==0);
            }
            command.Parameters["@json"].Value=original;command.ExecuteNonQuery();
            var staleRejected=false;
            try{publisher.Publish(access,conditionals.JobId,conditionalIds,"deterministic-rules",supersedes:new Dictionary<string,string>(),expectedCommitSequence:staleSequence);}
            catch(InvalidOperationException){staleRejected=true;}
            Check("stale_correction_snapshot_rejected_atomically",staleRejected && !publisher.HasDecision(access,conditionals.JobId) && facts.Query(access,access.MemoryNamespace,new("user","drinks")).Count==0);
            publisher.Publish(access,conditionals.JobId,conditionalIds,"deterministic-rules");
            Check("publication_retry_after_rollback",facts.Query(access,access.MemoryNamespace,new("user","drinks")).Count==2);
            Check("publication_retraction_current_hidden",publisher.Retract(access,first.JobId)==1 && facts.Query(access,access.MemoryNamespace,new("user","prefers")).Count==0);
            Check("publication_retraction_idempotent",publisher.Retract(access,first.JobId)==0 && repository.Read(access,source) is not null);
            var resurrection=false;try {publisher.Publish(access,first.JobId,firstIds,"deterministic-rules");}catch(InvalidOperationException){resurrection=true;}
            Check("retracted_publication_cannot_replay",resurrection);
            Check("retracted_job_retains_decision_for_runtime_replay",publisher.HasDecision(access,first.JobId));
            var deferredAccess=KnowledgeAccessContext.ForSession("deferred","hutao","desktop",[]);
            facts.RegisterEntity(deferredAccess,deferredAccess.MemoryNamespace,"user","person","用户");
            var deferredStore=new HuTao.Knowledge.Memory.ConversationMemoryStore(Path.Combine(root,"unused-deferred.json"),repository:new HuTao.Knowledge.Memory.SqlConversationMemoryRepository(db,deferredAccess));
            var deferredUser=deferredStore.ObserveTurn("user","我喜欢咖啡。",DateTimeOffset.Now)!;
            var deferredQueue=new TurnIngestionQueue(db);var deferredLease=deferredQueue.Claim(deferredAccess)!;
            deferredQueue.Complete(deferredAccess,deferredLease,"deferred");
            var deferredProvider=new FixtureProvider();var deferredService=new DeferredTurnExtraction(db,deferredProvider);
            var selection=deferredService.List(deferredAccess).Single();
            var deferredBudget=new ExtractionBudget(1);
            var deferredResult=await deferredService.ExtractAsync(deferredAccess,selection.Reference,deferredBudget);
            Check("deferred_extraction_preserves_source_without_publication",deferredProvider.Calls==1 && deferredResult.Validations.Single().Assertion!.Sources.Single().Reference==selection.Reference && facts.Query(deferredAccess,deferredAccess.MemoryNamespace,new("user","prefers")).Count==0);
            var deferredCached=await deferredService.ExtractAsync(deferredAccess,selection.Reference,deferredBudget);
            Check("deferred_review_cache_not_rebilled",deferredCached.Cached && deferredProvider.Calls==1 && deferredService.List(deferredAccess).Single().Status=="deferred");
            string[] Admin(string action,params string[] extra)=>[action,"--db",db.FilePath,"--user","deferred","--persona","hutao","--scope","desktop",..extra];
            var listing=new StringWriter();
            Check("admin_list_has_ids_without_private_text",await HuTao.Dialogue.Runtime.KnowledgeAdminCommand.RunAsync(Admin("list"),listing)==0 && listing.ToString().Contains(deferredUser.Id) && !listing.ToString().Contains("咖啡"));
            var reviewOutput=new StringWriter();
            Check("admin_review_includes_current_source",await HuTao.Dialogue.Runtime.KnowledgeAdminCommand.RunAsync(Admin("review","--job",deferredResult.JobId),reviewOutput)==0 && JsonDocument.Parse(reviewOutput.ToString()).RootElement.GetProperty("SourceCurrent").GetBoolean());
            var wrongScope=Admin("review","--job",deferredResult.JobId);wrongScope[4]="another-user";
            Check("admin_review_rejects_wrong_scope",await HuTao.Dialogue.Runtime.KnowledgeAdminCommand.RunAsync(wrongScope,new StringWriter())==1);
            var decisionPath=Path.Combine(root,"decision.json");
            File.WriteAllText(decisionPath,JsonSerializer.Serialize(new HuTao.Dialogue.Runtime.KnowledgeAdminCommand.Decision(deferredResult.JobId,"fixture-human",deferredResult.Validations.Select(v=>v.Assertion!.Id).ToArray())));
            Check("admin_explicit_decision_publishes_selected_fact",await HuTao.Dialogue.Runtime.KnowledgeAdminCommand.RunAsync(Admin("publish","--decision-file",decisionPath),new StringWriter())==0 && facts.Query(deferredAccess,deferredAccess.MemoryNamespace,new("user","prefers")).Count==1);
            var cliUser=deferredStore.ObserveTurn("user","我喜欢茶。",DateTimeOffset.Now,deduplicate:false)!;
            var cliProvider=new FixtureProvider {ResponseOverride="""
                {"assertions":[{"subject_id":"user","predicate_id":"prefers","object_id":null,"literal":"茶","polarity":1,"qualifiers":{},"start_utf16":0,"end_utf16":5,"quote":"我喜欢茶。"}]}
                """};var extractionOutput=new StringWriter();
            Check("admin_extract_is_bounded_and_unpublished",await HuTao.Dialogue.Runtime.KnowledgeAdminCommand.RunAsync(Admin("extract","--source",cliUser.Id,"--revision","1"),extractionOutput,()=>cliProvider)==0 && cliProvider.Calls==1 && JsonDocument.Parse(extractionOutput.ToString()).RootElement.GetProperty("Publication").GetString()=="not-published");
            var extractedCommand=JsonDocument.Parse(extractionOutput.ToString()).RootElement;
            var correctionId=extractedCommand.GetProperty("Validations")[0].GetProperty("Assertion").GetProperty("Id").GetString()!;
            var correctionJob=extractedCommand.GetProperty("JobId").GetString()!;
            var correctionReview=new StringWriter();
            await HuTao.Dialogue.Runtime.KnowledgeAdminCommand.RunAsync(Admin("review","--job",correctionJob),correctionReview);
            var reviewedSequence=JsonDocument.Parse(correctionReview.ToString()).RootElement.GetProperty("ExpectedCommitSequence").GetInt64();
            var correctionDecision=new HuTao.Dialogue.Runtime.KnowledgeAdminCommand.Decision(correctionJob,"fixture-human",[correctionId],false,new Dictionary<string,string>{{correctionId,deferredResult.Validations[0].Assertion!.Id}},reviewedSequence-1);
            File.WriteAllText(decisionPath,JsonSerializer.Serialize(correctionDecision));
            Check("admin_stale_correction_rejected",await HuTao.Dialogue.Runtime.KnowledgeAdminCommand.RunAsync(Admin("publish","--decision-file",decisionPath),new StringWriter())==1 && facts.Query(deferredAccess,deferredAccess.MemoryNamespace,new("user","prefers")).Single().Literal=="咖啡");
            File.WriteAllText(decisionPath,JsonSerializer.Serialize(correctionDecision with {ExpectedCommitSequence=reviewedSequence}));
            Check("admin_reviewed_correction_replaces_old_fact",await HuTao.Dialogue.Runtime.KnowledgeAdminCommand.RunAsync(Admin("publish","--decision-file",decisionPath),new StringWriter())==0 && facts.Query(deferredAccess,deferredAccess.MemoryNamespace,new("user","prefers")).Single().Literal=="茶");
            Check("admin_reviewed_correction_replay_is_idempotent",await HuTao.Dialogue.Runtime.KnowledgeAdminCommand.RunAsync(Admin("publish","--decision-file",decisionPath),new StringWriter())==0);
            new KnowledgeDeletionService(db).Forget(deferredAccess,deferredAccess.MemoryNamespace,[cliUser.Id]);
            new KnowledgeDeletionService(db).Forget(deferredAccess,deferredAccess.MemoryNamespace,[deferredUser.Id]);
            var deletedPublicationRejected=false;
            try{publisher.Publish(deferredAccess,deferredResult.JobId,deferredResult.Validations.Select(v=>v.Assertion!.Id).ToHashSet(StringComparer.Ordinal),"fixture-review");}
            catch(InvalidOperationException){deletedPublicationRejected=true;}
            Check("deferred_source_forgetting_invalidates_review",deferredService.List(deferredAccess).Count==0 && deletedPublicationRejected);
        }
        finally {Directory.Delete(root,true);}
        return results;
    }
    private sealed class FixtureProvider:ILLMProvider
    {
        public string Name=>"extraction-test-fixture";
        public int Calls {get;private set;}
        public bool Cancel {get;set;}
        public string? ResponseOverride {get;set;}
        public Task<string> CompleteAsync(string systemPrompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)
        {
            Calls++;if(Cancel)throw new OperationCanceledException();
            if(ResponseOverride is not null)return Task.FromResult(ResponseOverride);
            return Task.FromResult("""
                {"assertions":[{"subject_id":"user","predicate_id":"prefers","object_id":null,"literal":"咖啡","polarity":1,"qualifiers":{},"start_utf16":0,"end_utf16":6,"quote":"我喜欢咖啡。"}]}
                """);
        }
    }
    private sealed class MeteredMalformedProvider(string response):IMeteredLLMProvider
    {
        public string Name=>"metered-malformed";
        public string ModelIdentity=>Name;
        public int MaxOutputTokens=>1000;
        public Task<string> CompleteAsync(string systemPrompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)=>Task.FromResult(response);
        public Task<LlmCompletion> CompleteWithUsageAsync(string systemPrompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)=>Task.FromResult(new LlmCompletion(response,123,17,1,Name));
    }
}
