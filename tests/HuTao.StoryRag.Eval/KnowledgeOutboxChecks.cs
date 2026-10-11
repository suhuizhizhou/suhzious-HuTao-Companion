using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;

internal static class KnowledgeOutboxChecks
{
    public static List<CheckResult> Run()
    {
        var results=new List<CheckResult>();void Check(string name,bool ok)=>results.Add(new("outbox_"+name,ok,""));
        var root=Path.Combine(Path.GetTempPath(),"hutao-outbox-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var db=new SqliteKnowledgeDatabase(Path.Combine(root,"db.sqlite"));
            var access=KnowledgeAccessContext.ForSession("outbox","hutao","desktop",[]);var space=access.MemoryNamespace;
            var evidence=new SqlEvidenceRepository(db);var facts=new SqlAssertionRepository(db);var outbox=new GraphOutboxRepository(db);
            facts.RegisterEntity(access,space,"user","person","用户");
            var text="我喜欢温水。";var reference=evidence.Put(space,new("turn",text,"user","turn","turn"));
            var source=new AssertionSource(reference,0,text.Length,text);var now=DateTimeOffset.UtcNow;
            facts.Put(access,space,new("a","user","prefers",null,"温水",1,new Dictionary<string,string>(),[source]),now);
            facts.Put(access,space,new("b","user","prefers",null,"茶",1,new Dictionary<string,string>(),[source]),now);
            var first=outbox.Claim(access,space,now,TimeSpan.FromSeconds(5))!;
            Check("committed_fact_has_event",first.AggregateId=="a" && first.AggregateRevision==1 && first.Kind=="assertion-upsert");
            Check("live_lease_blocks_duplicate_claim",outbox.Claim(access,space,now) is null);
            var recovered=outbox.Claim(access,space,now.AddSeconds(6))!;
            Check("expired_lease_recovers_same_event",recovered.EventId==first.EventId && recovered.LeaseToken!=first.LeaseToken && recovered.Attempts==2);
            Check("old_owner_cannot_ack",!outbox.Acknowledge(access,first,now.AddSeconds(7)));
            Check("retry_diagnostic",outbox.Fail(access,recovered,now.AddSeconds(7),"InjectedFailure",maxAttempts:2));
            Check("diagnostic_does_not_skip_to_later_event",outbox.Claim(access,space,now.AddMinutes(1)) is null);
            using(var c=db.Open())using(var cmd=c.CreateCommand())
            {cmd.CommandText="SELECT graph_checkpoint FROM namespace_state WHERE namespace_id=@n";cmd.Parameters.AddWithValue("@n",space);Check("failure_does_not_advance_checkpoint",Convert.ToInt64(cmd.ExecuteScalar())==0);}
            Check("explicit_retry_restores_delivery",outbox.RetryDiagnostic(access,space,first.EventId));
            var retried=outbox.Claim(access,space,now.AddMinutes(1))!;
            Check("successful_ack",outbox.Acknowledge(access,retried with {CommitSequence=long.MaxValue,Generation="forged"},now.AddMinutes(1).AddSeconds(1)));
            using(var c=db.Open())using(var cmd=c.CreateCommand())
            {cmd.CommandText="SELECT graph_checkpoint FROM namespace_state WHERE namespace_id=@n";cmd.Parameters.AddWithValue("@n",space);Check("checkpoint_uses_sql_metadata",Convert.ToInt64(cmd.ExecuteScalar())==retried.CommitSequence);}
            Check("duplicate_ack_no_effect",!outbox.Acknowledge(access,retried,now.AddMinutes(1).AddSeconds(1)));
            var second=outbox.Claim(access,space,now.AddMinutes(1).AddSeconds(2))!;
            Check("ordered_delivery",second.AggregateId=="b" && second.CommitSequence>first.CommitSequence);
            outbox.Acknowledge(access,second,now.AddMinutes(1).AddSeconds(3));
            var failed=false;
            try {facts.Put(access,space,new("bad","user","prefers",null,"茶",1,new Dictionary<string,string>(),[source with {Quote="forged"}]),now);}catch(InvalidDataException){failed=true;}
            Check("failed_fact_no_event",failed && outbox.Claim(access,space,now.AddMinutes(2)) is null);
            facts.Put(access,space,new("corrected","user","prefers",null,"白水",1,new Dictionary<string,string>(),[source]),now.AddSeconds(1),"a");
            var correction=outbox.Claim(access,space,now.AddMinutes(2))!;
            Check("correction_enqueues_old_revision",correction.AggregateId=="a" && correction.AggregateRevision==2);
            var otherAccess=KnowledgeAccessContext.ForSession("outbox","hutao","chatroom",[]);var other=otherAccess.MemoryNamespace;
            facts.RegisterEntity(otherAccess,other,"user","person","用户");
            var otherRef=evidence.Put(other,new("turn",text,"user","turn","turn"));
            facts.Put(otherAccess,other,new("other","user","prefers",null,"温水",1,new Dictionary<string,string>(),[source with {Reference=otherRef}]),DateTimeOffset.UtcNow);
            Check("namespace_queues_independent",outbox.Claim(otherAccess,other,now.AddMinutes(2))?.AggregateId=="other");
            var isolated=false;try {outbox.Claim(access,other,now);}catch(UnauthorizedAccessException){isolated=true;}
            Check("scope_enforced",isolated);
        }
        finally {Directory.Delete(root,true);}
        return results;
    }
}
