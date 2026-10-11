using System.Diagnostics;
using HuTao.Knowledge.Storage;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Graph;
using Microsoft.Data.Sqlite;

internal static class KnowledgeSqlReadChecks
{
    public static List<CheckResult> Run()
    {
        var results=new List<CheckResult>();
        void Check(string name,bool passed,string detail="")=>results.Add(new("knowledge_read_"+name,passed,detail));
        var directory=Path.Combine(Path.GetTempPath(),"hutao-read-deadline-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var db=new SqliteKnowledgeDatabase(Path.Combine(directory,"test.db"));
            const string slow="WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000000) SELECT sum(x) FROM n";
            static long Scalar(SqliteConnection connection,string sql)
            {using var command=connection.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
            var watch=Stopwatch.StartNew();var deadline=false;
            try{db.Read(c=>Scalar(c,slow),duration:TimeSpan.FromMilliseconds(80));}
            catch(TimeoutException){deadline=true;}
            Check("interrupts_computing_vm",deadline && watch.Elapsed<TimeSpan.FromSeconds(2),$"elapsed_ms={watch.Elapsed.TotalMilliseconds:F1}");
            using(var cancelled=new CancellationTokenSource())
            {
                cancelled.CancelAfter(80);watch.Restart();var stopped=false;
                try{db.Read(c=>Scalar(c,slow),cancelled.Token,TimeSpan.FromSeconds(5));}
                catch(OperationCanceledException ex){stopped=ex.CancellationToken==cancelled.Token;}
                Check("caller_cancellation_interrupts_vm",stopped && watch.Elapsed<TimeSpan.FromSeconds(2),$"elapsed_ms={watch.Elapsed.TotalMilliseconds:F1}");
            }
            Check("next_connection_not_interrupted",db.Read(c=>Scalar(c,"SELECT 42"))==42);
            var refused=false;
            try{db.Read(c=>Scalar(c,"CREATE TABLE forbidden(value TEXT)"));}
            catch(SqliteException ex){refused=ex.SqliteErrorCode==8;}
            Check("connection_is_read_only",refused);
            // DELETE journal + exclusive lock genuinely blocks readers, unlike a normal WAL writer.
            using(var blocker=db.Open())
            {
                using var command=blocker.CreateCommand();
                command.CommandText="PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE";command.ExecuteNonQuery();
                try
                {
                    watch.Restart();var expired=false;
                    try{db.Read(c=>Scalar(c,"SELECT count(*) FROM schema_migrations"),duration:TimeSpan.FromMilliseconds(80));}
                    catch(TimeoutException){expired=true;}
                    Check("lock_wait_native_deadline",expired && watch.Elapsed<TimeSpan.FromMilliseconds(300),$"elapsed_ms={watch.Elapsed.TotalMilliseconds:F1}");
                    using var cancelLocked=new CancellationTokenSource(80);watch.Restart();var cancelledLock=false;
                    try{db.Read(c=>Scalar(c,"SELECT count(*) FROM schema_migrations"),cancelLocked.Token,TimeSpan.FromSeconds(5));}
                    catch(OperationCanceledException ex){cancelledLock=ex.CancellationToken==cancelLocked.Token;}
                    Check("lock_wait_preserves_caller_cancellation",cancelledLock && watch.Elapsed<TimeSpan.FromMilliseconds(300),$"elapsed_ms={watch.Elapsed.TotalMilliseconds:F1}");
                    var access=KnowledgeAccessContext.ForSession("lock-test","hutao","desktop",[]);
                    var resolved=new EntityResolver(db).Resolve(access,access.MemoryNamespace,"合成人名");
                    Check("entity_timeout_is_not_unknown",resolved.Status=="deadline" && resolved.Candidates.Count==0);
                    var evidence=new SqlEvidenceRepository(db);
                    var retriever=new KnowledgeRetriever(new([new SqlLexicalStrategy(evidence)]),evidence);
                    var bundle=retriever.RetrieveAsync(access,[new("locked",KnowledgeStrategy.SqlLexical,new(new("合成记忆"),access.MemoryNamespace))],new()).GetAwaiter().GetResult();
                    Check("retrieval_timeout_returns_explicit_gap",bundle.Evidence.Count==0 && bundle.Gaps.Contains("sql-read-deadline"));
                    var shared=new RecallBudget(duration:TimeSpan.FromMilliseconds(200));
                    Thread.Sleep(130);watch.Restart();
                    var shortened=retriever.RetrieveAsync(access,[new("remaining",KnowledgeStrategy.SqlLexical,new(new("合成记忆"),access.MemoryNamespace))],shared).GetAwaiter().GetResult();
                    Check("initial_stamp_uses_remaining_plan_time",shortened.Gaps.Contains("sql-read-deadline") && watch.Elapsed<TimeSpan.FromMilliseconds(200),$"elapsed_ms={watch.Elapsed.TotalMilliseconds:F1}");
                    watch.Restart();
                    var repeated=retriever.RetrieveAsync(access,[new("expired",KnowledgeStrategy.SqlLexical,new(new("合成记忆"),access.MemoryNamespace))],shared).GetAwaiter().GetResult();
                    Check("next_task_does_not_reset_expired_budget",repeated.Evidence.Count==0 && repeated.Gaps.Contains("sql-read-deadline") && watch.Elapsed<TimeSpan.FromMilliseconds(80),$"elapsed_ms={watch.Elapsed.TotalMilliseconds:F1}");
                }
                finally{command.CommandText="ROLLBACK";command.ExecuteNonQuery();}
            }
            Check("lock_release_allows_read",db.Read(c=>Scalar(c,"SELECT count(*) FROM schema_migrations"))==11);
            using(var contender=db.Open())
            {
                SQLitePCL.raw.sqlite3_busy_timeout(contender.Handle,0);
                var held=db.Read(c=>
                {
                    using(var transaction=c.BeginTransaction(deferred:true))
                    {
                        _=Scalar(c,"SELECT count(*) FROM schema_migrations");
                        transaction.Commit();
                    }
                    var code=SQLitePCL.raw.sqlite3_exec(contender.Handle,"BEGIN EXCLUSIVE");
                    if(code==SQLitePCL.raw.SQLITE_OK)SQLitePCL.raw.sqlite3_exec(contender.Handle,"ROLLBACK");
                    return code==SQLitePCL.raw.SQLITE_BUSY;
                });
                Check("snapshot_stays_pinned_after_managed_read_commit",held);
                var released=SQLitePCL.raw.sqlite3_exec(contender.Handle,"BEGIN EXCLUSIVE");
                if(released==SQLitePCL.raw.SQLITE_OK)SQLitePCL.raw.sqlite3_exec(contender.Handle,"ROLLBACK");
                Check("snapshot_released_before_return_to_caller",released==SQLitePCL.raw.SQLITE_OK);
            }
        }
        finally{Directory.Delete(directory,true);}
        return results;
    }
}
