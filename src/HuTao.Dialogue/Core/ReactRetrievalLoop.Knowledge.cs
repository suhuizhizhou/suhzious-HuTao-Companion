using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Retrieval;

namespace HuTao.Dialogue.Core;

public sealed record KnowledgeRecallExecution(EvidenceBundle Bundle,IReadOnlyList<ConversationRecallStep> Steps);

public sealed partial class ReactRetrievalLoop
{
    public static async Task<KnowledgeRecallExecution> RunKnowledgeAsync(KnowledgeConversationContext context,
        IReadOnlyList<KnowledgeConversationTask> tasks,RecallBudget budget,CancellationToken ct=default,string? currentUserEvidenceId=null,SemanticPlanningCandidates? semanticCandidates=null)
    {
        KnowledgeConversationPlan.Validate(tasks,context,semanticCandidates);
        var completed=new Dictionary<string,EvidenceBundle>(StringComparer.Ordinal);
        var steps=new List<ConversationRecallStep>();
        while(completed.Count<tasks.Count)
        {
            var ready=tasks.Where(t=>!completed.ContainsKey(t.Id) && t.DependsOn.All(completed.ContainsKey)).ToArray();
            if(ready.Length==0)throw new InvalidDataException("Unresolvable knowledge dependencies.");
            foreach(var task in ready)
            {
                ct.ThrowIfCancellationRequested();
                var space=context.Sources[task.Source];var gaps=new List<string>();var seeds=new List<string>();
                if(task.DependsOn.Any(d=>completed[d].Evidence.Count==0 || !context.IsCurrent(completed[d],budget,ct)))
                {
                    var absent=new EvidenceBundle([],["dependency-evidence-missing"],new Dictionary<string,long>(),[],
                        [new(task.Id,[],[],["dependency-evidence-missing"])],new Dictionary<string,long>());
                    completed[task.Id]=absent;steps.Add(new(task.Source,task.Query,[],task.Id,task.Strategy.ToString(),task.DependsOn,absent.Gaps));continue;
                }
                if(task.EntityBindings.Any(binding=>binding.FromTask is not null &&
                    !completed[binding.FromTask].Evidence.Any(e=>e.Text.Contains(binding.Mention,StringComparison.Ordinal))))
                {
                    var absent=new EvidenceBundle([],["ungrounded-upstream-entity"],new Dictionary<string,long>(),[],
                        [new(task.Id,[],[],["ungrounded-upstream-entity"])],new Dictionary<string,long>());
                    completed[task.Id]=absent;steps.Add(new(task.Source,task.Query,[],task.Id,task.Strategy.ToString(),task.DependsOn,absent.Gaps));continue;
                }
                foreach(var binding in task.EntityBindings)
                {
                    if(task.Strategy is KnowledgeStrategy.SqlLexical or KnowledgeStrategy.SqlSemantic)continue;
                    if(!budget.ReserveQuery()) {gaps.Add("entity-resolution-budget-exhausted");break;}
                    var resolved=context.Entities.Resolve(context.Access,space,binding.Mention,
                        participants:context.Participants.GetValueOrDefault(task.Source),ct:ct,duration:budget.SqlReadRemaining);
                    if(resolved.Status!="resolved") {gaps.Add("entity-"+resolved.Status);continue;}
                    seeds.Add(resolved.Candidates.Single().Id);
                }
                var strategy=task.Strategy;
                if(strategy is KnowledgeStrategy.SqlFacts or KnowledgeStrategy.GraphExpand && seeds.Count==0)
                {strategy=KnowledgeStrategy.SqlLexical;gaps.Add("unresolved-entity-sql-lexical-fallback");}
                if(strategy==KnowledgeStrategy.SqlFacts && seeds.Count>1)
                {strategy=KnowledgeStrategy.SqlLexical;gaps.Add("multiple-fact-subjects-sql-lexical-fallback");}
                var predicate=task.Predicates.Count==1?task.Predicates[0]:null;
                var request=new KnowledgeStrategyRequest(new(task.Query),space,seeds.Count>0?new FactQuery(seeds[0],predicate):null,
                    seeds,task.Predicates,task.Source=="memory" && currentUserEvidenceId is not null?[currentUserEvidenceId]:null,
                    (semanticCandidates?.Items??[]).Where(e=>task.SelectedIds?.Contains(e.Reference.Id)==true).Select(e=>e.Reference).ToArray());
                var bundle=await context.Retriever.RetrieveAsync(context.Access,[new(task.Id,strategy,request)],budget,ct).ConfigureAwait(false);
                // Optional recovery must not consume the query + hydration + entity-resolution
                // allowance needed by the remaining planned tasks. Keep the shared hard limit.
                var remainingPlannedQueries=tasks.Where(t=>t.Id!=task.Id && !completed.ContainsKey(t.Id))
                    .Sum(t=>t.Strategy==KnowledgeStrategy.SqlSemantic?1:2+(t.Strategy==KnowledgeStrategy.SqlLexical?0:t.EntityBindings.Count));
                if(strategy==KnowledgeStrategy.SqlFacts && bundle.Evidence.Count==0 && request.SemanticReferences is {Count:>0} &&
                    context.Strategies.Contains(KnowledgeStrategy.SqlSemantic) && budget.QueriesRemaining>=remainingPlannedQueries+1 && context.IsCurrent(bundle,budget,ct))
                {
                    var failedTrace=bundle.Trace;strategy=KnowledgeStrategy.SqlSemantic;
                    bundle=await context.Retriever.RetrieveAsync(context.Access,[new(task.Id,strategy,request)],budget,ct).ConfigureAwait(false);
                    bundle=bundle with {Trace=failedTrace.Concat(["fact-miss-planned-semantic-fallback"]).Concat(bundle.Trace).ToArray()};
                }
                else if(strategy==KnowledgeStrategy.SqlFacts && bundle.Evidence.Count==0 && budget.QueriesRemaining>=remainingPlannedQueries+2 && context.IsCurrent(bundle,budget,ct))
                {
                    var failedTrace=bundle.Trace;
                    strategy=KnowledgeStrategy.SqlLexical;
                    bundle=await context.Retriever.RetrieveAsync(context.Access,[new(task.Id,strategy,request)],budget,ct).ConfigureAwait(false);
                    bundle=bundle with {Trace=failedTrace.Concat(["fact-miss-bounded-lexical-fallback"]).Concat(bundle.Trace).ToArray()};
                }
                else if(strategy==KnowledgeStrategy.SqlFacts && bundle.Evidence.Count==0 && remainingPlannedQueries>0)
                    bundle=bundle with {Trace=bundle.Trace.Append("fact-miss-fallback-deferred-for-planned-tasks").ToArray()};
                if(strategy==KnowledgeStrategy.SqlLexical && task.Source=="memory" && bundle.Evidence.Count==0 &&
                    request.SemanticReferences is {Count:>0} && context.Strategies.Contains(KnowledgeStrategy.SqlSemantic) && budget.QueriesRemaining>=remainingPlannedQueries+1 && context.IsCurrent(bundle,budget,ct))
                {
                    var failedTrace=bundle.Trace;strategy=KnowledgeStrategy.SqlSemantic;
                    bundle=await context.Retriever.RetrieveAsync(context.Access,[new(task.Id,strategy,request)],budget,ct).ConfigureAwait(false);
                    bundle=bundle with {Trace=failedTrace.Concat(["lexical-miss-bounded-semantic-fallback"]).Concat(bundle.Trace).ToArray()};
                }
                foreach(var required in task.RequiredFacts)
                    if(!bundle.Evidence.Any(e=>e.Text.Contains(required,StringComparison.OrdinalIgnoreCase)))gaps.Add("required-fact-unconfirmed:"+required);
                bundle=bundle with {Gaps=bundle.Gaps.Concat(gaps).Distinct().ToArray(),
                    Trace=bundle.Trace.Prepend($"task={task.Id};source={task.Source};requested={task.Strategy};actual={strategy}").ToArray(),
                    Tasks=bundle.Tasks?.Select(t=>t with {Gaps=t.Gaps.Concat(gaps).Distinct().ToArray()}).ToArray()};
                completed[task.Id]=bundle;
                steps.Add(new(task.Source,task.Query,bundle.Evidence.Select(e=>e.Reference.Id).ToArray(),task.Id,strategy.ToString(),task.DependsOn,bundle.Gaps));
            }
        }
        var all=tasks.Select(t=>completed[t.Id]).ToArray();
        var valid=all.All(bundle=>context.IsCurrent(bundle,budget,ct));
        var invalidReason=budget.Remaining<=TimeSpan.Zero?"knowledge-plan-deadline":"knowledge-changed-during-plan";
        var combined=new EvidenceBundle(valid?all.SelectMany(b=>b.Evidence).DistinctBy(e=>e.Reference).ToArray():[],
            all.SelectMany(b=>b.Gaps).Concat(valid?[]:[invalidReason]).Distinct().ToArray(),
            all.SelectMany(b=>b.PrivacyEpochs).GroupBy(x=>x.Key).ToDictionary(g=>g.Key,g=>g.First().Value),
            all.SelectMany(b=>b.Trace).ToArray(),
            all.SelectMany(b=>b.Tasks??[]).Select(t=>valid?t:t with {References=[],Paths=[],Gaps=t.Gaps.Append(invalidReason).ToArray()}).ToArray(),
            all.SelectMany(b=>b.CommitSequences??new Dictionary<string,long>()).GroupBy(x=>x.Key).ToDictionary(g=>g.Key,g=>g.First().Value));
        return new(combined,steps);
    }
}
