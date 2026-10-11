using System.Text.Json;
using HuTao.Knowledge.Memory;
using HuTao.Knowledge.Rag;
using HuTao.Knowledge.Retrieval;
using HuTao.Persona;

namespace HuTao.Dialogue.Core;

internal static class KnowledgeResultAdapter
{
    internal static (IReadOnlyList<MemoryHit> Memories,ReactRetrievalResult? Story) Adapt(EvidenceBundle bundle,
        string input,IReadOnlyList<HuTao.Foundation.Abstractions.ChatMessage> history,StoryLexicon lexicon,
        IReadOnlyList<ConversationRecallStep> steps)
    {
        var memories=new List<MemoryHit>();var stories=new List<StoryEvidence>();var gaps=bundle.Gaps.ToList();
        foreach(var evidence in bundle.Evidence)
        {
            if(evidence.Reference.Namespace.StartsWith("memory:",StringComparison.Ordinal))
            {
                var record=new MemoryRecord {Id=evidence.Reference.Id,Text=evidence.Text,Speaker=evidence.Speaker,
                    ObservedAt=evidence.ObservedAt??DateTimeOffset.UnixEpoch,ObservationTimeKnown=evidence.ObservedAt is not null,
                    RecordedAt=evidence.RecordedAt??DateTimeOffset.UnixEpoch,Kind=MemoryKind.Turn,
                    Fingerprint=ConversationMemoryStore.Fingerprint(evidence.Speaker,evidence.Text)};
                memories.Add(new(record,1d/(60+memories.Count+1),0,0,0,false,"sql-evidence;revision="+evidence.Reference.Revision));
            }
            else
            {
                StoryDialogueLine? line;
                try {line=JsonSerializer.Deserialize<StoryDialogueLine>(evidence.MetadataJson);}catch(JsonException){line=null;}
                if(line is null || line.EvidenceId!=evidence.Reference.Id || line.Text!=evidence.Text || line.Speaker!=evidence.Speaker)
                {gaps.Add("story-metadata-invalid");continue;}
                stories.Add(new(line.EvidenceId,line,[],1d/(60+stories.Count+1),0,false,"sql-hydrated",
                    lexicon.SelfName==line.Speaker?StoryPerspective.Personal:StoryPerspective.Archive,"sql-source"));
            }
        }
        if(bundle.Evidence.Count==0 && steps.Count==0)return ([],null);
        var plan=StoryQueryAnalyzer.Plan(input,lexicon,history);
        var supported=memories.Count+stories.Count>0;
        var factual=bundle.Tasks?.Any(t=>t.Paths.Count>0)==true && gaps.Count==0;
        var result=new StoryRagResult(plan,supported?(factual?StoryStatus.Answer:StoryStatus.Tentative):StoryStatus.NotFound,stories,[],
            new("sql-authority",0,false,bundle.Evidence.Count,bundle.Evidence.Sum(e=>e.Text.Length),string.Join(';',bundle.Trace),
                gaps.Select(g=>"证据边界："+g).ToArray())){ConversationMemories=memories,Knowledge=bundle};
        var retrievalSteps=steps.Select((s,i)=>new ReactRetrievalStep(i+1,s.Query,result.Status,s.EvidenceIds.Count,0,false,
            s.NamedStrategy??"SqlLexical",TaskId:s.TaskId??"",DependsOn:s.DependsOn,RequiredFacts:[],StrategySource:"knowledge-plan")).ToArray();
        return (memories,new(result,result,[result],retrievalSteps,string.Join('\n',bundle.Trace.Concat(bundle.Gaps))));
    }
}
