namespace HuTao.Knowledge.Retrieval;

public sealed record SemanticPlanningCandidates(IReadOnlyList<KnowledgeEvidence> Items,int Queries,int Characters,IReadOnlyList<string> Gaps)
{
    public static SemanticPlanningCandidates Empty {get;}=new([],0,0,[]);
}
