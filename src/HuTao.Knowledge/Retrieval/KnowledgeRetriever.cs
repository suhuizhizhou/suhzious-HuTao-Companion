using HuTao.Knowledge.Storage;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;

namespace HuTao.Knowledge.Retrieval;

public sealed record KnowledgeRetrievalStep(string TaskId,KnowledgeStrategy Strategy,KnowledgeStrategyRequest Request);
internal sealed record KnowledgeStepResult(KnowledgeRetrievalStep Step,KnowledgeStrategyResult Result);

/// <summary>One shared budget and authoritative read boundary for every requested source/strategy.</summary>
public sealed class KnowledgeRetriever(KnowledgeStrategyCatalog catalog,SqlEvidenceRepository evidence)
{
    public SemanticPlanningCandidates PrepareSemanticPlanning(KnowledgeAccessContext access,string? currentUserEvidenceId,CancellationToken ct)
    {
        try
        {
            var rows=evidence.PersonalSelectionCandidates(access,currentUserEvidenceId is null?[]:[currentUserEvidenceId],ct:ct);
            var selected=new List<KnowledgeEvidence>();var characters=0;
            foreach(var row in rows)
            {
                var excerpt=row.Text.Length>400?row.Text[..400]:row.Text;
                if(characters+excerpt.Length>4000)continue;
                selected.Add(row with {Text=excerpt});characters+=excerpt.Length;
            }
            return new(selected,1,characters,[]);
        }
        catch(Exception ex) when(ex is SqliteException or TimeoutException)
        {return new([],1,0,["semantic-planning-sql-unavailable"]);}
    }
    public EvidenceBundle PrivacyStamp(KnowledgeAccessContext access)=>new([],[],evidence.PrivacyEpochs(access),[]);
    public async Task<EvidenceBundle> RetrieveAsync(KnowledgeAccessContext access,IReadOnlyList<KnowledgeRetrievalStep> steps,
        RecallBudget budget,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(steps.Count>4 || steps.Any(s=>string.IsNullOrWhiteSpace(s.TaskId)) ||
            steps.GroupBy(s=>s.TaskId).Any(g=>g.Select(s=>(s.Request.Namespace,s.Request.Query.TimeMode,s.Request.Query.AsOf,s.Request.Query.KnownAt)).Distinct().Count()!=1))
            throw new ArgumentException("Invalid retrieval steps.");
        foreach(var step in steps)
        {
            access.Demand(step.Request.Namespace);
            var query=step.Request.Query;
            if(!Enum.IsDefined(query.TimeMode) || query.TimeMode==KnowledgeTimeMode.Current && (query.AsOf is not null || query.KnownAt is not null))
                throw new ArgumentException("Invalid retrieval time mode.");
        }
        IReadOnlyDictionary<string,KnowledgeReadStamp> stamps;
        try {stamps=evidence.ReadStamps(access,steps.Select(s=>s.Request.Namespace),ct,budget.SqlReadRemaining);}
        catch(TimeoutException) {return new([], budget.Remaining<=TimeSpan.Zero?["sql-read-deadline","query-budget-exhausted"]:["sql-read-deadline"],new Dictionary<string,long>(),["backend=sqlite;read-deadline"]);}
        catch(SqliteException) {return new([], ["sql-unavailable"],new Dictionary<string,long>(),["backend=unavailable"]);}
        var results=new List<KnowledgeStepResult>();
        foreach(var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            var strategy=catalog.Demand(step.Strategy);
            KnowledgeStrategyResult result;
            var strategyClock=Stopwatch.StartNew();
            try {result=await strategy.RetrieveAsync(access,step.Request,budget,ct).ConfigureAwait(false);}
            catch(TimeoutException)
            {result=new([],[],["sql-read-deadline"],["strategy="+step.Strategy+";backend=sqlite;read-deadline"]);}
            catch(SqliteException)
            {result=new([],[],["sql-unavailable"],["strategy="+step.Strategy+";backend=unavailable"]);}
            result=result with {Trace=result.Trace.Append("timing;phase=strategy-total;strategy="+step.Strategy+
                ";elapsed_ms="+strategyClock.Elapsed.TotalMilliseconds.ToString("F3",CultureInfo.InvariantCulture)).ToArray()};
            results.Add(new(step,result));
        }
        var merged=results.GroupBy(x=>x.Step.TaskId).Select(group=>new KnowledgeStepResult(group.First().Step,
            new(group.SelectMany(x=>x.Result.Candidates).ToArray(),group.SelectMany(x=>x.Result.Facts).DistinctBy(x=>(x.Id,x.Revision)).ToArray(),
                group.SelectMany(x=>x.Result.Gaps).Distinct().ToArray(),group.SelectMany(x=>x.Result.Trace).ToArray(),
                group.SelectMany(x=>x.Result.Paths ?? x.Result.Facts.Select(f=>new KnowledgeFactPath(x.Step.Request.Namespace,[f])).ToArray()).ToArray()))).ToArray();
        var buildClock=Stopwatch.StartNew();
        var bundle=new EvidenceBuilder(evidence).Build(access,merged,stamps,budget,ct);
        return bundle with {Trace=bundle.Trace.Append("timing;phase=evidence-build;elapsed_ms="+
            buildClock.Elapsed.TotalMilliseconds.ToString("F3",CultureInfo.InvariantCulture)).ToArray()};
    }

    public bool IsCurrent(KnowledgeAccessContext access,EvidenceBundle bundle,RecallBudget? budget=null,CancellationToken ct=default)
    {
        if(bundle.CommitSequences is null)return false;
        try
        {
            var now=evidence.ReadStamps(access,bundle.CommitSequences.Keys,ct,budget?.SqlReadRemaining);
            return now.All(x=>bundle.CommitSequences[x.Key]==x.Value.CommitSequence &&
                bundle.PrivacyEpochs.TryGetValue(x.Key,out var epoch) && epoch==x.Value.PrivacyEpoch);
        }
        catch(Exception ex) when(ex is SqliteException or TimeoutException) {return false;}
    }

    public bool PrivacyIsCurrent(KnowledgeAccessContext access,EvidenceBundle bundle)
    {
        try
        {
            var now=evidence.ReadStamps(access,bundle.PrivacyEpochs.Keys);
            return now.All(x=>bundle.PrivacyEpochs[x.Key]==x.Value.PrivacyEpoch);
        }
        catch(Exception ex) when(ex is SqliteException or TimeoutException) {return false;}
    }
}
