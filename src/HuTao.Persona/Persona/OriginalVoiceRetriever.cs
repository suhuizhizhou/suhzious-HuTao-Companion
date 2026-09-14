namespace HuTao.Persona;

/// <summary>原声候选的来源层级，决定提示词里的优先级与措辞。</summary>
public enum OriginalVoiceTier
{
    /// <summary>本轮 RAG 检索到的剧情台词，逐字命中了角色本人录过的原声——最强信号。</summary>
    RagExact,
    /// <summary>从角色全部原声构成的台词库里按话题检索命中。</summary>
    VoiceLibrary,
    /// <summary>按用户这句话的意图/字面重合命中（原有逻辑）。</summary>
    Intent,
}

public sealed record OriginalVoiceCandidate(
    OriginalVoiceClip Clip,
    OriginalVoiceTier Tier,
    double Score);

/// <summary>
/// 一轮原声召回的输入。
/// <paramref name="EvidenceTexts"/> 必须是**本角色本人**在本轮剧情证据里说过的台词，
/// 由调用方先按说话人过滤——否则会把别的角色的台词错配成本角色的原声。
/// </summary>
public sealed record OriginalVoiceQuery(
    string? UserInput,
    IReadOnlyList<string> EvidenceTexts,
    IReadOnlyList<string> RecentUtterances,
    bool AllowLibrary = true,
    int MaxCandidates = 8);

/// <summary>
/// 原声召回器：把「用户问什么」升级成「这一轮到底在聊什么」。
///
/// 原先只拿用户原话去比对台词字面，命中门槛高且只覆盖 10 组手写意图，
/// 导致近半数原声（语料里查无此句的待机/任务语音）永远选不上。
/// 这里改成三路融合：
///   1) RAG 逐字直连——检索到的角色台词若能对上原声，直接作为最高优先级候选；
///   2) 台词库 BM25——用「用户原话 + 本轮检索到的场景文本 + 近期对话」联合检索全部原声；
///   3) 原有意图匹配——保住已经工作的那部分行为不变。
/// 三路都只产出**候选**，最终能否播放仍由 SpeechSegmentParser 的逐字校验兜底。
/// </summary>
public sealed class OriginalVoiceRetriever
{
    private const double MinLibraryCoverage = 0.08;
    private const int MinLibraryGrams = 3;
    private const int MaxLibraryCandidates = 3;
    private const int MaxExactCandidates = 3;
    private const int MaxIntentCandidates = 3;

    private readonly OriginalVoiceCatalog _catalog;
    private readonly Lazy<Library> _library;

