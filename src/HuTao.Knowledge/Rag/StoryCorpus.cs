using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HuTao.Persona;

namespace HuTao.Knowledge.Rag;

/// <summary>唯一语料入口。派生索引不替代原文件，坏文件隔离并进入诊断信息。</summary>
internal sealed class StoryCorpus
{
    public List<StoryDialogueLine> Lines { get; } = [];
    public required StoryCorpusStats Stats { get; init; }

    /// <summary>
    /// 载入语料。
    ///
    /// <paramref name="lexicon"/> 决定「角色个人档案」这个来源读哪个文件——
    /// 原来是写死的 <c>character/hutao.json</c> 与章节名 <c>character-hutao</c>，
    /// 于是这套语料只能配胡桃。现在由 <see cref="StoryLexicon.CharacterStoryFile"/> 指定，
    /// 没配就没有这个来源（而不是去读一个别人的档案）。
    /// </summary>
    public static StoryCorpus Load(string dialogueRoot, StoryLexicon? lexicon = null)
    {
        lexicon ??= StoryLexicon.Empty;
        var lines = new List<StoryDialogueLine>();
        var warnings = new List<string>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = 0;
        var missing = 0;
        void Read(string path, Action<string> parse)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(dialogueRoot, path).Replace('\\', '/')));
                hash.AppendData(bytes);
                files++;
                parse(Encoding.UTF8.GetString(bytes));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException
                or InvalidOperationException or KeyNotFoundException or FormatException)
            { warnings.Add($"{Path.GetFileName(path)}: {ex.GetType().Name}"); }
        }
        var chapters = Path.Combine(dialogueRoot, "chapters");
        if (Directory.Exists(chapters))
            foreach (var file in Directory.EnumerateFiles(chapters, "*.jsonl").Order(StringComparer.Ordinal))
                Read(file, json =>
                {
                    foreach (var row in json.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            var line = JsonSerializer.Deserialize<StoryDialogueLine>(row);
                            if (line is null) continue;
                            if (!line.Resolved || string.IsNullOrWhiteSpace(line.Text)) { missing++; continue; }
                            lines.Add(line);
                        }
                        catch (JsonException) { warnings.Add($"{Path.GetFileName(file)}: invalid row"); }
                    }
                });
        var pages = Path.Combine(dialogueRoot, "pages", "records");
        if (Directory.Exists(pages))
            foreach (var file in Directory.EnumerateFiles(pages, "*.json").Order(StringComparer.Ordinal))
                Read(file, json =>
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("lines", out var rows) || rows.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("Page lines must be an array");
                    var pageId = root.GetProperty("page_id").GetString() ?? "";
                    var title = root.GetProperty("name").GetString() ?? "";
                    var chapter = root.TryGetProperty("chapter_ids", out var chapters) && chapters.ValueKind == JsonValueKind.Array
                        ? chapters.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()).FirstOrDefault()
                        : null;
                    chapter ??= "archive-page-" + pageId; // 未标章节的页面不能冒充已知主线。
                    foreach (var row in rows.EnumerateArray())
                    {
                        // 任务目标、页面标题不能冒充对话；没有可靠分支图的页面只返回单行证据。
                        if (!row.TryGetProperty("kind", out var kind) || kind.GetString() != "dialogue") continue;
                        if (!row.TryGetProperty("text", out var body) || body.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(body.GetString())) { missing++; continue; }
                        lines.Add(new StoryDialogueLine
                        {
                            ChapterId = chapter,
                            MainTitle = title,
                            Sequence = row.GetProperty("sequence").GetInt32(),
                            Speaker = row.GetProperty("speaker").GetString() ?? "",
                            Text = row.GetProperty("text").GetString() ?? "",
                            RawText = row.GetProperty("raw_text").GetString() ?? "",
                            Resolved = true,
                            EvidenceKind = "supplemental_page",
                            SourcePageId = pageId,
                            SourcePageName = title,
                            SourceFile = $"pages/records/{pageId}.json"
                        });
                    }
                });
        // 角色个人档案：路径由词表指定。没配就没有这个来源——绝不去读别人的档案。
        var storyFile = lexicon.CharacterStoryFile;
        var memory = string.IsNullOrWhiteSpace(storyFile)
            ? ""
            : Path.Combine(Path.GetDirectoryName(dialogueRoot)!, storyFile.Replace('/', Path.DirectorySeparatorChar));
        if (memory.Length > 0 && File.Exists(memory)) Read(memory, json =>
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var story in doc.RootElement.GetProperty("stories").EnumerateArray())
            {
                var key = long.Parse(story.GetProperty("text_hash").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                var title = story.GetProperty("title").GetString() ?? "";
                var sequence = 0;
                foreach (var paragraph in story.GetProperty("paragraphs").EnumerateArray())
                {
                    var text = paragraph.GetString() ?? "";
                    lines.Add(new StoryDialogueLine
                    {
                        ChapterId = string.IsNullOrWhiteSpace(lexicon.CharacterChapterId)
                            ? "character-story" : lexicon.CharacterChapterId,
                        MainTitle = title,
                        Sequence = sequence++,
                        TextHash = key,
                        Text = text,
                        RawText = text,
                        Resolved = true,
                        EvidenceKind = "character_story",
                        SourceFile = storyFile,
                        Speaker = "角色资料叙述"
                    });
                }
            }
        });
        var unique = lines.DistinctBy(line => line.EvidenceId).ToArray();
        foreach (var group in lines.GroupBy(l => l.EvidenceId).Where(g => g.Select(l => l.Text).Distinct().Skip(1).Any()))
            warnings.Add("Conflicting duplicate source ID: " + group.Key);
        var corpus = new StoryCorpus
        {
            Stats = new StoryCorpusStats(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            unique.Length, missing, files, warnings.AsReadOnly(), lines.Count - unique.Length)
        };
        corpus.Lines.AddRange(unique);
        return corpus;
    }
}
