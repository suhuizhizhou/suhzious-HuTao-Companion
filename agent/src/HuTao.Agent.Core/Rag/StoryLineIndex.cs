namespace HuTao.Agent.Core.Rag;

/// <summary>全局 BM25 倒排 + 短语/容错精排；全文来源与检索分数分开保存。</summary>
internal sealed class StoryLineIndex
{
    private readonly StoryCorpus _corpus;
    private readonly Dictionary<uint, List<Posting>> _postings = [];
    private readonly Dictionary<string, int> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoryDialogueLine[]> _scenes;
    private readonly string[] _texts;
    private readonly int[] _lengths;
    private readonly double _averageLength;
    private readonly Dictionary<string, HashSet<uint>> _memoryTerms;
    private readonly Dictionary<string, int[]> _memoryRows;
    public StoryCorpusStats Stats => _corpus.Stats;
    public IReadOnlyList<StoryDialogueLine> Lines => _corpus.Lines;

    private StoryLineIndex(StoryCorpus corpus)
    {
        _corpus = corpus;
        _texts = new string[corpus.Lines.Count];
        _lengths = new int[corpus.Lines.Count];
        for (var i = 0; i < corpus.Lines.Count; i++)
        {
            var line = corpus.Lines[i];
            _byId[line.EvidenceId] = i;
            _texts[i] = StoryQueryAnalyzer.Normalize(line.Text);
            // 标题不重复进每条 BM25 正文，避免任务大标题淹没具体台词。
            var tokens = StoryQueryAnalyzer.BigramHashes(line.Speaker + " " + line.Text).ToArray();
            _lengths[i] = tokens.Length;
            foreach (var group in tokens.GroupBy(x => x))
            {
                if (!_postings.TryGetValue(group.Key, out var postings)) _postings[group.Key] = postings = [];
                postings.Add(new Posting(i, group.Count()));
            }
        }
        _averageLength = Math.Max(1, _lengths.Length == 0 ? 1 : _lengths.Average());
        _scenes = corpus.Lines.GroupBy(l => l.SceneKey).ToDictionary(g => g.Key,
            g => g.OrderBy(l => l.Sequence).ThenBy(l => l.Variant).ToArray(), StringComparer.Ordinal);
        _memoryTerms = corpus.Lines.Where(l => l.EvidenceKind == "character_story").GroupBy(l => l.SceneKey)
            .ToDictionary(g => g.Key, g => StoryQueryAnalyzer.BigramHashes(string.Join(' ', g.Select(l => l.MainTitle + " " + l.Text))).ToHashSet());
        _memoryRows = corpus.Lines.Select((l, i) => (l, i)).Where(p => p.l.EvidenceKind == "character_story")
            .GroupBy(p => p.l.SceneKey).ToDictionary(g => g.Key, g => g.Select(p => p.i).ToArray());
    }

    public static StoryLineIndex Load(string chaptersRoot) =>
        new(StoryCorpus.Load(Path.GetDirectoryName(chaptersRoot)!));

    public IReadOnlyList<DialogueSearchHit> Search(string query, int topK, int windowSize, double minScore)
    {
        var plan = StoryQueryAnalyzer.Plan(query);
        return Retrieve(plan, new StoryRagOptions { TopK = topK, WindowSize = windowSize, MinScore = minScore },
            [], [], CancellationToken.None).Evidence.Select(e => new DialogueSearchHit(
                e.Anchor.ChapterId, e.Score, e.Context, e.Anchor.LineId, e.MatchKind)).ToList();
    }

