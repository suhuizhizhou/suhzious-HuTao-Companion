using System.Collections.Frozen;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Retrieval;

namespace HuTao.Dialogue.Core;

/// <summary>Host-owned scope and participant binding. None of these values come from planner JSON.</summary>
public sealed class KnowledgeConversationContext
{
    public Func<string,CancellationToken,Task>? IngestCommittedUserTurnAsync {get;init;}
    public KnowledgeAccessContext Access {get;}
    public KnowledgeRetriever Retriever {get;}
    public EntityResolver Entities {get;}
    public IReadOnlyDictionary<string,string> Sources {get;}
    public IReadOnlyDictionary<string,ConversationParticipants> Participants {get;}
    public IReadOnlySet<KnowledgeStrategy> Strategies {get;}
    public KnowledgeConversationContext(KnowledgeAccessContext access,KnowledgeRetriever retriever,EntityResolver entities,
        IReadOnlyDictionary<string,string> sources,IReadOnlyDictionary<string,ConversationParticipants> participants,
        IEnumerable<KnowledgeStrategy> strategies)
    {
        foreach(var (source,space) in sources)
        {
            access.Demand(space);
            if(source is not ("memory" or "story") || source=="memory" && space!=access.MemoryNamespace || source=="story" && !space.StartsWith("corpus:",StringComparison.Ordinal))
                throw new ArgumentException("Invalid host source binding.");
        }
        Access=access;Retriever=retriever;Entities=entities;
        Sources=sources.ToFrozenDictionary(StringComparer.Ordinal);Participants=participants.ToFrozenDictionary(StringComparer.Ordinal);
        Strategies=strategies.ToFrozenSet();
        if(!Strategies.Contains(KnowledgeStrategy.SqlLexical))throw new ArgumentException("SQL lexical fallback is required.");
    }
    public bool IsCurrent(EvidenceBundle bundle,RecallBudget? budget=null,CancellationToken ct=default)=>Retriever.IsCurrent(Access,bundle,budget,ct);
    public EvidenceBundle PrivacyStamp()=>Retriever.PrivacyStamp(Access);
    public bool PrivacyIsCurrent(EvidenceBundle bundle)=>Retriever.PrivacyIsCurrent(Access,bundle);
}
