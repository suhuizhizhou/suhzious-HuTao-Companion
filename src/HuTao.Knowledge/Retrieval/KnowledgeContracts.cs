using System.Collections.Frozen;
using System.Diagnostics;

namespace HuTao.Knowledge.Retrieval;

public sealed record EvidenceRef
{
    public string Namespace { get; }
    public string Id { get; }
    public long Revision { get; }
    public EvidenceRef(string @namespace, string id, long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        Namespace = @namespace; Id = id; Revision = revision;
    }
}

/// <summary>Created by the host, never deserialized from a model plan.</summary>
public sealed class KnowledgeAccessContext
{
    private readonly FrozenSet<string> _namespaces;
    public IReadOnlySet<string> Namespaces => _namespaces;
    public string MemoryNamespace { get; }
    private KnowledgeAccessContext(string memory, IEnumerable<string> corpora)
    { MemoryNamespace = memory; _namespaces = corpora.Append(memory).ToFrozenSet(StringComparer.Ordinal); }
    public static KnowledgeAccessContext ForSession(string user, string persona, string scope, IEnumerable<string> corpora)
    {
        foreach (var component in new[] { user, persona, scope })
            if (string.IsNullOrWhiteSpace(component) || component.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new ArgumentException("Invalid host identity component.");
        var allowed = corpora.ToArray();
        if (allowed.Any(c => !c.StartsWith("corpus:", StringComparison.Ordinal) || c.Length > 180))
            throw new ArgumentException("Only configured public corpora may be granted.");
        return new($"memory:{user}:{persona}:{scope}", allowed);
    }
    public void Demand(string space)
    { if (!_namespaces.Contains(space)) throw new UnauthorizedAccessException("Knowledge namespace is not granted."); }
}

public enum KnowledgeStrategy { LegacyLexical, SqlLexical, SqlFacts, GraphExpand, Hybrid, SqlSemantic }
public enum KnowledgeTimeMode { Current, Historical }
public sealed record KnowledgeQuery(string Text, KnowledgeTimeMode TimeMode = KnowledgeTimeMode.Current,
    DateTimeOffset? AsOf = null, DateTimeOffset? KnownAt = null);
public sealed record KnowledgeTask(string Id, KnowledgeStrategy Strategy, string Query,
    IReadOnlyList<string> DependsOn, IReadOnlyList<string> RequiredFacts);
public sealed class RecallBudget(int maxQueries = 6, int maxCharacters = 8000, TimeSpan? duration = null)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _queries;
    private int _characters;
    private readonly object _gate = new();
    public TimeSpan Remaining => (duration ?? TimeSpan.FromSeconds(2)) - _clock.Elapsed;
    public TimeSpan SqlReadRemaining
    {
        get {var remaining=Remaining;return remaining<TimeSpan.FromMilliseconds(300)?remaining:TimeSpan.FromMilliseconds(300);}
    }
    public int QueriesRemaining {get {lock(_gate)return Math.Max(0,maxQueries-_queries);}}
    public bool ReserveQuery() { lock (_gate) { if (Remaining <= TimeSpan.Zero || _queries >= maxQueries) return false; _queries++; return true; } }
    public bool ReserveCharacters(int count)
    { lock (_gate) { if (count < 0 || _characters + count > maxCharacters) return false; _characters += count; return true; } }
}
public sealed record KnowledgeCandidate(EvidenceRef Reference, string Channel, int Rank,
    IReadOnlyList<string>? AssertionIds = null);
public sealed record KnowledgeEvidence(EvidenceRef Reference, string Text, string Speaker, string SourceKind,
    string SourceId, string ChapterId, DateTimeOffset? ObservedAt = null,string MetadataJson="{}",DateTimeOffset? RecordedAt=null);
public sealed record EvidenceBundle(IReadOnlyList<KnowledgeEvidence> Evidence, IReadOnlyList<string> Gaps,
    IReadOnlyDictionary<string, long> PrivacyEpochs, IReadOnlyList<string> Trace,
    IReadOnlyList<KnowledgeTaskEvidence>? Tasks=null,IReadOnlyDictionary<string,long>? CommitSequences=null);
public sealed record KnowledgeTaskEvidence(string TaskId,IReadOnlyList<EvidenceRef> References,
    IReadOnlyList<KnowledgeFactPath> Paths,IReadOnlyList<string> Gaps);

public static class KnowledgePlanValidator
{
    public static void Validate(IReadOnlyList<KnowledgeTask> tasks)
    {
        if (tasks.Count > 4 || tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != tasks.Count)
            throw new ArgumentException("Too many or duplicate tasks.");
        var byId = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        int Depth(string id, HashSet<string> visiting)
        {
            if (!byId.TryGetValue(id, out var task) || !visiting.Add(id)) throw new ArgumentException("Missing or cyclic dependency.");
            if (!Enum.IsDefined(task.Strategy) || string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Query) || task.Query.Length > 4000)
                throw new ArgumentException("Invalid task.");
            var depth = 1 + task.DependsOn.Select(d => Depth(d, new(visiting, StringComparer.Ordinal))).DefaultIfEmpty(0).Max();
            return depth;
        }
        foreach (var task in tasks) if (Depth(task.Id, []) > 2) throw new ArgumentException("Dependency depth exceeded.");
    }
}