    public (IReadOnlyList<StoryEvidence> Evidence, int Candidates) Retrieve(
        StoryQueryPlan plan, StoryRagOptions options, IReadOnlyList<string> chapterHints,
        IReadOnlyList<StorySemanticHit> semantic, CancellationToken ct)
    {
        var original = ScoreTerms(plan.Variants);
        var expanded = options.EnableQueryExpansion ? ScoreTerms(StoryQueryAnalyzer.Expand(plan)) : new Dictionary<int, double>();
        // 角色记忆只有少量完整故事：先匹配故事主题，再回读原段落，避免口语被别人的同形问句截走。
        var queryTerms = StoryQueryAnalyzer.BigramHashes(plan.Variants.FirstOrDefault() ?? "").ToHashSet();
        var expandedTerms = options.EnableQueryExpansion ? StoryQueryAnalyzer.Expand(plan).SelectMany(StoryQueryAnalyzer.BigramHashes).ToHashSet() : [];
        var memories = _memoryTerms.ToDictionary(m => m.Key, m =>
            queryTerms.Where(m.Value.Contains).Sum(t => Math.Log(1 + (double)_memoryTerms.Count / (1 + _memoryTerms.Count(s => s.Value.Contains(t))))) +
            expandedTerms.Where(m.Value.Contains).Sum(t =>
                Math.Log(1 + (double)_memoryTerms.Count / (1 + _memoryTerms.Count(s => s.Value.Contains(t))))) * 0.35);
        var memoryMax = memories.Values.DefaultIfEmpty().Max();
        var memoryScenes = plan.IsSelf && options.EnablePersonalMemory ? memories.Where(m => m.Value > 0 && m.Value >= memoryMax * 0.45)
            .OrderByDescending(m => m.Value).Take(3).Select(m => m.Key).ToHashSet() : [];
        var selected = original.OrderByDescending(x => x.Value).Take(options.CandidateLimit).Select(x => x.Key)
            .Concat(expanded.OrderByDescending(x => x.Value).Take(options.CandidateLimit).Select(x => x.Key))
            .Concat(semantic.Where(h => _byId.ContainsKey(h.Id)).Select(h => _byId[h.Id]))
            .Concat(memoryScenes.SelectMany(s => _memoryRows[s]))
            .Distinct().ToArray();
        var semanticRanks = semantic.Select((h, rank) => (h.Id, rank)).DistinctBy(x => x.Id)
            .ToDictionary(x => x.Id, x => x.rank);
        var semanticScores = semantic.DistinctBy(h => h.Id).ToDictionary(h => h.Id, h => h.Score);
        var originalRanks = original.OrderByDescending(x => x.Value).Take(options.CandidateLimit)
            .Select((h, rank) => (h.Key, rank)).ToDictionary(x => x.Key, x => x.rank);
        var expandedRanks = expanded.OrderByDescending(x => x.Value).Take(options.CandidateLimit)
            .Select((h, rank) => (h.Key, rank)).ToDictionary(x => x.Key, x => x.rank);
        var ranked = new List<(int Index, double Score, double Coverage, bool Exact, string Kind)>();
        foreach (var i in selected)
        {
            ct.ThrowIfCancellationRequested();
            var line = _corpus.Lines[i];
            var exact = plan.Variants.Any(v => v.Length >= 5 && _texts[i].Contains(v, StringComparison.Ordinal));
            var coverage = plan.Variants.Select(v => Coverage(v, _texts[i])).DefaultIfEmpty().Max();
            // RRF 只融合排名，不把不同来源的原始得分混为一种“概率”。
            var fusion = (originalRanks.TryGetValue(i, out var r) ? 1.0 / (60 + r) : 0) +
                         (expandedRanks.TryGetValue(i, out r) ? 0.4 / (60 + r) : 0) +
                         (semanticRanks.TryGetValue(line.EvidenceId, out r) ? 0.8 / (60 + r) : 0);
            var self = plan.IsSelf && line.Speaker == "胡桃" ? 0.08 : 0;
            if (memoryScenes.Contains(line.SceneKey)) self += 0.30 * memories[line.SceneKey] / Math.Max(1, memoryMax);
            var entity = plan.Entities.Count(e => line.Text.Contains(e) || line.Speaker.Contains(e)) * 0.06;
            var chapter = chapterHints.Contains(line.ChapterId) ? 0.015 : 0;
            var score = (exact ? 0.55 : 0) + coverage * 0.44 + fusion * 4 + self + entity + chapter;
            ranked.Add((i, score, coverage, exact, exact ? "phrase" :
                semanticRanks.ContainsKey(line.EvidenceId) ? "bm25+dense+rrf" : "bm25+expansion+rrf"));
        }
        var evidence = new List<StoryEvidence>();
        var seenText = new HashSet<string>(StringComparer.Ordinal);
        var seenAnchors = new HashSet<string>(StringComparer.Ordinal);
        var seenMemories = new HashSet<string>(StringComparer.Ordinal);
        var hasLexicalEvidence = ranked.Any(h => h.Score >= options.MinScore);
        foreach (var hit in ranked.OrderByDescending(x => x.Score)
                     .ThenBy(x => _corpus.Lines[x.Index].EvidenceKind == "supplemental_page" ? 1 : 0)
                     .ThenBy(x => _corpus.Lines[x.Index].EvidenceId, StringComparer.Ordinal))
        {
            var line = _corpus.Lines[hit.Index];
            // 没有任何常规命中时才放行高相似纯 Dense 候选，仍以低分 Tentative 返回。
            if (hit.Score < options.MinScore && (hasLexicalEvidence ||
                !semanticScores.TryGetValue(line.EvidenceId, out var cosine) || cosine < 0.70)) continue;
            if (line.EvidenceKind == "character_story" && !seenMemories.Add(line.SceneKey)) continue;
            if (!seenText.Add(StoryQueryAnalyzer.Normalize(line.Speaker) + ":" + _texts[hit.Index])) continue;
            // 保留独立命中锚点；每个证据携带自己的合法窗口，最终上下文再全局去重。
            if (!seenAnchors.Add(line.EvidenceId)) continue;
            var (context, kind) = Context(line, options.WindowSize);
            evidence.Add(new StoryEvidence(line.EvidenceId, line, context, hit.Score, hit.Coverage, hit.Exact,
                hit.Kind, line.EvidenceKind == "character_story" || line.Speaker == "胡桃"
                    ? StoryPerspective.Personal : StoryPerspective.Archive, kind));
            if (evidence.Count >= options.TopK) break;
        }
        return (evidence.AsReadOnly(), selected.Length);
    }

