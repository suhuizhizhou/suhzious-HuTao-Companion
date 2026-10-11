using System.Text.Json;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Memory;

namespace HuTao.Dialogue.Core;

public sealed record KnowledgeEntityBinding(string Mention,string? FromTask=null);
public sealed record KnowledgeConversationTask(string Id,string Source,KnowledgeStrategy Strategy,string Query,
    IReadOnlyList<string> RequiredFacts,IReadOnlyList<string> DependsOn,IReadOnlyList<KnowledgeEntityBinding> EntityBindings,
    IReadOnlyList<string> Predicates,IReadOnlyList<string>? SelectedIds=null);

internal static class KnowledgeConversationPlan
{
    private static readonly HashSet<string> FailureCodes=["response-purpose","fields","response-size","strategy","intent-size","semantic-ids","semantic-source","source","required-facts","entity-bindings","predicates","structured-bindings","selection-limit"];
    private static InvalidDataException Invalid(string code)
    {
        var error=new InvalidDataException("Invalid knowledge plan.");error.Data["knowledge-plan-code"]=code;return error;
    }
    public static string FailureCode(Exception error)=>error.Data["knowledge-plan-code"] is string code && FailureCodes.Contains(code)?code:error switch
    {
        JsonException=>"json-syntax",KeyNotFoundException=>"required-field",InvalidOperationException=>"json-type",
        ArgumentException=>"plan-shape",OperationCanceledException=>"planning-timeout",HttpRequestException=>"provider-http",
        _=>"unclassified"
    };
    private static void Fields(JsonElement row,params string[] allowed)
    {
        if(row.ValueKind!=JsonValueKind.Object || row.EnumerateObject().Any(p=>!allowed.Contains(p.Name)) ||
            row.EnumerateObject().Select(p=>p.Name).Distinct().Count()!=row.EnumerateObject().Count())
            throw Invalid("fields");
    }
    private static string[] Strings(JsonElement row,string name)=>row.TryGetProperty(name,out var value)?value.EnumerateArray().Select(x=>x.GetString()??"").ToArray():[];
    public static (string Intent,IReadOnlyList<KnowledgeConversationTask> Tasks,IReadOnlyList<string> Diagnostics,ConversationResponsePurpose ResponsePurpose) Parse(string raw,KnowledgeConversationContext context,SemanticPlanningCandidates? candidates=null)
    {
        if(raw.Length>32000)throw Invalid("response-size");
        using var document=JsonDocument.Parse(raw);var root=document.RootElement;
        Fields(root,"intent","tasks","response_purpose");
        var purpose=ConversationResponsePurpose.Unspecified;
        if(root.TryGetProperty("response_purpose",out var purposeValue))
            purpose=purposeValue.GetString() switch
            {
                "factual"=>ConversationResponsePurpose.Factual,
                "comfort"=>ConversationResponsePurpose.Comfort,
                "conversation"=>ConversationResponsePurpose.Conversation,
                _=>throw Invalid("response-purpose")
            };
        var tasks=new List<KnowledgeConversationTask>();
        foreach(var row in root.GetProperty("tasks").EnumerateArray())
        {
            Fields(row,"id","source","strategy","query","required_facts","depends_on","entity_bindings","predicates","selected_ids");
            var strategyName=row.GetProperty("strategy").GetString();
            var strategy=context.Strategies.Cast<KnowledgeStrategy?>().SingleOrDefault(x=>x.ToString()==strategyName)
                ??throw Invalid("strategy");
            var bindings=new List<KnowledgeEntityBinding>();
            if(row.TryGetProperty("entity_bindings",out var entityBindings))
                foreach(var binding in entityBindings.EnumerateArray())
                {
                    Fields(binding,"mention","from_task");
                    bindings.Add(new(binding.GetProperty("mention").GetString()??"",binding.TryGetProperty("from_task",out var parent)?parent.GetString():null));
                }
            tasks.Add(new(row.GetProperty("id").GetString()??"",row.GetProperty("source").GetString()??"",strategy,
                row.GetProperty("query").GetString()??"",Strings(row,"required_facts"),Strings(row,"depends_on"),bindings,Strings(row,"predicates"),Strings(row,"selected_ids")));
        }
        // Check every scope, ID, field, dependency and bound before permitting a weaker read
        // strategy. Never infer entity bindings or choose one predicate from an ambiguous set.
        Validate(tasks,context,candidates,permitReadFallback:true);
        var diagnostics=new List<string>();
        for(var i=0;i<tasks.Count;i++)
        {
            var task=tasks[i];
            if(!NeedsStructuredBindings(task))continue;
            var replacement=task.Source=="memory" && task.SelectedIds is {Count:>0} && context.Strategies.Contains(KnowledgeStrategy.SqlSemantic)
                ?KnowledgeStrategy.SqlSemantic:KnowledgeStrategy.SqlLexical;
            diagnostics.Add($"planner-adjustment=structured-read-fallback;task-index={i};from={task.Strategy};to={replacement}");
            tasks[i]=task with {Strategy=replacement,EntityBindings=[],Predicates=[]};
        }
        Validate(tasks,context,candidates);
        var intent=root.TryGetProperty("intent",out var value)?value.GetString()??"":"";
        if(intent.Length>500)throw Invalid("intent-size");
        return (intent,tasks,diagnostics,purpose);
    }
    private static bool NeedsStructuredBindings(KnowledgeConversationTask task)=>
        task.Strategy==KnowledgeStrategy.SqlFacts && (task.EntityBindings.Count!=1 || task.Predicates.Count!=1) ||
        task.Strategy==KnowledgeStrategy.GraphExpand && (task.EntityBindings.Count==0 || task.Predicates.Count==0);
    public static void Validate(IReadOnlyList<KnowledgeConversationTask> tasks,KnowledgeConversationContext context,SemanticPlanningCandidates? candidates=null,bool permitReadFallback=false)
    {
        KnowledgePlanValidator.Validate(tasks.Select(t=>new KnowledgeTask(t.Id,t.Strategy,t.Query,t.DependsOn,t.RequiredFacts)).ToArray());
        foreach(var task in tasks)
        {
            var ids=task.SelectedIds??[];
            try{MemoryRelevanceSelector.ValidateIds(ids,(candidates?.Items??[]).Select(e=>e.Reference.Id));}
            catch(InvalidDataException){throw Invalid("semantic-ids");}
            if((ids.Count>0 || task.Strategy==KnowledgeStrategy.SqlSemantic) && task.Source!="memory")throw Invalid("semantic-source");
            if(!context.Sources.ContainsKey(task.Source))throw Invalid("source");
            if(!context.Strategies.Contains(task.Strategy))throw Invalid("strategy");
            if(task.RequiredFacts.Count>8 || task.RequiredFacts.Any(f=>string.IsNullOrWhiteSpace(f) || f.Length>200))throw Invalid("required-facts");
            if(task.EntityBindings.Count>4 || task.EntityBindings.Any(b=>string.IsNullOrWhiteSpace(b.Mention) || b.Mention.Length>100 ||
                b.FromTask is not null && !task.DependsOn.Contains(b.FromTask)))throw Invalid("entity-bindings");
            if(task.Predicates.Count>6 || task.Predicates.Any(p=>!PredicateCatalog.Entries.ContainsKey(p)))throw Invalid("predicates");
            if(!permitReadFallback && NeedsStructuredBindings(task))
                throw Invalid("structured-bindings");
        }
        if(tasks.SelectMany(t=>t.SelectedIds??[]).Distinct(StringComparer.Ordinal).Count()>4)
            throw Invalid("selection-limit");
    }
}
