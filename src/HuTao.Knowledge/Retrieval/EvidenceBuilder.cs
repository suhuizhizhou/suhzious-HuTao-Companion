using HuTao.Knowledge.Storage;
using Microsoft.Data.Sqlite;

namespace HuTao.Knowledge.Retrieval;

internal sealed class EvidenceBuilder(SqlEvidenceRepository repository)
{
    public EvidenceBundle Build(KnowledgeAccessContext access,IReadOnlyList<KnowledgeStepResult> results,
        IReadOnlyDictionary<string,KnowledgeReadStamp> stamps,RecallBudget budget,CancellationToken ct)
    {
        var selected=new Dictionary<EvidenceRef,KnowledgeEvidence>();
        var tasks=new List<KnowledgeTaskEvidence>();
        var trace=results.SelectMany(x=>x.Result.Trace).Append("fusion="+CandidateFusion.Version).ToArray();
        // Share SQL hydration for equal time modes, while retaining per-task evidence and atomic fact paths.
        var hydration=new Dictionary<(KnowledgeTimeMode,DateTimeOffset?,DateTimeOffset?),Dictionary<EvidenceRef,KnowledgeEvidence>>();
        var hydrationGaps=new Dictionary<(KnowledgeTimeMode,DateTimeOffset?,DateTimeOffset?),List<string>>();
        foreach(var group in results.GroupBy(x=>(x.Step.Request.Query.TimeMode,x.Step.Request.Query.AsOf,x.Step.Request.Query.KnownAt)))
        {
            var allReferences=group.SelectMany(x=>x.Result.Candidates.Select(c=>c.Reference)
                .Concat(x.Result.Facts.SelectMany(f=>f.Sources.Select(s=>s.Reference))))
                .Distinct().ToArray();
            var references=allReferences.Take(200).ToArray();
            hydrationGaps[group.Key]=allReferences.Length>200?["hydration-candidate-limit"]:[];
            foreach(var reference in references)access.Demand(reference.Namespace);
            if(references.Length==0){hydration[group.Key]=[];continue;}
            if(!budget.ReserveQuery()) {hydration[group.Key]=[];hydrationGaps[group.Key].Add("sql-hydration-budget-exhausted");continue;}
            try {hydration[group.Key]=repository.ReadMany(access,references,group.First().Step.Request.Query,ct,budget.SqlReadRemaining).ToDictionary(x=>x.Reference);}
            catch(TimeoutException) {hydration[group.Key]=[];hydrationGaps[group.Key].Add("sql-hydration-deadline");}
            catch(SqliteException) {hydration[group.Key]=[];hydrationGaps[group.Key].Add("sql-hydration-unavailable");}
        }
        foreach(var item in results)
        {
            ct.ThrowIfCancellationRequested();
            var gaps=item.Result.Gaps.ToList();var refs=new HashSet<EvidenceRef>();var paths=new List<KnowledgeFactPath>();
            var query=item.Step.Request.Query;var available=hydration[(query.TimeMode,query.AsOf,query.KnownAt)];
            gaps.AddRange(hydrationGaps[(query.TimeMode,query.AsOf,query.KnownAt)]);
            bool Select(IReadOnlyList<EvidenceRef> references)
            {
                if(references.Any(r=>r.Namespace!=item.Step.Request.Namespace || !available.ContainsKey(r)))
                {gaps.Add("evidence-missing-or-invalid");return false;}
                var fresh=references.Distinct().Where(r=>!selected.ContainsKey(r)).Select(r=>available[r]).ToArray();
                if(budget.Remaining<=TimeSpan.Zero || !budget.ReserveCharacters(fresh.Sum(x=>x.Text.Length)))
                {gaps.Add("evidence-budget-exhausted");return false;}
                foreach(var row in fresh)selected[row.Reference]=row;
                refs.UnionWith(references);return true;
            }
            var factPaths=item.Result.Paths ?? item.Result.Facts.Select(f=>new KnowledgeFactPath(item.Step.Request.Namespace,[f])).ToArray();
            var scores=CandidateFusion.Fuse(item.Result.Candidates).ToDictionary(x=>x.Reference,x=>x.Score);
            foreach(var path in factPaths.OrderByDescending(x=>x.Facts.SelectMany(f=>f.Sources).Select(s=>scores.GetValueOrDefault(s.Reference)).DefaultIfEmpty().Average()).ThenByDescending(x=>x.Facts.Count))
            {
                if(path.Namespace!=item.Step.Request.Namespace || path.Facts.Count==0)throw new InvalidDataException("Invalid fact path scope.");
                var sources=path.Facts.SelectMany(x=>x.Sources).ToArray();
                if(path.Facts.Any(f=>f.Sources.Count==0) || sources.Any(s=>!available.TryGetValue(s.Reference,out var row) ||
                    s.StartUtf16<0 || s.EndUtf16<=s.StartUtf16 || s.EndUtf16>row.Text.Length || row.Text[s.StartUtf16..s.EndUtf16]!=s.Quote))
                {gaps.Add("path-source-invalid");continue;}
                if(Select(sources.Select(s=>s.Reference).Distinct().ToArray()))paths.Add(path);
                else gaps.Add("incomplete-path-omitted");
            }
            // Unsupported/incomplete paths do not become factual claims through flattened candidates.
            var lexical=item.Result.Candidates.Where(c=>c.AssertionIds is null || c.AssertionIds.Count==0);
            foreach(var candidate in CandidateFusion.Fuse(lexical))Select([candidate.Reference]);
            if(refs.Count==0)gaps.Add("no-usable-evidence");
            tasks.Add(new(item.Step.TaskId,refs.ToArray(),paths,gaps.Distinct().ToArray()));
        }
        var changed=false;
        try
        {
            var after=repository.ReadStamps(access,stamps.Keys,ct,budget.SqlReadRemaining);
            changed=stamps.Any(x=>!after.TryGetValue(x.Key,out var current) || x.Value!=current);
        }
        catch(Exception ex) when(ex is SqliteException or TimeoutException) {changed=true;}
        if(changed)
        {
            selected.Clear();
            tasks=tasks.Select(t=>t with {References=[],Paths=[],Gaps=t.Gaps.Append("knowledge-changed-during-retrieval").ToArray()}).ToList();
        }
        return new(selected.Values.ToArray(),tasks.SelectMany(x=>x.Gaps).Distinct().ToArray(),
            stamps.ToDictionary(x=>x.Key,x=>x.Value.PrivacyEpoch),trace,tasks,stamps.ToDictionary(x=>x.Key,x=>x.Value.CommitSequence));
    }
}
