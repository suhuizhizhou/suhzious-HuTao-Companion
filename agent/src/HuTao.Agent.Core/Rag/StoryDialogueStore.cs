using System.Text.Json;
using System.Text.Json.Serialization;

namespace HuTao.Agent.Core.Rag;

/// <summary>
/// 剧情逐句全文库。支持全局台词召回，也保留按章节搜索的降级入口。
/// </summary>
public sealed class StoryDialogueStore
{
    public string RootPath { get; }
    private const int VectorDimension = 4096;
    private static readonly IReadOnlyDictionary<int, double> EmptyIdf =
        new Dictionary<int, double>();
    private readonly string _chaptersRoot;
    private readonly string _pagesRoot;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _pageFilesByChapter;
    private readonly Lazy<StoryLineIndex> _globalIndex;

    public StoryDialogueStore(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        _chaptersRoot = Path.Combine(rootPath, "chapters");
        _pagesRoot = Path.Combine(rootPath, "pages");
        _pageFilesByChapter = LoadPageManifest(Path.Combine(_pagesRoot, "manifest.json"));
        _globalIndex = new Lazy<StoryLineIndex>(
            () => StoryLineIndex.Load(_chaptersRoot),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// 在全库逐句检索。用户通常不会提供章节名，而是描述记得的某句台词，
    /// 因此这一入口不能先依赖章节摘要。检索完成后再按命中行回读同一段对话上下文。
    /// </summary>
    public IReadOnlyList<DialogueSearchHit> SearchGlobal(
        string query,
        int topK = 5,
        int windowSize = 8,
        double minScore = 0.16)
    {
        if (string.IsNullOrWhiteSpace(query) || !Directory.Exists(_chaptersRoot))
            return [];

        return _globalIndex.Value.Search(query, topK, windowSize, minScore);
    }

    public IReadOnlyList<DialogueSearchHit> Search(
        string query,
        IEnumerable<string> chapterIds,
        int topK = 3,
        int windowSize = 8,
        int overlap = 3)
    {
        if (string.IsNullOrWhiteSpace(query) || !Directory.Exists(_chaptersRoot))
            return [];

        var queryVector = StoryVectorizer.Vectorize(query, VectorDimension, EmptyIdf);
        var candidates = new List<DialogueSearchHit>();
        foreach (var chapterId in chapterIds.Distinct(StringComparer.Ordinal))
        {
            var numericId = chapterId.StartsWith("main-quest-", StringComparison.Ordinal)
                ? chapterId["main-quest-".Length..]
                : chapterId;
            if (!numericId.All(char.IsDigit))
                continue;
            var path = Path.Combine(_chaptersRoot, numericId + ".jsonl");
            if (!File.Exists(path))
                continue;

            var lines = ReadLines(path);
            foreach (var group in lines.GroupBy(line => line.SubquestIndex))
            {
                var ordered = group.OrderBy(line => line.Sequence).ThenBy(line => line.Variant).ToList();
                var step = Math.Max(1, windowSize - Math.Clamp(overlap, 0, windowSize - 1));
                for (var start = 0; start < ordered.Count; start += step)
                {
                    var window = ordered.Skip(start).Take(windowSize).ToList();
                    if (window.Count == 0 || window.All(line => string.IsNullOrWhiteSpace(line.Text)))
                        continue;
                    var searchable = string.Join('\n', window.Select(line =>
                        $"{line.Speaker} {line.Text}"));
                    var vector = StoryVectorizer.Vectorize(searchable, VectorDimension, EmptyIdf);
                    var score = Dot(queryVector, vector) + SpeakerBonus(query, window);
                    candidates.Add(new DialogueSearchHit(chapterId, score, window));
                    if (start + windowSize >= ordered.Count)
                        break;
                }
            }
        }

        return candidates
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.ChapterId, StringComparer.Ordinal)
            .Take(Math.Max(1, topK))
            .ToList();
    }

    /// <summary>
    /// 搜索完整任务页面的补充逐句档案。该层不覆盖 TextMap 行号，结果单独标注来源。
    /// </summary>
    public IReadOnlyList<DialogueSearchHit> SearchSupplemental(
        string query,
        IEnumerable<string> chapterIds,
        int topK = 2,
        int windowSize = 8,
        int overlap = 3)
    {
        if (string.IsNullOrWhiteSpace(query) || !Directory.Exists(_pagesRoot))
            return [];

        var selectedChapters = chapterIds.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var pageFiles = selectedChapters
            .Where(_pageFilesByChapter.ContainsKey)
            .SelectMany(chapterId => _pageFilesByChapter[chapterId])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (pageFiles.Count == 0)
            return [];

        var queryVector = StoryVectorizer.Vectorize(query, VectorDimension, EmptyIdf);
        var candidates = new List<DialogueSearchHit>();
        var step = Math.Max(1, windowSize - Math.Clamp(overlap, 0, windowSize - 1));
        foreach (var relativePath in pageFiles)
        {
            var path = Path.Combine(_pagesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var page = ReadArchivePage(path);
            if (page is null)
                continue;
            var chapterId = page.ChapterIds.FirstOrDefault(selectedChapters.Contains) ?? page.ChapterIds.FirstOrDefault() ?? "";
            var lines = page.Lines
                .Where(line => !string.IsNullOrWhiteSpace(line.Text))
                .Select(line => new StoryDialogueLine
                {
                    ChapterId = chapterId,
                    Sequence = line.Sequence,
                    Speaker = line.Speaker,
                    Text = line.Text,
                    RawText = line.RawText,
                    Resolved = true,
                    SourceFile = $"pages/records/{page.PageId}.json",
                    EvidenceKind = "supplemental_page",
                    SourcePageId = page.PageId,
                    SourcePageName = page.Name,
                })
                .ToList();
            for (var start = 0; start < lines.Count; start += step)
            {
                var window = lines.Skip(start).Take(windowSize).ToList();
                if (window.Count == 0)
                    continue;
                var searchable = string.Join('\n', window.Select(line => $"{line.Speaker} {line.Text}"));
                var vector = StoryVectorizer.Vectorize(searchable, VectorDimension, EmptyIdf);
                var score = Dot(queryVector, vector) + SpeakerBonus(query, window);
                candidates.Add(new DialogueSearchHit(chapterId, score, window));
                if (start + windowSize >= lines.Count)
                    break;
            }
        }

        return candidates
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Lines[0].SourcePageId, StringComparer.Ordinal)
            .Take(Math.Max(1, topK))
            .ToList();
    }

    internal static List<StoryDialogueLine> ReadLines(string path)
    {
        var result = new List<StoryDialogueLine>();
        foreach (var json in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(json))
                continue;
            var line = JsonSerializer.Deserialize<StoryDialogueLine>(json, JsonOptions);
            if (line is not null)
                result.Add(line);
        }
        return result;
    }

