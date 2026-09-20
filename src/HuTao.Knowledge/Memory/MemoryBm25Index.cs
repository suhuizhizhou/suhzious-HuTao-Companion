using HuTao.Knowledge.Rag;

namespace HuTao.Knowledge.Memory;

/// <summary>
/// 记忆的增量 BM25 索引。与剧情索引用同一套分词和同一族参数，
/// 只是规模小得多（千级文档），因此可以做成增量追加而不是整表重建。
///
/// 只用 BM25、不加向量，是刻意的选择：阶段 1 的目标是**零额外 LLM 成本、零额外延迟**，
/// 而记忆检索的 query 通常就是用户刚说的那句话，字面重合度已经很高。
/// </summary>
internal sealed class MemoryBm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    /// <summary>参与打分的查询词项上限，防止长句被大量词项稀释。</summary>
    private const int MaxQueryTerms = 12;
    /// <summary>命中词项 idf 之和的绝对下限，用来挡掉「只命中一个超高频虚词」的情况。</summary>
    private const double MinMatchedIdf = 0.3;

    private readonly List<MemoryRecord> _documents = [];
    private readonly List<int> _lengths = [];
    private readonly Dictionary<uint, List<Posting>> _postings = [];
    private readonly Dictionary<uint, int> _documentFrequency = [];
    private readonly HashSet<string> _indexedIds = new(StringComparer.Ordinal);
    private double _averageLength = 1;

    public int Count => _documents.Count;

    /// <summary>增量加入一条记录；重复 id 直接忽略。</summary>
    public void Add(MemoryRecord record)
    {
        if (!_indexedIds.Add(record.Id))
            return;

        var grams = StoryQueryAnalyzer.BigramHashes(record.Text).ToArray();
        var index = _documents.Count;
        _documents.Add(record);
        _lengths.Add(Math.Max(1, grams.Length));

        foreach (var group in grams.GroupBy(g => g))
        {
            if (!_postings.TryGetValue(group.Key, out var postings))
                _postings[group.Key] = postings = [];
            postings.Add(new Posting(index, group.Count()));
            _documentFrequency[group.Key] = _documentFrequency.GetValueOrDefault(group.Key) + 1;
        }

        _averageLength = Math.Max(1, _lengths.Average());
    }

    /// <summary>
    /// 相关度用「IDF 加权的查询覆盖率」：命中的内容词 idf 之和 ÷ 全部内容词 idf 之和。
    ///
    /// 为什么不用 raw/(raw+k) 那种固定常数归一化：BM25 的 idf 量级随语料规模变化两个数量级
    /// （4 篇语料里 df=2 的词 idf≈0.18，1000 篇里 df=1 的词 idf≈6.5），任何固定 k 都必然
    /// 在一端失效——小语料上全部被门槛挡掉，大语料上又形同虚设。覆盖率是尺度无关的。
    ///
    /// BM25 原始分仍然计算，但只用来在同一覆盖率下排序（体现词频与文档长度差异）。
    /// </summary>
    public IReadOnlyList<(MemoryRecord Record, double Relevance, double Raw)> Search(string query, int topK)
    {
        if (_documents.Count == 0 || topK <= 0)
            return [];

        var normalized = StoryQueryAnalyzer.Normalize(query);
        if (normalized.Length == 0)
            return [];

        // 只保留语料里真实出现过的词项：未出现的词 idf 反而最大，留着会污染分母。
        var contentTerms = StoryQueryAnalyzer.BigramHashes(normalized)
            .Distinct()
            .Where(_documentFrequency.ContainsKey)
            .Select(term => (Term: term, Idf: Idf(term)))
            .OrderByDescending(item => item.Idf)
            .Take(MaxQueryTerms)
            .ToArray();
        if (contentTerms.Length == 0)
            return [];

        var totalIdf = contentTerms.Sum(item => item.Idf);
        var raw = new Dictionary<int, double>();
        var matchedIdf = new Dictionary<int, double>();
        foreach (var (term, idf) in contentTerms)
        {
            if (!_postings.TryGetValue(term, out var postings))
                continue;
            foreach (var posting in postings)
            {
                var length = _lengths[posting.Document];
                var tf = posting.Frequency;
                var norm = tf * (K1 + 1) / (tf + K1 * (1 - B + B * length / _averageLength));
                raw[posting.Document] = raw.GetValueOrDefault(posting.Document) + idf * norm;
                matchedIdf[posting.Document] = matchedIdf.GetValueOrDefault(posting.Document) + idf;
            }
        }

        // In a one-record store even an exact rare term has IDF=0.288.
        var minimumMatchedIdf = Math.Min(MinMatchedIdf, Math.Log(1 + (_documents.Count - 0.5) / 1.5));
        return raw
            .Where(pair => matchedIdf[pair.Key] >= minimumMatchedIdf)
            .Select(pair => (
                _documents[pair.Key],
                Relevance: Math.Clamp(matchedIdf[pair.Key] / totalIdf, 0, 1),
                Raw: pair.Value))
            .OrderByDescending(item => item.Item2)
            .ThenByDescending(item => item.Item3)
            .ThenBy(item => item.Item1.Id, StringComparer.Ordinal)
            .Take(topK)
            .ToList();
    }

    private double Idf(uint term)
    {
        var df = _documentFrequency.GetValueOrDefault(term);
        return Math.Log(1 + (_documents.Count - df + 0.5) / (df + 0.5));
    }

    private readonly record struct Posting(int Document, int Frequency);
}
