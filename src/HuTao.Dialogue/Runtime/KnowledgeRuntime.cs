using HuTao.Dialogue.Core;
using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using Neo4j.Driver;

namespace HuTao.Dialogue.Runtime;

/// <summary>One runtime-owned projection worker. SQLite remains authoritative and graph health never blocks construction.</summary>
public sealed class KnowledgeRuntime : IAsyncDisposable
{
    public SqliteKnowledgeDatabase Database {get;}
    public KnowledgeConversationContext Context {get;}
    public KnowledgeRuntimeMode Mode {get;}
    public string GraphHealth {get;private set;}="disabled";
    public IReadOnlyList<string> LastShadowTrace {get;private set;}=[];
    public string LastIngestionStatus {get;private set;}="idle";
    public bool WorkerStopped { get { lock(_lifecycle) return _worker.IsCompleted && _recovery.IsCompleted; } }
    private readonly IDriver? _driver;
    private readonly CancellationTokenSource _stop=new();
    private Task _worker=Task.CompletedTask;
    private Task _recovery=Task.CompletedTask;
    private readonly SemaphoreSlim _ingestionGate=new(1,1);
    private readonly object _lifecycle=new();
    private bool _activated;
    private readonly Action<string>? _log;
    private int _disposed;
    private Task? _disposeTask;
    private bool _workersDrained;
    private bool _driverReleased;
    private bool _stopReleased;
    private readonly List<Exception> _cleanupFailures=[];
    /// <summary>Historical cleanup errors remain inspectable after a successful resource retry.</summary>
    public IReadOnlyList<Exception> CleanupFailures { get { lock(_lifecycle) return _cleanupFailures.ToArray(); } }
    public KnowledgeRuntime(KnowledgeRuntimeOptions options,string persona,string scope,string? corpusDirectory,Action<string>? log=null,string? expectedLineageId=null,HuTao.Foundation.Abstractions.ILLMProvider? llm=null)
    {
        Mode=options.Mode;_log=log;
        if(Mode==KnowledgeRuntimeMode.Legacy)throw new ArgumentException("Legacy does not construct SQL runtime.");
        if(!File.Exists(options.DatabasePath))throw new InvalidOperationException("Knowledge database must be explicitly initialized or migrated before selecting a SQL/shadow mode.");
        Database=new(options.DatabasePath);
        if(expectedLineageId is not null && KnowledgeRecovery.ReadLineage(Database)!=expectedLineageId)
            throw new InvalidOperationException("Configured database lineage differs from this conversation's SQL authority.");
        KnowledgeRecovery.DemandActivationReady(Database);
        var corpus=corpusDirectory is not null && options.CorpusNamespaces?.TryGetValue(corpusDirectory.Replace('\\','/'),out var configured)==true?configured:null;
        if(corpusDirectory is not null && corpus is null)throw new InvalidOperationException("A SQL corpus binding is required for this character's corpus directory.");
        var access=KnowledgeAccessContext.ForSession(options.UserId,persona,scope,corpus is null?[]:[corpus]);
        Database.EnsureNamespace(access.MemoryNamespace);
        if(corpus is not null)
        {
            using var c=Database.Open();using var command=c.CreateCommand();
            command.CommandText="SELECT active_corpus_version FROM namespaces WHERE namespace_id=@n";command.Parameters.AddWithValue("@n",corpus);
            if(command.ExecuteScalar() is not string)throw new InvalidOperationException("Configured SQL corpus has no activated import.");
        }
        var evidence=new SqlEvidenceRepository(Database);var facts=new SqlAssertionRepository(Database);
        facts.RegisterEntity(access,access.MemoryNamespace,"user","person","用户");
        facts.RegisterEntity(access,access.MemoryNamespace,"character","character",persona);
        var strategies=new List<IKnowledgeRetrievalStrategy> {new SqlLexicalStrategy(evidence),new SqlFactsStrategy(facts)};
        if(llm is not null)strategies.Add(new SqlSemanticStrategy());
        if(Mode==KnowledgeRuntimeMode.SqlGraph && !string.IsNullOrWhiteSpace(options.GraphUri))
        {
            _driver=GraphDatabase.Driver(options.GraphUri,string.IsNullOrWhiteSpace(options.GraphUser)?AuthTokens.None:AuthTokens.Basic(options.GraphUser,options.GraphPassword??""),
                config=>config.WithConnectionTimeout(TimeSpan.FromSeconds(1)).WithMaxTransactionRetryTime(TimeSpan.Zero));
            strategies.Add(new GraphExpandStrategy(new(Database,_driver),facts));GraphHealth="starting";
        }
        else if(Mode==KnowledgeRuntimeMode.SqlGraph)GraphHealth="unconfigured-sql-fallback";
        var catalog=new KnowledgeStrategyCatalog(strategies);var sources=new Dictionary<string,string>{{"memory",access.MemoryNamespace}};
        if(corpus is not null)sources.Add("story",corpus);
        Context=new(access,new(catalog,evidence),new(Database),sources,new Dictionary<string,ConversationParticipants>{{"memory",new("user","character")}},catalog.Available)
        {IngestCommittedUserTurnAsync=IngestCommittedUserTurnAsync};
    }
    private async Task IngestCommittedUserTurnAsync(string id,CancellationToken ct)
    {
        if(Mode is not (KnowledgeRuntimeMode.Sql or KnowledgeRuntimeMode.SqlGraph))return;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct,_stop.Token);
        await _ingestionGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try{await IngestTurnCoreAsync(id,deadline.Token).ConfigureAwait(false);}
        finally{_ingestionGate.Release();}
    }
    private async Task IngestTurnCoreAsync(string id,CancellationToken ct)
    {
        var queue=new TurnIngestionQueue(Database);TurnIngestionLease? lease=null;
        var terminal="failed";
        try
        {
            EvidenceRef reference;
            using(var c=Database.Open())
            using(var command=c.CreateCommand())
            {
                command.CommandText="SELECT evidence_revision FROM memory_records WHERE namespace_id=@n AND memory_id=@id AND kind='Turn' AND archived=0";
                command.Parameters.AddWithValue("@n",Context.Access.MemoryNamespace);command.Parameters.AddWithValue("@id",id);
                var revision=command.ExecuteScalar();
                if(revision is null){LastIngestionStatus="source-unavailable";return;}
                reference=new(Context.Access.MemoryNamespace,id,Convert.ToInt64(revision));
            }
            var publisher=new ExtractionPublisher(Database);
            lease=queue.Claim(Context.Access,ct,reference);
            if(lease is null)
            {LastIngestionStatus=publisher.HasSourceDecision(Context.Access,reference)?"already-reviewed":"not-claimable";return;}
            var evidence=new SqlEvidenceRepository(Database).Read(Context.Access,reference);
            if(evidence is null || DeterministicAssertionRules.Extract(evidence,"user").Count==0)
            {LastIngestionStatus="no-deterministic-rule";terminal="deferred";return;}
            if(publisher.HasSourceDecision(Context.Access,reference)){LastIngestionStatus="already-reviewed";terminal="completed";return;}
            var result=await new ExtractionJobRunner(Database,new NoRemoteExtraction()).RunAsync(Context.Access,reference,
                [new("user","person","用户")],"user",new(maxCalls:1,maxSourceCharacters:200,maxReservedTokens:0),ct).ConfigureAwait(false);
            var selected=result.Validations.Where(v=>v.Status=="accepted" && v.Assertion is not null).Select(v=>v.Assertion!.Id).ToHashSet(StringComparer.Ordinal);
            if(publisher.HasDecision(Context.Access,result.JobId)){LastIngestionStatus="already-reviewed";terminal="completed";return;}
            Dictionary<string,string>? corrections=null;
            long? expectedSequence=null;
            if(DeterministicAssertionRules.IsExplicitCorrection(evidence.Text))
            {
                expectedSequence=new SqlEvidenceRepository(Database).ReadStamps(Context.Access,[reference.Namespace])[reference.Namespace].CommitSequence;
                corrections=new(StringComparer.Ordinal);
                foreach(var validation in result.Validations.Where(v=>v.Assertion is not null && selected.Contains(v.Assertion.Id)))
                {
                    var assertion=validation.Assertion!;
                    var dimension=assertion.Qualifiers.Where(p=>p.Key!="exclusive").ToDictionary(p=>p.Key,p=>p.Value);
                    var previous=new SqlAssertionRepository(Database).Query(Context.Access,reference.Namespace,new(assertion.SubjectId,assertion.PredicateId,Qualifiers:dimension));
                    if(previous.Count>1)throw new InvalidOperationException("Correction requires review of multiple existing facts.");
                    if(previous.SelectMany(f=>f.Sources).Any(s=>new SqlEvidenceRepository(Database).Read(Context.Access,s.Reference)?.ObservedAt is { } observed &&
                        (evidence.ObservedAt is null || observed>evidence.ObservedAt)))
                        throw new InvalidOperationException("Recovered correction predates the current fact source.");
                    if(previous.Count==1)corrections.Add(assertion.Id,previous[0].Id);
                }
            }
            var published=publisher.Publish(Context.Access,result.JobId,selected,"runtime:"+DeterministicAssertionRules.Version,ct:ct,supersedes:corrections,expectedCommitSequence:expectedSequence,ingestionLease:lease);
            terminal="completed";
            LastIngestionStatus="published:"+published.Assertions;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception ex)
        {LastIngestionStatus="failed:"+ex.GetType().Name;_log?.Invoke("[knowledge] turn extraction "+LastIngestionStatus);}
        finally{if(lease is not null && !ct.IsCancellationRequested)queue.Complete(Context.Access,lease,terminal);}
    }
    private sealed class NoRemoteExtraction:HuTao.Foundation.Abstractions.ILLMProvider
    {
        public string Name=>"runtime-deterministic-only";
        public Task<string> CompleteAsync(string systemPrompt,IReadOnlyList<HuTao.Foundation.Abstractions.ChatMessage> history,CancellationToken ct=default)
            =>throw new InvalidOperationException("Runtime deterministic ingestion cannot call a remote model.");
    }
    /// <summary>Start projection only after the host has successfully assembled and activated its runtime.</summary>
    public void Activate()
    {
        lock(_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed!=0,this);
            if(_activated)return;
            _activated=true;
            if(_driver is not null)_worker=Task.Run(RunWorkerAsync);
            if(Mode is KnowledgeRuntimeMode.Sql or KnowledgeRuntimeMode.SqlGraph)_recovery=Task.Run(RunRecoveryAsync);
        }
    }
    private async Task RunRecoveryAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            try
            {
                var ids=new TurnIngestionQueue(Database).ReadyIds(Context.Access,_stop.Token);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(5));
                foreach(var id in ids)await IngestCommittedUserTurnAsync(id,deadline.Token).ConfigureAwait(false);
            }
            catch(Exception ex) when(!_stop.IsCancellationRequested){_log?.Invoke("[knowledge] recovery: "+ex.GetType().Name);}
            catch(Exception) when(_stop.IsCancellationRequested){break;}
            try{await Task.Delay(TimeSpan.FromSeconds(2),_stop.Token).ConfigureAwait(false);}catch(OperationCanceledException){break;}
        }
    }
    private async Task RunWorkerAsync()
    {
        var projection=new Neo4jGraphProjection(_driver!);var worker=new GraphProjectionWorker(Database,projection);var initialized=false;
        while(!_stop.IsCancellationRequested)
        {
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(10));
                if(!initialized){await projection.InitializeAsync(deadline.Token).ConfigureAwait(false);initialized=true;}
                foreach(var space in Context.Access.Namespaces)
                    for(var count=0;count<32 && await worker.PublishOneAsync(Context.Access,space,deadline.Token).ConfigureAwait(false);count++) { }
                using var connection=Database.Open();using var command=connection.CreateCommand();
                command.CommandText="SELECT count(*) FROM graph_outbox WHERE namespace_id IN (SELECT value FROM json_each(@spaces)) AND delivered_at IS NULL";
                command.Parameters.AddWithValue("@spaces",System.Text.Json.JsonSerializer.Serialize(Context.Access.Namespaces));
                GraphHealth=Convert.ToInt64(command.ExecuteScalar())==0?"ready":"catching-up-sql-fallback";
            }
            catch(Exception ex) when(!_stop.IsCancellationRequested)
            {GraphHealth="unavailable-sql-fallback";_log?.Invoke("[knowledge] graph unavailable: "+ex.GetType().Name);}
            catch(Exception) when(_stop.IsCancellationRequested){break;}
            try{await Task.Delay(TimeSpan.FromSeconds(2),_stop.Token).ConfigureAwait(false);}catch(OperationCanceledException){break;}
        }
    }
    public async Task ObserveShadowAsync(string? input,CancellationToken ct)
    {
        if(Mode!=KnowledgeRuntimeMode.Shadow || string.IsNullOrWhiteSpace(input))return;
        var tasks=Context.Sources.Select((s,i)=>new KnowledgeRetrievalStep("shadow-"+i,KnowledgeStrategy.SqlLexical,new(new(input),s.Value))).ToArray();
        var result=await Context.Retriever.RetrieveAsync(Context.Access,tasks,new(),ct).ConfigureAwait(false);
        LastShadowTrace=result.Trace.Append("shadow-evidence-count="+result.Evidence.Count).Concat(result.Gaps).ToArray();
        _log?.Invoke("[knowledge] shadow: evidence="+result.Evidence.Count+"; gaps="+result.Gaps.Count);
    }
    public ValueTask DisposeAsync()
    {
        lock(_lifecycle)
        {
            if(_disposeTask is not null && !_disposeTask.IsFaulted)return new(_disposeTask);
            _disposed=1;
            _disposeTask=DisposeCoreAsync(Task.WhenAll(_worker,_recovery));
            return new(_disposeTask);
        }
    }
    private async Task DisposeCoreAsync(Task worker)
    {
        var failures=new List<Exception>();
        if(!_workersDrained)
        {
            try {_stop.Cancel();}catch(Exception ex){failures.Add(ex);}
            try {await worker.ConfigureAwait(false);}
            catch(OperationCanceledException) when(worker.IsCanceled) { }
            catch(Exception)
            {
                // WhenAll may contain multiple worker failures; awaiting it exposes only one.
                if(worker.Exception is { } errors)failures.AddRange(errors.Flatten().InnerExceptions);
            }
            _workersDrained=true;
        }
        if(!_driverReleased)
        {
            try
            {
                if(_driver is not null)await _driver.DisposeAsync().ConfigureAwait(false);
                _driverReleased=true;
            }
            catch(Exception ex){failures.Add(ex);}
        }
        if(!_stopReleased)
        {
            try {_stop.Dispose();_stopReleased=true;}
            catch(Exception ex){failures.Add(ex);}
        }
        lock(_lifecycle)_cleanupFailures.AddRange(failures);
        if(failures.Count>0)throw new AggregateException("Knowledge runtime cleanup failed.",failures);
    }
}