    private Dictionary<int, double> ScoreTerms(IEnumerable<string> texts)
    {
        var result = new Dictionary<int, double>();
        foreach (var term in texts.SelectMany(StoryQueryAnalyzer.BigramHashes).Distinct())
        {
            if (!_postings.TryGetValue(term, out var postings)) continue;
            var idf = Math.Log(1 + (_texts.Length - postings.Count + 0.5) / (postings.Count + 0.5));
            foreach (var p in postings)
            {
                var norm = 1.2 * (0.25 + 0.75 * _lengths[p.Index] / _averageLength);
                result[p.Index] = result.GetValueOrDefault(p.Index) + idf * p.Tf * 2.2 / (p.Tf + norm);
            }
        }
        return result;
    }

    private static double Coverage(string query, string text)
    {
        var grams = StoryQueryAnalyzer.BigramHashes(query).ToHashSet();
        if (grams.Count == 0) return 0;
        return (double)StoryQueryAnalyzer.BigramHashes(text).Distinct().Count(grams.Contains) / grams.Count;
    }

    private (IReadOnlyList<StoryDialogueLine>, string) Context(StoryDialogueLine anchor, int size)
    {
        if (anchor.EvidenceKind == "supplemental_page") return (new[] { anchor }, "page-single-line-no-branch-graph");
        var scene = _scenes[anchor.SceneKey];
        if (anchor.EvidenceKind == "character_story")
        {
            // 角色故事是有序叙述，不存在玩家互斥选项。保留完整短篇能同时看到七七态度的前后变化。
            return (scene, "complete-character-story-within-budget");
        }
        if (scene.Any(l => l.NextLineIds.Count > 0))
        {
            // 只沿唯一前驱/唯一后继行走，在岔路、缺行、合流处停止。
            var byLine = scene.GroupBy(l => l.LineId).ToDictionary(g => g.Key, g => g.ToArray());
            var chain = new LinkedList<StoryDialogueLine>();
            chain.AddLast(anchor);
            var visited = new HashSet<long> { anchor.LineId };
            var current = anchor;
            for (var k = 0; k < 2; k++)
            {
                var predecessors = scene.Where(l => l.NextLineIds.Contains(current.LineId)).ToArray();
                if (predecessors.Length != 1 || predecessors[0].NextLineIds.Count != 1 ||
                    !visited.Add(predecessors[0].LineId)) break;
                current = predecessors[0];
                chain.AddFirst(current);
            }
            current = anchor;
            while (chain.Count < size && current.NextLineIds.Count == 1)
            {
                var id = current.NextLineIds[0];
                if (!byLine.TryGetValue(id, out var next) || next.Length != 1 || !visited.Add(id)) break;
                if (scene.Count(l => l.NextLineIds.Contains(id)) != 1) break;
                current = next[0];
                chain.AddLast(current);
            }
            return (chain.ToArray(), "unique-graph-path");
        }
        // Codex 只有 sequence/variant 时，跨互斥选项或缺失序号就停止。
        var groups = scene.GroupBy(l => l.Sequence).ToDictionary(g => g.Key, g => g.ToArray());
        var output = new List<StoryDialogueLine> { anchor };
        for (var n = anchor.Sequence - 1; n >= anchor.Sequence - 2 && output.Count < size; n--)
        {
            if (!groups.TryGetValue(n, out var g) || g.Length != 1) break;
            output.Insert(0, g[0]);
        }
        for (var n = anchor.Sequence + 1; output.Count < size; n++)
        {
            if (!groups.TryGetValue(n, out var g) || g.Length != 1) break;
            output.Add(g[0]);
        }
        return (output.AsReadOnly(), "sequence-stops-at-choice");
    }

    private readonly record struct Posting(int Index, int Tf);
}
