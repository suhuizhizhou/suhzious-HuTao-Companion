using System.Text.Json;
using HuTao.Foundation.Abstractions;
using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Import;
using HuTao.Knowledge.Memory;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using Microsoft.Data.Sqlite;

internal static class KnowledgeDeletionChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var results=new List<CheckResult>();void Check(string name,bool ok)=>results.Add(new("forget_"+name,ok,"real SQLite forgetting"));
        var root=Path.Combine(Path.GetTempPath(),"hutao-forget-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var db=new SqliteKnowledgeDatabase(Path.Combine(root,"test.db"));
            var access=KnowledgeAccessContext.ForSession("forget","hutao","desktop",[]);var space=access.MemoryNamespace;
            var other=KnowledgeAccessContext.ForSession("forget","hutao","chatroom",[]);
            var repository=new SqlConversationMemoryRepository(db,access);var evidence=new SqlEvidenceRepository(db);var facts=new SqlAssertionRepository(db);
            var at=DateTimeOffset.UtcNow.AddDays(-1);const string secret="我喜欢紫色风铃。";
            MemoryRecord Record(string id,string text,MemoryKind kind=MemoryKind.Turn,params string[] sources)=>new()
            {Id=id,Text=text,Speaker=kind==MemoryKind.Turn?"user":"consolidation",Kind=kind,ObservedAt=at,RecordedAt=at,Fingerprint=ConversationMemoryStore.Fingerprint("user",text),Sources=sources.ToList()};
            var state=new MemoryFileState {Records=[Record("original",secret),Record("unrelated","我在写代码。"),
                Record("derived","用户喜欢紫色风铃。",MemoryKind.Preference,"original","unrelated"),
                Record("duplicate-a","我晚上只喝温水。"),Record("duplicate-b","我晚上只喝温水。"),
                Record("shared","用户晚上只喝温水。",MemoryKind.Preference,"duplicate-a","duplicate-b")]};
            repository.Save(state,0);var stale=repository.Load();
            var cachedStore=new ConversationMemoryStore(Path.Combine(root,"unused.json"),repository:repository);
            var original=evidence.Read(access,new(space,"original",1))!;
            facts.RegisterEntity(access,space,"user","person","用户");
            facts.Put(access,space,new("secret","user","prefers",null,"紫色风铃",1,new Dictionary<string,string>(),[new(original.Reference,0,secret.Length,secret)]),DateTimeOffset.UtcNow);
            const string drink="我晚上只喝温水。";
            facts.Put(access,space,new("shared","user","drinks",null,"温水",1,new Dictionary<string,string>(),
                [new(new(space,"duplicate-a",1),0,drink.Length,drink),new(new(space,"duplicate-b",1),0,drink.Length,drink)]),DateTimeOffset.UtcNow);
            var otherRef=evidence.Put(other.MemoryNamespace,new("original",secret,"user","source","turn"));
            var deletion=new KnowledgeDeletionService(db);
            long Number(string sql)
            {using var c=db.Open();using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.Parameters.AddWithValue("@n",space);return Convert.ToInt64(cmd.ExecuteScalar());}
            var epoch=Number("SELECT privacy_epoch FROM namespaces WHERE namespace_id=@n");
            var outboxBefore=Number("SELECT count(*) FROM graph_outbox WHERE namespace_id=@n");
            // Fail late in the transaction: source clearing, retraction and outbox must all roll back.
            using(var c=db.Open())using(var cmd=c.CreateCommand())
            {cmd.CommandText="CREATE TRIGGER fail_forgetting BEFORE UPDATE OF privacy_epoch ON namespaces BEGIN SELECT RAISE(ABORT,'fixture'); END";cmd.ExecuteNonQuery();}
            var rolledBack=false;try{deletion.Forget(access,space,["original"]);}catch(SqliteException){rolledBack=true;}
            Check("transaction_failure_rolls_back_everything",rolledBack && evidence.Read(access,original.Reference)?.Text==secret && facts.Query(access,space,new("user","prefers")).Count==1 && Number("SELECT count(*) FROM graph_outbox WHERE namespace_id=@n")==outboxBefore);
            using(var c=db.Open())using(var cmd=c.CreateCommand()){cmd.CommandText="DROP TRIGGER fail_forgetting";cmd.ExecuteNonQuery();}
            var recoveryPackage=Path.Combine(root,"before-forget.zip");KnowledgeBackupPackage.Create(db,recoveryPackage);
            var removed=deletion.Forget(access,space,["original","duplicate-a"]);
            var recoveryPath=Path.Combine(root,"after-reconcile.db");
            var recoveryReport=KnowledgeBackupPackage.RestoreWithDeletions(recoveryPackage,db.FilePath,recoveryPath);
            var recovered=new SqliteKnowledgeDatabase(recoveryPath);var recoveredEvidence=new SqlEvidenceRepository(recovered);var recoveredFacts=new SqlAssertionRepository(recovered);
            Check("restore_replays_transitive_forgetting_and_preserves_other_scope",recoveryReport.Recovery.EvidenceDeleted==3 &&
                recoveredEvidence.Read(access,new(space,"derived",1)) is null && recoveredEvidence.Read(other,otherRef)?.Text==secret &&
                recoveredFacts.Query(access,space,new("user","prefers")).Count==0 && recoveredEvidence.Search(access,"紫色风铃").Count==0);
            Check("restore_rebinds_shared_fact_to_remaining_source",recoveredFacts.Query(access,space,new("user","drinks")).Single().Sources.Single().Reference.Id=="duplicate-b");
            Check("source_and_unsupported_derivation_scrubbed",removed.EvidenceDeleted==3 && evidence.Read(access,new(space,"derived",1)) is null && evidence.Read(access,original.Reference) is null);
            Check("shared_equivalent_source_revalidated",removed.SharedDerivationsRetained==1 && repository.Load().State.Records.Single(r=>r.Id=="shared").Sources.SequenceEqual(["duplicate-b"]));
            Check("assertion_retracted_or_rebound",removed.AssertionsRetracted==1 && facts.Query(access,space,new("user","prefers")).Count==0 && facts.Query(access,space,new("user","drinks")).Single().Sources.Single().Reference.Id=="duplicate-b");
            Check("privacy_epoch_atomic",removed.PrivacyEpoch==epoch+1);
            Check("chatroom_untouched",evidence.Read(other,otherRef)?.Text==secret);
            Check("fts_forgets_secret",evidence.Search(access,"紫色风铃").Count==0);
            Check("plaintext_fingerprint_scrubbed",Number("SELECT count(*) FROM memory_records WHERE namespace_id=@n AND fingerprint LIKE '%紫色风铃%'")==0);
            Check("existing_store_refreshes_after_forgetting",cachedStore.Snapshot().All(r=>!r.Text.Contains("紫色风铃")));
            Check("historical_literal_scrubbed",Number("SELECT count(*) FROM assertions WHERE namespace_id=@n AND typed_literal='紫色风铃'")==0);
            Check("repeat_is_idempotent",deletion.Forget(access,space,["original","duplicate-a"]).PrivacyEpoch==removed.PrivacyEpoch);
            var resurrect=false;try {evidence.Put(space,new("original",secret,"user","source","turn"));}catch(InvalidOperationException){resurrect=true;}
            Check("stable_id_cannot_resurrect",resurrect);
            var staleRejected=false;try {repository.Save(stale.State,stale.Revision);}catch(InvalidOperationException){staleRejected=true;}
            Check("stale_memory_writer_rejected",staleRejected);
            var legacy=Path.Combine(root,"old.json");File.WriteAllText(legacy,JsonSerializer.Serialize(state,MemoryFileState.Json));
            var importRejected=false;try {new MemoryJsonImporter(repository).Import(legacy,false);}catch(InvalidOperationException){importRejected=true;}
            Check("old_json_cannot_reimport",importRejected);
            var denied=false;try {deletion.Forget(access,other.MemoryNamespace,["original"]);}catch(UnauthorizedAccessException){denied=true;}
            Check("scope_checked_before_mutation",denied);
            var late=evidence.Put(space,new("late","我喜欢咖啡。","user","late","turn"));
            var provider=new DuringExtraction(()=>deletion.Forget(access,space,["late"]));
            var discarded=false;
            try {await new ExtractionJobRunner(db,provider).RunAsync(access,late,[new("user","person","用户")],"user",new());}
            catch(InvalidDataException){discarded=true;}
            Check("inflight_extraction_cannot_restore_body",discarded && Number("SELECT count(*) FROM extraction_jobs WHERE namespace_id=@n AND output_json IS NOT NULL")==0);
            var stillLive=evidence.Put(space,new("live-extraction","我喜欢茶。","user","live-extraction","turn"));
            var unrelatedSecret=evidence.Put(space,new("catalog-secret","实体目录中的合成秘密。","user","catalog-secret","turn"));
            discarded=false;
            try {await new ExtractionJobRunner(db,new DuringExtraction(()=>deletion.Forget(access,space,[unrelatedSecret.Id])))
                .RunAsync(access,stillLive,[new("user","person","用户")],"user",new());}
            catch(InvalidDataException){discarded=true;}
            Check("namespace_epoch_invalidates_inflight_entity_catalog",discarded && evidence.Read(access,stillLive) is not null && Number("SELECT count(*) FROM extraction_jobs WHERE namespace_id=@n AND output_json IS NOT NULL")==0);
            var newEpoch=await new ExtractionJobRunner(db,new DuringExtraction(()=>{})).RunAsync(access,stillLive,[new("user","person","用户")],"user",new());
            Check("fresh_epoch_can_extract_again_without_old_cache",!newEpoch.Cached);
            var backup=Path.Combine(root,"backup.db");db.Backup(backup);var backupDb=new SqliteKnowledgeDatabase(backup);
            Check("new_backup_has_no_forgotten_source",new SqlEvidenceRepository(backupDb).Read(access,original.Reference) is null && new SqlConversationMemoryRepository(backupDb,access).Load().State.Records.All(r=>!r.Text.Contains("紫色风铃")));
            Check("new_backup_has_no_deleted_derivation_bytes",!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(backup)).Contains("用户喜欢紫色风铃"));
            var pin=evidence.Put(space,new("pin-source","我喜欢绿茶。","user","pin-source","turn"));
            var pinState=repository.Load();var answer=Record("pin-answer","你喜欢绿茶。");answer.Speaker="assistant";
            answer.Sources.Add(pin.Id);answer.SourceRevisions[pin.Id]=pin.Revision;pinState.State.Records.Add(answer);
            var replacement=evidence.Put(space,new("pin-source","我现在喜欢红茶。","user","pin-source","turn"));
            var changed=false;try{repository.Save(pinState.State,pinState.Revision);}catch(InvalidDataException){changed=true;}
            Check("stale_generation_source_revision_rolls_back",changed && evidence.Read(access,new(space,"pin-answer",1)) is null);
            answer.Text="你现在喜欢红茶。";answer.SourceRevisions[pin.Id]=replacement.Revision;
            repository.Save(pinState.State,pinState.Revision);
            var saved=repository.Load();Check("source_revision_roundtrip",saved.State.Records.Single(r=>r.Id==answer.Id).SourceRevisions[pin.Id]==replacement.Revision);
            var followup=Record("pin-followup","再次确认你喜欢红茶。");followup.Speaker="assistant";
            followup.Sources.Add(answer.Id);followup.SourceRevisions[answer.Id]=1;saved.State.Records.Add(followup);
            repository.Save(saved.State,saved.Revision);saved=repository.Load();
            facts.Put(access,space,new("provenance-fact","user","prefers",null,"红茶",1,new Dictionary<string,string>(),[new(new(space,followup.Id,1),0,followup.Text.Length,followup.Text)]),DateTimeOffset.UtcNow);
            var historicalKnownAt=DateTimeOffset.UtcNow;
            Check("current_recursive_provenance_readable_before_source_revision",evidence.Read(access,new(space,followup.Id,1)) is not null);
            await Task.Delay(10);
            var newer=evidence.Put(space,new("pin-source","我现在喜欢白茶。","user","pin-source","turn"));
            Check("revised_source_blocks_direct_and_transitive_derived_reads",evidence.Read(access,new(space,answer.Id,1)) is null && evidence.Read(access,new(space,followup.Id,1)) is null);
            Check("revised_source_blocks_fts_and_hydration",!evidence.Search(access,"红茶").Any(r=>r.Reference.Id==answer.Id || r.Reference.Id==followup.Id) &&
                evidence.ReadMany(access,[new(space,answer.Id,1),new(space,followup.Id,1)],new("红茶")).Count==0);
            Check("fact_with_transitively_stale_source_not_returned",facts.Query(access,space,new("user","prefers",AssertionIds:["provenance-fact"])).Count==0);
            Check("historical_provenance_uses_known_time",evidence.ReadMany(access,[new(space,answer.Id,1),new(space,followup.Id,1)],new("红茶",KnowledgeTimeMode.Historical,KnownAt:historicalKnownAt)).Count==2);
            repository.Save(saved.State,saved.Revision);
            var stillPinned=repository.Load();
            Check("unrelated_save_never_rebinds_old_answer",stillPinned.State.Records.Single(r=>r.Id==answer.Id).SourceRevisions[pin.Id]==replacement.Revision);
            stillPinned.State.Records.Single(r=>r.Id==answer.Id).SourceRevisions[pin.Id]=newer.Revision;
            changed=false;try{repository.Save(stillPinned.State,stillPinned.Revision);}catch(InvalidDataException){changed=true;}
            Check("existing_answer_provenance_cannot_be_rewritten",changed);
            var cycleA=evidence.Put(space,new("cycle-a","循环校验甲","user","cycle-a","turn"));
            var cycleB=evidence.Put(space,new("cycle-b","循环校验乙","user","cycle-b","turn"));
            using(var c=db.Open())using(var command=c.CreateCommand())
            {command.CommandText="INSERT INTO memory_derivations VALUES(@n,'cycle-a',1,'cycle-b',1),(@n,'cycle-b',1,'cycle-a',1)";command.Parameters.AddWithValue("@n",space);command.ExecuteNonQuery();}
            Check("cyclic_provenance_is_not_readable",evidence.Read(access,cycleA) is null && evidence.ReadMany(access,[cycleB],new("循环校验")).Count==0 && evidence.Search(access,"循环校验").Count==0);
            for(var depth=0;depth<65;depth++)evidence.Put(space,new("depth-"+depth,"有界来源检查"+depth,"user","depth-"+depth,"turn"));
            using(var c=db.Open())using(var transaction=c.BeginTransaction())
            {
                for(var depth=1;depth<65;depth++)
                {using var command=c.CreateCommand();command.Transaction=transaction;command.CommandText="INSERT INTO memory_derivations VALUES(@n,@id,1,@source,1)";command.Parameters.AddWithValue("@n",space);command.Parameters.AddWithValue("@id","depth-"+depth);command.Parameters.AddWithValue("@source","depth-"+(depth-1));command.ExecuteNonQuery();}
                transaction.Commit();
            }
            Check("provenance_depth_budget_fails_closed",evidence.Read(access,new(space,"depth-63",1)) is not null && evidence.Read(access,new(space,"depth-64",1)) is null);
        }
        finally {Directory.Delete(root,true);}
        return results;
    }
    private sealed class DuringExtraction(Action during) : ILLMProvider
    {
        public string Name=>"deletion-race-fixture";
        public Task<string> CompleteAsync(string systemPrompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)
        {during();return Task.FromResult("{\"assertions\":[]}");}
    }
}