    private static StoryArchivePage? ReadArchivePage(string path)
    {
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<StoryArchivePage>(File.ReadAllText(path), JsonOptions);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadPageManifest(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var manifest = JsonSerializer.Deserialize<StoryPageManifest>(File.ReadAllText(path), JsonOptions);
        if (manifest is null)
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        return manifest.Pages
            .Where(page => page.DialogueLineCount > 0)
            .SelectMany(page => page.ChapterIds.Select(chapterId => (chapterId, page.File)))
            .GroupBy(item => item.chapterId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(item => item.File).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
    }

    private static double Dot(
        IReadOnlyDictionary<int, double> left,
        IReadOnlyDictionary<int, double> right)
    {
        var sum = 0d;
        foreach (var (key, value) in left)
            if (right.TryGetValue(key, out var other))
                sum += value * other;
        return sum;
    }

    private static double SpeakerBonus(string query, IEnumerable<StoryDialogueLine> lines) =>
        lines.Select(line => line.Speaker)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Count(name => query.Contains(name, StringComparison.OrdinalIgnoreCase)) * 0.08;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

public sealed class StoryDialogueLine
{
    [JsonPropertyName("chapter_id")] public string ChapterId { get; init; } = "";
    [JsonPropertyName("main_id")] public int MainId { get; init; }
    [JsonPropertyName("main_title")] public string MainTitle { get; init; } = "";
    [JsonPropertyName("main_description")] public string MainDescription { get; init; } = "";
    [JsonPropertyName("chapter_title")] public string ChapterTitle { get; init; } = "";
    [JsonPropertyName("chapter_number")] public string ChapterNumber { get; init; } = "";
    [JsonPropertyName("subquest_index")] public int SubquestIndex { get; init; }
    [JsonPropertyName("subquest_id")] public int SubquestId { get; init; }
    [JsonPropertyName("subquest_title")] public string SubquestTitle { get; init; } = "";
    [JsonPropertyName("talk_id")] public int TalkId { get; init; }
    [JsonPropertyName("sequence")] public int Sequence { get; init; }
    [JsonPropertyName("variant")] public int Variant { get; init; }
    [JsonPropertyName("line_id")] public long LineId { get; init; }
    [JsonPropertyName("next_line_ids")] public List<long> NextLineIds { get; init; } = [];
    [JsonPropertyName("speaker_type")] public string SpeakerType { get; init; } = "";
    [JsonPropertyName("speaker_id")] public int SpeakerId { get; init; }
    [JsonPropertyName("speaker")] public string Speaker { get; init; } = "";
    [JsonPropertyName("text_hash")] public long TextHash { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("raw_text")] public string RawText { get; init; } = "";
    [JsonPropertyName("resolved")] public bool Resolved { get; init; }
    [JsonPropertyName("source_file")] public string SourceFile { get; init; } = "";
    [JsonPropertyName("evidence_kind")] public string EvidenceKind { get; init; } = "textmap";
    [JsonPropertyName("source_page_id")] public string SourcePageId { get; init; } = "";
    [JsonPropertyName("source_page_name")] public string SourcePageName { get; init; } = "";
    [JsonIgnore] public string EvidenceId => EvidenceKind switch
    {
        "supplemental_page" => $"archive:{SourcePageId}:{Sequence}",
        "character_story" => $"character:hutao:{TextHash}:{Sequence}",
        _ => $"textmap:{ChapterId}:{SubquestIndex}:{LineId}:{Variant}",
    };
    [JsonIgnore] public string SceneKey => $"{EvidenceKind}:{ChapterId}:{SourcePageId}:{SubquestIndex}:{TalkId}:{(EvidenceKind == "character_story" ? TextHash : 0)}";
}

public sealed record DialogueSearchHit(
    string ChapterId,
    double Score,
    IReadOnlyList<StoryDialogueLine> Lines,
    long MatchedLineId = 0,
    string MatchKind = "chapter-window");

internal sealed class StoryPageManifest
{
    [JsonPropertyName("pages")] public List<StoryPageManifestEntry> Pages { get; init; } = [];
}

internal sealed class StoryPageManifestEntry
{
    [JsonPropertyName("chapter_ids")] public List<string> ChapterIds { get; init; } = [];
    [JsonPropertyName("file")] public string File { get; init; } = "";
    [JsonPropertyName("dialogue_line_count")] public int DialogueLineCount { get; init; }
}

internal sealed class StoryArchivePage
{
    [JsonPropertyName("page_id")] public string PageId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("chapter_ids")] public List<string> ChapterIds { get; init; } = [];
    [JsonPropertyName("lines")] public List<StoryArchiveLine> Lines { get; init; } = [];
}

internal sealed class StoryArchiveLine
{
    [JsonPropertyName("sequence")] public int Sequence { get; init; }
    [JsonPropertyName("speaker")] public string Speaker { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("raw_text")] public string RawText { get; init; } = "";
}
