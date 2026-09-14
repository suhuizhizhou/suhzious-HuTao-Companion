using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HuTao.Persona;

/// <summary>一条可以不经过 TTS、直接播放的游戏原声。</summary>
public sealed record OriginalVoiceClip(
    string Id,
    string Text,
    string AudioPath,
    string SourceFile,
    int DurationMs,
    bool Preferred);

/// <summary>
/// 从本地语音 manifest 建立原声目录。检索只负责给 LLM 提供少量候选；
/// 真正播放前仍会用 id + 完整台词做严格校验，防止模型改写原句或伪造音频。
/// </summary>
public sealed class OriginalVoiceCatalog
{
    private static readonly Regex Noise = new(
        @"[#{}]|NICKNAME|PLAYERAVATAR|SEXPRO|INFO_",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Punctuation = new(
        @"[\s，。！？、；：…—~～,.!?;:'‘’“”""「」『』（）()]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>清单条目一律按大小写无关读取，容忍不同导出工具的写法差异。</summary>
    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<char> StopCharacters =
        ['的', '了', '我', '你', '是', '有', '在', '吗', '呢', '吧', '啊', '呀', '和', '就', '都', '不', '很', '也'];

    private static readonly (string[] Query, string[] Voice)[] IntentTerms =
    [
        (["你好", "早安", "早上好", "在吗", "回来", "想你", "好久不见"],
         ["你好", "早安", "回来", "想我", "见到", "欢迎", "来啦"]),
        (["谢谢", "感谢"], ["谢谢", "感谢", "不用谢"]),
        (["对不起", "抱歉", "道歉"], ["对不起", "抱歉", "没关系", "不要紧"]),
        (["累", "困", "休息", "睡觉", "晚安"], ["累", "困", "休息", "睡", "晚安", "做梦"]),
        (["玩", "冒险", "出发"], ["玩", "冒险", "出发", "一起去"]),
        (["吃", "喝", "饿", "饭", "蛋糕", "饮料"], ["吃", "喝", "饿", "饭", "蛋糕", "饮料", "好吃"]),
        (["帮我", "帮忙", "怎么办", "可以吗"], ["交给我", "帮", "办法", "没问题"]),
        (["厉害", "真棒", "优秀", "赢了"], ["厉害", "真棒", "好耶", "赢"]),
        (["再见", "走了", "下次见"], ["再见", "下次", "要走", "回来"]),
        (["天气", "下雨", "打雷", "风", "太阳"], ["天气", "下雨", "打雷", "风", "太阳"]),
    ];

    private readonly Dictionary<string, OriginalVoiceClip> _clips;
    private readonly Dictionary<string, List<OriginalVoiceClip>> _byNormalizedText;

    private OriginalVoiceCatalog(IEnumerable<OriginalVoiceClip> clips)
    {
        _clips = clips.ToDictionary(clip => clip.Id, StringComparer.OrdinalIgnoreCase);
        // 逐字索引：RAG 命中某段剧情后，用台词原文反查角色本人有没有录过这一句。
        _byNormalizedText = [];
        foreach (var clip in _clips.Values)
        {
            var key = Normalize(clip.Text);
            if (key.Length < 2)
                continue;
            if (!_byNormalizedText.TryGetValue(key, out var bucket))
                _byNormalizedText[key] = bucket = [];
            bucket.Add(clip);
        }
    }

    public static OriginalVoiceCatalog Load(
        string manifestPath,
        IEnumerable<SignatureVoice>? preferredQuotes = null)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
            return new OriginalVoiceCatalog([]);

        var preferred = (preferredQuotes ?? [])
            .Select(quote => MakeKey(quote.SourceFile, quote.Text))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetDirectoryName(fullManifestPath)!;
        var clips = new List<OriginalVoiceClip>();

        foreach (var line in File.ReadLines(fullManifestPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            VoiceManifestEntry? item;
            try
            {
                // PropertyNameCaseInsensitive 只是兜底：PascalCase 的 `Id`/`Audio`/`Text`
                // 也能读进来。但 `DurationMs` 与 `duration_ms` 差在**下划线**，大小写无关
                // 匹配救不了——所以真正的约定是「导出方必须写 snake_case」，
                // 这一点由 tools/nte 的 schema 对齐检查守着。
                item = JsonSerializer.Deserialize<VoiceManifestEntry>(line, RelaxedJson);
            }
            catch (JsonException)
            {
                continue;
            }

            if (item is null || string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Text) || Noise.IsMatch(item.Text) ||
                IsParenthetical(item.Text) ||
                item.Text.Length is < 2 or > 80 || item.DurationMs is < 900 or > 15_000)
                continue;

            var audioPath = Path.GetFullPath(Path.Combine(root, item.Audio));
            if (!File.Exists(audioPath))
                continue;

            clips.Add(new OriginalVoiceClip(
                item.Id, item.Text.Trim(), audioPath, item.SourceFile,
                item.DurationMs, preferred.Contains(MakeKey(item.SourceFile, item.Text))));
        }

        return new OriginalVoiceCatalog(clips);
    }

    public bool HasEntries => _clips.Count > 0;

    /// <summary>全量片段，供台词库检索建立索引。</summary>
    public IReadOnlyCollection<OriginalVoiceClip> All => _clips.Values;

    /// <summary>按台词原文逐字反查原声。用于「RAG 检到角色本人的这句台词」这一最强信号。</summary>
    public IReadOnlyList<OriginalVoiceClip> FindByTexts(IEnumerable<string> texts, int maxCount = 4)
    {
        if (maxCount <= 0)
            return [];

        var result = new List<OriginalVoiceClip>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
        {
            var key = Normalize(text);
            if (key.Length < 2 || !_byNormalizedText.TryGetValue(key, out var bucket))
                continue;
            foreach (var clip in bucket)
            {
                if (result.Count >= maxCount)
                    return result;
                if (!seen.Add(clip.Id) || !File.Exists(clip.AudioPath))
                    continue;
                result.Add(clip);
            }
        }
        return result;
    }

    /// <summary>为当前问题挑出少量原声候选；无语义交集时返回空，避免强塞台词。</summary>
    public IReadOnlyList<OriginalVoiceClip> FindCandidates(string? query, int maxCount = 6)
    {
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0 || maxCount <= 0)
            return [];

        var queryBigrams = Bigrams(normalizedQuery);
        return _clips.Values
            .Select(clip => (Clip: clip, Score: Score(clip, normalizedQuery, queryBigrams)))
            .Where(item => item.Score >= 4)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Clip.DurationMs <= 6_000 ? 0 : 1)
            .ThenBy(item => item.Clip.DurationMs)
            .Take(maxCount)
            .Select(item => item.Clip)
            .ToList();
    }

    /// <summary>只有 id 存在、台词逐字一致且 WAV 仍存在时才允许直出原声。</summary>
    public OriginalVoiceClip? Resolve(string? id, string exactText)
    {
        if (string.IsNullOrWhiteSpace(id) || !_clips.TryGetValue(id, out var clip))
            return null;
        return string.Equals(clip.Text, exactText.Trim(), StringComparison.Ordinal) &&
               File.Exists(clip.AudioPath)
            ? clip
            : null;
    }

    /// <summary>模型省略 voice 标签但恰好逐字输出原句时，也可安全命中本地原声。</summary>
    public OriginalVoiceClip? ResolveExactText(string exactText)
        => _clips.Values.FirstOrDefault(clip =>
            string.Equals(clip.Text, exactText.Trim(), StringComparison.Ordinal) &&
            File.Exists(clip.AudioPath));

    private static int Score(
        OriginalVoiceClip clip,
        string normalizedQuery,
        IReadOnlySet<string> queryBigrams)
    {
        var normalizedText = Normalize(clip.Text);
        var score = 0;

        if (normalizedQuery.Length >= 2 && normalizedText.Contains(normalizedQuery, StringComparison.Ordinal))
            score += 14;

        foreach (var character in normalizedQuery.Distinct())
        {
            if (!StopCharacters.Contains(character) && normalizedText.Contains(character))
                score++;
        }

        var textBigrams = Bigrams(normalizedText);
        score += queryBigrams.Count(textBigrams.Contains) * 4;

        foreach (var (queryTerms, voiceTerms) in IntentTerms)
        {
            if (queryTerms.Any(term => normalizedQuery.Contains(term, StringComparison.Ordinal)) &&
                voiceTerms.Any(term => normalizedText.Contains(term, StringComparison.Ordinal)))
                score += 8;
        }

        if (score > 0 && clip.Preferred)
            score += 2;
        if (score > 0 && clip.DurationMs <= 6_000)
            score += 1;
        if (clip.Text.Length > 45)
            score -= 2;
        return score;
    }

    internal static string Normalize(string? text)
        => string.IsNullOrWhiteSpace(text) ? "" : Punctuation.Replace(text, "").ToLowerInvariant();

    private static HashSet<string> Bigrams(string value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < value.Length; i++)
            result.Add(value.Substring(i, 2));
        return result;
    }

    private static string MakeKey(string sourceFile, string text)
        => $"{sourceFile}\u001f{text.Trim()}";

    private static bool IsParenthetical(string text)
    {
        var value = text.Trim();
        return value.Length >= 2 &&
               ((value.StartsWith('（') && value.EndsWith('）')) ||
                (value.StartsWith('(') && value.EndsWith(')')));
    }
}

internal sealed class VoiceManifestEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("audio")] public string Audio { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("source_file")] public string SourceFile { get; set; } = "";
    [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }
}
