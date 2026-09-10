using HuTao.Agent.Core.Abstractions;

namespace HuTao.Agent.Core.Rag;

public enum StoryRoute { Bypass, Retrieve, Clarify, Playful, Comfort, Boundary }
public enum StoryStatus { Bypass, Answer, Tentative, Clarify, NotFound, Unavailable, Playful, Comfort, Boundary }
public enum StoryPerspective { Personal, Archive }

/// <summary>所有阈值在一个地方；分数是检索启发值，不是事实正确概率。</summary>
public sealed record StoryRagOptions
{
    public int CandidateLimit { get; init; } = 160;
    public int TopK { get; init; } = 5;
    public int WindowSize { get; init; } = 7;
    public int ContextCharacterBudget { get; init; } = 6000;
    public int CacheCapacity { get; init; } = 128;
    public bool EnablePersonalMemory { get; init; } = true;
    public bool EnableQueryExpansion { get; init; } = true;
    public double MinScore { get; init; } = 0.19;
    public double AnswerScore { get; init; } = 0.38;
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ColdStartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public void Validate()
    {
        if (CandidateLimit < TopK || TopK is < 1 or > 20 || WindowSize is < 1 or > 20 ||
            ContextCharacterBudget < 1000 || CacheCapacity < 0 ||
            MinScore < 0 || AnswerScore < MinScore || QueryTimeout <= TimeSpan.Zero || ColdStartTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(StoryRagOptions));
    }
}

public sealed record StoryQueryPlan(
    string Original, string SearchText, IReadOnlyList<string> Variants,
    IReadOnlyList<string> Entities, StoryRoute Route, bool IsQuote,
    bool IsSelf, bool IsFollowUp, string Reason);

public sealed record StoryEvidence(
    string Id, StoryDialogueLine Anchor, IReadOnlyList<StoryDialogueLine> Context,
    double Score, double Coverage, bool Exact, string MatchKind, StoryPerspective Perspective,
    string ContextKind);

public sealed record StoryCorpusStats(
    string Version, int SearchableLines, int MissingLines, int Files, IReadOnlyList<string> Warnings, int DuplicateIds = 0);

public sealed record StoryRagTrace(
    string CorpusVersion, double ElapsedMs, bool CacheHit, int Candidates,
    int ContextCharacters, string RetrievalMode, IReadOnlyList<string> Warnings);

public sealed record StoryRagResult(
    StoryQueryPlan Plan, StoryStatus Status, IReadOnlyList<StoryEvidence> Evidence,
    IReadOnlyList<StoryDocument> Background, StoryRagTrace Trace);

public interface IStoryRagService
{
    Task WarmupAsync(CancellationToken ct = default);
    Task<StoryRagResult> RetrieveAsync(string input, IReadOnlyList<ChatMessage>? history = null,
        CancellationToken ct = default);
}

public sealed record StoryAnswerSegment(string Text, string Emotion, IReadOnlyList<string> EvidenceIds, string Kind);
public sealed record StoryAnswerResult(string Reply, IReadOnlyList<StoryAnswerSegment> Segments,
    bool Validated, IReadOnlyList<string> Issues, string Path);
