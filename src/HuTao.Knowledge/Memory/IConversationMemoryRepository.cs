namespace HuTao.Knowledge.Memory;

public sealed record MemoryRepositorySnapshot(MemoryFileState State, long Revision)
{
    public IReadOnlySet<string> ArchivedIds {get;init;}=new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string,long> EvidenceRevisions {get;init;}=new Dictionary<string,long>(StringComparer.Ordinal);
}
public sealed record MemoryConsolidationRun(string InputHash, string Model, string PromptVersion);

/// <summary>A single authoritative backend; stale writers must reload rather than overwrite.</summary>
public interface IConversationMemoryRepository
{
    MemoryRepositorySnapshot Load();
    long Save(MemoryFileState state, long expectedRevision, MemoryConsolidationRun? run = null);
    bool HasCommitted(MemoryConsolidationRun run);
}
