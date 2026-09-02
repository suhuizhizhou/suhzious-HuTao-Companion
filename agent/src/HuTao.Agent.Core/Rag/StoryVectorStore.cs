using System.Text.Json;
using System.Text.Json.Serialization;

namespace HuTao.Agent.Core.Rag;

/// <summary>只读的本地剧情向量索引。索引由 scripts/build_story_index.py 离线生成。</summary>
public sealed class StoryVectorStore
{
    private const int ExpectedVersion = 1;
    private readonly StoryIndex _index;

    private StoryVectorStore(StoryIndex index) => _index = index;

    public int Count => _index.Documents.Count;

    public static StoryVectorStore Load(string indexPath)
    {
        if (!File.Exists(indexPath))
            return new StoryVectorStore(new StoryIndex());

        var json = File.ReadAllText(indexPath);
        var index = JsonSerializer.Deserialize<StoryIndex>(json, JsonOptions) ?? new StoryIndex();
        if (index.Version != ExpectedVersion)
            throw new InvalidDataException($"不支持的剧情索引版本：{index.Version}");
        if (index.Dimension <= 0)
            throw new InvalidDataException("剧情索引维度必须大于 0。");
        return new StoryVectorStore(index);
    }

    public IReadOnlyList<StorySearchHit> Search(string query, int topK = 3, double minScore = 0.12)
    {
        if (string.IsNullOrWhiteSpace(query) || _index.Documents.Count == 0)
            return [];

        var queryVector = StoryVectorizer.Vectorize(query, _index.Dimension, _index.Idf);
        if (queryVector.Count == 0)
            return [];

        return _index.Documents
            .Select(document => new StorySearchHit(
                document,
                Dot(queryVector, document.Vector) + MetadataBonus(query, document)))
            .Where(hit => hit.Score >= minScore)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Document.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, topK))
            .ToList();
    }

    private static double MetadataBonus(string query, StoryDocument document)
    {
        // 章节摘要中的角色名并不总会重复出现。对明确点名的人物做轻量重排，
        // 避免“胡桃的传说任务”被其他标题中含“传说”的记录挤到后面。
        var characterMatches = document.Characters.Count(name =>
            name.Length > 0 && query.Contains(name, StringComparison.OrdinalIgnoreCase));
        var titleMatch = document.Title.Length >= 3 &&
                         query.Contains(document.Title, StringComparison.OrdinalIgnoreCase);
        var regionMatch = document.Region != "提瓦特" && document.Region.Length > 0 &&
                          query.Contains(document.Region, StringComparison.OrdinalIgnoreCase);
        return characterMatches * 0.12 + (titleMatch ? 0.08 : 0) + (regionMatch ? 0.03 : 0);
    }

    private static double Dot(IReadOnlyDictionary<int, double> left, IReadOnlyDictionary<int, double> right)
    {
        var small = left.Count <= right.Count ? left : right;
        var large = ReferenceEquals(small, left) ? right : left;
        var sum = 0d;
        foreach (var (key, value) in small)
            if (large.TryGetValue(key, out var other))
                sum += value * other;
        return sum;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

public sealed class StoryIndex
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("dimension")] public int Dimension { get; init; } = 4096;
    [JsonPropertyName("generated_at")] public string GeneratedAt { get; init; } = "";
    [JsonPropertyName("idf")] public Dictionary<int, double> Idf { get; init; } = [];
    [JsonPropertyName("documents")] public List<StoryDocument> Documents { get; init; } = [];
}

public sealed class StoryDocument
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("scope")] public string Scope { get; init; } = "";
    [JsonPropertyName("region")] public string Region { get; init; } = "";
    [JsonPropertyName("chapter")] public string Chapter { get; init; } = "";
    [JsonPropertyName("act")] public string Act { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
    [JsonPropertyName("key_events")] public List<string> KeyEvents { get; init; } = [];
    [JsonPropertyName("characters")] public List<string> Characters { get; init; } = [];
    [JsonPropertyName("keywords")] public List<string> Keywords { get; init; } = [];
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("coverage")] public string Coverage { get; init; } = "";
    [JsonPropertyName("vector")] public Dictionary<int, double> Vector { get; init; } = [];
}

public sealed record StorySearchHit(StoryDocument Document, double Score);

internal static class StoryVectorizer
{
    public static Dictionary<int, double> Vectorize(
        string text, int dimension, IReadOnlyDictionary<int, double> idf)
    {
        var counts = new Dictionary<int, double>();
        foreach (var token in Tokens(text))
        {
            var bucket = (int)(Fnv1a(token) % (uint)dimension);
            counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
        }

        var norm = 0d;
        foreach (var key in counts.Keys.ToList())
        {
            var weight = (1d + Math.Log(counts[key])) * idf.GetValueOrDefault(key, 1d);
            counts[key] = weight;
            norm += weight * weight;
        }

        if (norm <= 0)
            return counts;
        norm = Math.Sqrt(norm);
        foreach (var key in counts.Keys.ToList())
            counts[key] /= norm;
        return counts;
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var normalized = new string(text.Trim().ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || IsCjk(ch) || char.IsWhiteSpace(ch))
            .ToArray());

        foreach (var word in normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (word.Any(ch => !IsCjk(ch)))
                yield return "w:" + word;

        var cjk = new string(normalized.Where(IsCjk).ToArray());
        for (var n = 1; n <= 3; n++)
            for (var i = 0; i + n <= cjk.Length; i++)
                yield return $"c{n}:" + cjk.Substring(i, n);
    }

    private static bool IsCjk(char ch) => ch is >= '\u3400' and <= '\u9fff';

    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }
}
