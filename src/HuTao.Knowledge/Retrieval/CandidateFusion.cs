namespace HuTao.Knowledge.Retrieval;

public sealed record FusedKnowledgeCandidate(EvidenceRef Reference,double Score);
public static class CandidateFusion
{
    public const string Version="rrf-60-distinct-channel-v1";
    public static IReadOnlyList<FusedKnowledgeCandidate> Fuse(IEnumerable<KnowledgeCandidate> candidates)=>candidates
        .Where(c=>c.Rank>0 && !string.IsNullOrWhiteSpace(c.Channel))
        .GroupBy(c=>c.Reference)
        .Select(g=>new FusedKnowledgeCandidate(g.Key,g.GroupBy(c=>c.Channel,StringComparer.Ordinal).Sum(channel=>1d/(60+channel.Min(c=>c.Rank)))))
        .OrderByDescending(c=>c.Score).ThenBy(c=>c.Reference.Namespace,StringComparer.Ordinal)
        .ThenBy(c=>c.Reference.Id,StringComparer.Ordinal).ThenBy(c=>c.Reference.Revision).ToArray();
}