    public OriginalVoiceRetriever(OriginalVoiceCatalog catalog)
    {
        _catalog = catalog;
        _library = new Lazy<Library>(() => new Library(catalog.All), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<OriginalVoiceCandidate> Retrieve(OriginalVoiceQuery query)
    {
        if (!_catalog.HasEntries || query.MaxCandidates <= 0)
            return [];

        var picked = new List<OriginalVoiceCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(OriginalVoiceClip clip, OriginalVoiceTier tier, double score)
        {
            if (picked.Count >= query.MaxCandidates || !seen.Add(clip.Id))
                return;
            picked.Add(new OriginalVoiceCandidate(clip, tier, score));
        }

        // 1) RAG 逐字直连：证据里就有角色本人说过的这句，连贯性天然成立。
        foreach (var clip in _catalog.FindByTexts(query.EvidenceTexts, MaxExactCandidates))
            Add(clip, OriginalVoiceTier.RagExact, 1.0);

        // 2) 台词库 BM25：用整轮上下文检索，救回语料里查不到的那半数原声。
        if (query.AllowLibrary)
        {
            foreach (var hit in _library.Value.Search(query, MinLibraryCoverage, MinLibraryGrams, MaxLibraryCandidates))
                Add(hit.Clip, OriginalVoiceTier.VoiceLibrary, hit.Coverage);
        }

        // 3) 原有意图匹配：保持不变，避免新逻辑把已经能用的场景弄丢。
        foreach (var clip in _catalog.FindCandidates(query.UserInput, MaxIntentCandidates))
            Add(clip, OriginalVoiceTier.Intent, 0.5);

        return picked;
    }

    /// <summary>角色全部原声的字面 BM25 索引；只读、进程内构建一次。</summary>
    private sealed class Library
    {
        private const double K1 = 1.2;
        private const double B = 0.75;

        private readonly OriginalVoiceClip[] _clips;
        private readonly int[] _lengths;
        private readonly Dictionary<string, List<Posting>> _postings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _idf = new(StringComparer.Ordinal);
        private readonly double _averageLength;

        public Library(IReadOnlyCollection<OriginalVoiceClip> clips)
        {
            _clips = clips
                .Where(clip => File.Exists(clip.AudioPath))
                .OrderBy(clip => clip.Id, StringComparer.Ordinal)
                .ToArray();
            _lengths = new int[_clips.Length];

            var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < _clips.Length; i++)
            {
                var grams = Grams(OriginalVoiceCatalog.Normalize(_clips[i].Text));
                _lengths[i] = Math.Max(1, grams.Count);
                foreach (var group in grams.GroupBy(gram => gram, StringComparer.Ordinal))
                {
                    if (!_postings.TryGetValue(group.Key, out var postings))
                        _postings[group.Key] = postings = [];
                    postings.Add(new Posting(i, group.Count()));
                    documentFrequency[group.Key] = documentFrequency.GetValueOrDefault(group.Key) + 1;
                }
            }

            _averageLength = _lengths.Length == 0 ? 1 : Math.Max(1, _lengths.Average());
            foreach (var (gram, df) in documentFrequency)
                _idf[gram] = Math.Log(1 + (_clips.Length - df + 0.5) / (df + 0.5));
        }

        public IReadOnlyList<(OriginalVoiceClip Clip, double Coverage)> Search(
            OriginalVoiceQuery query,
            double minCoverage,
            int minMatchedGrams,
            int maxCount)
        {
            if (_clips.Length == 0)
                return [];

            var terms = BuildTerms(query);
            if (terms.Count == 0)
                return [];

            var totalIdf = terms.Sum(pair => pair.Value * _idf.GetValueOrDefault(pair.Key));
            if (totalIdf <= 0)
                return [];

            // 每个文档只累加命中的词项，避免遍历全表 × 全部词项。
            var scores = new Dictionary<int, double>();
            var matched = new Dictionary<int, int>();
            foreach (var (gram, weight) in terms)
            {
                if (!_postings.TryGetValue(gram, out var postings))
                    continue;
                var idf = _idf.GetValueOrDefault(gram);
                foreach (var posting in postings)
                {
                    var length = _lengths[posting.Document];
                    var tf = posting.Frequency;
                    var norm = tf * (K1 + 1) / (tf + K1 * (1 - B + B * length / _averageLength));
                    scores[posting.Document] = scores.GetValueOrDefault(posting.Document) + weight * idf * norm;
                    matched[posting.Document] = matched.GetValueOrDefault(posting.Document) + 1;
                }
            }

            var results = new List<(OriginalVoiceClip Clip, double Coverage)>();
            foreach (var (document, score) in scores)
            {
                if (matched.GetValueOrDefault(document) < minMatchedGrams)
                    continue;
                var coverage = score / totalIdf;
                if (coverage < minCoverage)
                    continue;
                results.Add((_clips[document], coverage));
            }

            return results
                .OrderByDescending(item => item.Coverage)
                .ThenBy(item => item.Clip.DurationMs <= 6_000 ? 0 : 1)
                .ThenBy(item => item.Clip.DurationMs)
                .Take(maxCount)
                .ToList();
        }

        /// <summary>把整轮上下文压成带权词项：用户原话最重，其次本轮剧情，再次近期对话。</summary>
        private static Dictionary<string, double> BuildTerms(OriginalVoiceQuery query)
        {
            var terms = new Dictionary<string, double>(StringComparer.Ordinal);
            Add(OriginalVoiceCatalog.Normalize(query.UserInput), 2.0);
            foreach (var text in query.EvidenceTexts)
                Add(OriginalVoiceCatalog.Normalize(text), 1.5);
            foreach (var text in query.RecentUtterances.TakeLast(4))
                Add(OriginalVoiceCatalog.Normalize(text), 0.8);
            return terms;

            void Add(string text, double weight)
            {
                if (text.Length < 2)
                    return;
                foreach (var gram in Grams(text))
                    terms[gram] = Math.Max(terms.GetValueOrDefault(gram), weight);
            }
        }

        private static List<string> Grams(string text)
        {
            var grams = new List<string>(Math.Max(1, text.Length));
            if (text.Length == 1)
            {
                grams.Add(text);
                return grams;
            }
            for (var i = 0; i + 1 < text.Length; i++)
                grams.Add(text.Substring(i, 2));
            return grams;
        }

        private readonly record struct Posting(int Document, int Frequency);
    }
}
