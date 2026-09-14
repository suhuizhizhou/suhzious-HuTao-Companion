using HuTao.Persona;

namespace HuTao.Knowledge.Rag;

/// <summary>全局 BM25 倒排 + 短语/容错精排；全文来源与检索分数分开保存。</summary>
internal sealed class StoryLineIndex
{
    private readonly StoryCorpus _corpus;
    private readonly StoryLexicon _lexicon;
    private readonly Dictionary<uint, List<Posting>> _postings = [];
    private readonly Dictionary<string, int> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoryDialogueLine[]> _scenes;
    private readonly string[] _texts;
    private readonly int[] _lengths;
    private readonly double _averageLength;
    private readonly Dictionary<string, HashSet<uint>> _memoryTerms;
    private readonly Dictionary<string, int[]> _memoryRows;
    /// <summary>章节 → 行下标。两级召回第二级要用它把整个章节的内容拉进候选。</summary>
    private readonly Dictionary<string, int[]> _chapterRows;
    public StoryCorpusStats Stats => _corpus.Stats;
    public IReadOnlyList<StoryDialogueLine> Lines => _corpus.Lines;

    private StoryLineIndex(StoryCorpus corpus, StoryLexicon lexicon)
    {
        _corpus = corpus;
        _lexicon = lexicon;
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
        _chapterRows = corpus.Lines.Select((l, i) => (l, i))
            .Where(p => !string.IsNullOrWhiteSpace(p.l.ChapterId))
            .GroupBy(p => p.l.ChapterId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.l.Sequence).ThenBy(p => p.l.Variant)
                .Select(p => p.i).ToArray(), StringComparer.Ordinal);
    }

    public static StoryLineIndex Load(string chaptersRoot, StoryLexicon? lexicon = null) =>
        new(StoryCorpus.Load(Path.GetDirectoryName(chaptersRoot)!, lexicon), lexicon ?? StoryLexicon.Empty);

    public IReadOnlyList<DialogueSearchHit> Search(string query, int topK, int windowSize, double minScore)
    {
        var plan = StoryQueryAnalyzer.Plan(query, _lexicon);
        return Retrieve(plan, new StoryRagOptions { TopK = topK, WindowSize = windowSize, MinScore = minScore },
            [], [], CancellationToken.None).Evidence.Select(e => new DialogueSearchHit(
                e.Anchor.ChapterId, e.Score, e.Context, e.Anchor.LineId, e.MatchKind)).ToList();
    }

    public (IReadOnlyList<StoryEvidence> Evidence, int Candidates) Retrieve(
        StoryQueryPlan plan, StoryRagOptions options, IReadOnlyList<string> chapterHints,
        IReadOnlyList<StorySemanticHit> semantic, CancellationToken ct)
    {
        // ── 融合臂 ──
        // **它只是众多可选臂中的一个**：不是默认路径，也不替代其他臂。
        // 是否值得选由离线策略矩阵判定——若 Fusion 碾压所有单臂，说明该做融合而不是加 agent；
        // 若某单臂在特定分层胜出，才说明「选择」本身有价值。
        //
        // **在 TopK 截断之前**做：候选并集 + rank-based 融合（RRF）。
        // 两个曾经的错误做法，记在这里防止回退：
        //  1. 合并各臂的**最终证据**：每个臂已经各自截断到 TopK，融合结果里不可能出现
        //     任何单臂没到达的案例——救回率恒为 0，那是恒等式，不是「融合不行」的结论。
        //  2. 跨臂取**最高分**排序：各臂分数尺度不一致（HierarchyFirst 抬高章节权重＝整体抬分），
        //     融合会被抬分最多的那个臂带偏（实测它的 A1 恰好等于 HierarchyFirst）。
        // RRF 只吃名次，对分数尺度天然免疫。
        //
        // 每个臂都用 CandidateLimit 取**深度列表**（不截断到 TopK），否则融合又被各臂的截断限制住。
        // 分数尺度以 Baseline 为准（arms[0]，先到先得），保证下游按 Score 判状态仍然可比。
        if (options.Strategy == RetrievalStrategy.Fusion)
        {
            const int rrfK = 60;
            var arms = new[]
            {
                RetrievalStrategy.Baseline, RetrievalStrategy.LexicalOnly,
                RetrievalStrategy.ConceptOnly, RetrievalStrategy.HierarchyFirst,
            };
            var rrf = new Dictionary<string, double>(StringComparer.Ordinal);
            var native = new Dictionary<string, StoryEvidence>(StringComparer.Ordinal);
            var candidates = 0;
            foreach (var arm in arms)
            {
                var (list, count) = Retrieve(plan, options with { Strategy = arm, TopK = options.CandidateLimit },
                    chapterHints, semantic, ct);
                candidates += count;
                for (var rank = 0; rank < list.Count; rank++)
                {
                    var e = list[rank];
                    rrf[e.Id] = rrf.GetValueOrDefault(e.Id) + 1.0 / (rrfK + rank);
                    // Baseline 排在最前，所以「先到先得」＝分数尺度以 Baseline 为准。
                    if (!native.ContainsKey(e.Id)) native[e.Id] = e;
                }
            }
            var fused = rrf.OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(options.TopK)
                .Select(kv => native[kv.Key])
                .ToArray();
            return (fused, candidates);
        }
        var original = ScoreTerms(plan.Variants);
        var expanded = options.EnableQueryExpansion ? ScoreTerms(StoryQueryAnalyzer.Expand(plan, _lexicon)) : new Dictionary<int, double>();
        // 角色记忆只有少量完整故事：先匹配故事主题，再回读原段落，避免口语被别人的同形问句截走。
        // ── 策略臂：只收窄「候选来源」，不改打分口径 ──
        // 打分口径（LineScorer）是**全臂共用的**，这样臂与臂之间的差异是纯粹可观测的差异，
        // 不会把「换了来源」和「换了打分」两种变化混在一起——否则离线矩阵算出来的东西无法归因。
        if (options.Strategy == RetrievalStrategy.LexicalOnly)
        {
            expanded = new Dictionary<int, double>();
            semantic = [];
        }
        else if (options.Strategy == RetrievalStrategy.ConceptOnly)
        {
            original = new Dictionary<int, double>();
            semantic = [];
        }
        else if (options.Strategy == RetrievalStrategy.SemanticOnly)
        {
            // 只看语义：词法两条通道全关，保留 dense 命中与角色故事记忆行。
            // 未配置 dense 服务时 semantic 本来就是空的 → 该臂返回空，
            // 如实反映「该策略当前不可用」，而不是偷偷退回词法把两种信号混在一起。
            original = new Dictionary<int, double>();
            expanded = new Dictionary<int, double>();
        }
        // 章节命中先验：Baseline 是「轻推」0.015；HierarchyFirst 臂把它提到 0.15，
        // 让「属于摘要/标题命中的那个章节」这层信息真正参与排序（治场景内歧义）。
        // **ChapterScope 不动这个先验**——它不是「更重的先验」，而是「多一类候选来源」，
        // 两者必须分开，否则测出来的差异归因不到机制上。
        var chapterWeight = options.Strategy == RetrievalStrategy.HierarchyFirst ? 0.15 : 0.015;
        // ── 两级召回的第二级：把已命中章节的内容拉进候选 ──
        // 第二级**不再要求与查询有字面重合**（那正是第一级在做的事），
        // 所以这些候选必须能绕过 MinScore 闸门；否则它们分数低、会被一句 `continue` 全部丢掉，
        // 这个臂就退化成「什么都没做」——一个看起来能选、实际无效的臂比没有这个臂更糟。
        var chapterCandidates = new HashSet<int>();
        if (options.Strategy == RetrievalStrategy.ChapterScope)
            foreach (var chapter in chapterHints)
                if (_chapterRows.TryGetValue(chapter, out var rows))
                    foreach (var index in rows) chapterCandidates.Add(index);
        var queryTerms = StoryQueryAnalyzer.BigramHashes(plan.Variants.FirstOrDefault() ?? "").ToHashSet();
        var expandedTerms = options.EnableQueryExpansion ? StoryQueryAnalyzer.Expand(plan, _lexicon).SelectMany(StoryQueryAnalyzer.BigramHashes).ToHashSet() : [];
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
            .Concat(chapterCandidates)
            .Distinct().ToArray();
        var semanticRanks = semantic.Select((h, rank) => (h.Id, rank)).DistinctBy(x => x.Id)
            .ToDictionary(x => x.Id, x => x.rank);
        var semanticScores = semantic.DistinctBy(h => h.Id).ToDictionary(h => h.Id, h => h.Score);
        var originalRanks = original.OrderByDescending(x => x.Value).Take(options.CandidateLimit)
            .Select((h, rank) => (h.Key, rank)).ToDictionary(x => x.Key, x => x.rank);
        var expandedRanks = expanded.OrderByDescending(x => x.Value).Take(options.CandidateLimit)
            .Select((h, rank) => (h.Key, rank)).ToDictionary(x => x.Key, x => x.rank);
        var scorer = new LineScorer(this, plan, originalRanks, expandedRanks, semanticRanks,
            memoryScenes, memories, memoryMax, chapterHints, chapterWeight);
        var ranked = new List<(int Index, double Score, double Coverage, bool Exact, string Kind)>();
        foreach (var i in selected)
        {
            ct.ThrowIfCancellationRequested();
            var scored = scorer.Score(i);
            ranked.Add((i, scored.Score, scored.Coverage, scored.Exact, scored.Kind));
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
            // 两级召回的第二级候选按定义就与查询没有字面重合，分数天然低于 MinScore：
            // 放行它们是这个臂的**全部意义**；不确定与「没有任何常规命中」的 dense 特例混在一起，
            // 所以这里单独一条、条件写死是 chapterCandidates 成员。
            // 安全性：放行只影响「能不能进候选」，分数与 Coverage 一个没动，
            // 而 Answer 门禁要求 `Score >= 0.38 && Coverage >= 0.30`——
            // 与查询无关的章节行 Coverage 近 0，**不可能**靠这条通道把状态抬成 Answer。
            var inChapterScope = chapterCandidates.Contains(hit.Index);
            // 没有任何常规命中时才放行高相似纯 Dense 候选，仍以低分 Tentative 返回。
            if (hit.Score < options.MinScore && !inChapterScope && (hasLexicalEvidence ||
                !semanticScores.TryGetValue(line.EvidenceId, out var cosine) || cosine < 0.70)) continue;
            // 角色故事每个场景只放行分数最高的 1 行。
            // **实测记录（2026-09-13）：不要放宽它。** 曾为了补 Anchor R@5 放宽到每场景 2 行
            // （假设：金标锚点不是该场景最高分行，所以永远进不了 top-5），结果全面变差：
            // challenge R@5 71.7%→70.9%、Context 67.7%→66.1%、legacy 全事实 88.1%→86.8%。
            // 原因是同场景多出来的那行会挤掉其他场景更有用的证据。R@5 的缺口不在这里。
            if (line.EvidenceKind == "character_story" && !seenMemories.Add(line.SceneKey)) continue;
            if (!seenText.Add(StoryQueryAnalyzer.Normalize(line.Speaker) + ":" + _texts[hit.Index])) continue;
            // 保留独立命中锚点；每个证据携带自己的合法窗口，最终上下文再全局去重。
            if (!seenAnchors.Add(line.EvidenceId)) continue;
            var (context, kind) = Context(line, options.WindowSize);
            evidence.Add(new StoryEvidence(line.EvidenceId, line, context, hit.Score, hit.Coverage, hit.Exact,
                hit.Kind, line.EvidenceKind == "character_story" || _lexicon.IsSelfSpeaker(line.Speaker)
                    ? StoryPerspective.Personal : StoryPerspective.Archive, kind));
            if (evidence.Count >= options.TopK) break;
        }
        return (ReselectAnchors(evidence, scorer), selected.Length);
    }

    /// <summary>
    /// 锚点重选：证据确定后，在它自己的 Context 窗口里用与主排序**完全同一个**
    /// <see cref="LineScorer"/> 把每行重算一遍，取分数最高的行当锚点；
    /// 若它不是原锚点，就与旧锚点交换位置——Context 仍是同一批行（集合不变），
    /// 所以事实覆盖不会因此退步。
    /// 收益来自「窗口里某一行的行级分数本就高于被选为锚点的那行」的情形
    /// （典型是 unique-graph-path / sequence 窗口：锚点是候选里分最高的，
    /// 但同一窗口的邻行可能更高）。
    /// </summary>
    /// <remarks>
    /// 能力边界（实测，勿夸大）：**它修不了词汇缺口**。
    /// 例如金标用例 <c>hat-owner</c>（问「你的帽子是谁传给你的？」，
    /// 期望锚点 <c>character:hutao:2520842344:1</c>「据说，此帽由七十五代堂主传承给胡桃」）：
    /// 该行确实在 top-1 证据的 Context 里，但它在本打分口径下就是**更低**的一方——
    /// 实测 seq1=0.3440 vs seq2=0.4367（Coverage 0.100 vs 0.200），
    /// 换成扩展变体（帽子/旧帽/拆补/头上/花饰）后仍是 0.1000，全场景最低。
    /// 因为查询与答案句除「胡桃」外没有任何共同 bigram（「帽子」≠「此帽」、「传给」≠「传承给」）。
    /// 主排序既然已经把 seq2 排在 seq1 前面（character_story 每个场景只放行最高分行），
    /// 同一口径的重选必然还是 seq2。要修这类问题得动词表/打分特征（T2），不是重锚的职责。
    /// </remarks>
    /// <remarks>
    /// 只动 <c>Anchor</c> 与 <c>Id</c>（<c>Id</c> 恒等于锚点行的 EvidenceId，
    /// 下游 <c>StoryRagService</c> 依赖「Id 必须出现在 Context 里」这个不变量）。
    /// <c>Score</c>/<c>Coverage</c>/<c>Exact</c>/<c>MatchKind</c>/<c>Perspective</c> 保持原值：
    /// 它们是「这条证据是怎么被选出来的」的排序产物（RRF 融合得分、原候选行的覆盖度），
    /// 下游的上下文预算、Answer/Tentative/Clarify 状态判定都在读它们；
    /// 重锚只是在同一窗口内换一个更好的「引用行」，不该把新的排序信号回灌进那套逻辑。
    /// </remarks>
    private static IReadOnlyList<StoryEvidence> ReselectAnchors(
        List<StoryEvidence> evidence, LineScorer scorer)
    {
        for (var e = 0; e < evidence.Count; e++)
        {
            var item = evidence[e];
            if (item.Context.Count <= 1) continue;

            var anchorAt = -1;
            var best = -1;
            var bestScore = double.NegativeInfinity;
            for (var k = 0; k < item.Context.Count; k++)
            {
                var line = item.Context[k];
                if (line.EvidenceId == item.Anchor.EvidenceId) anchorAt = k;
                var index = scorer.IndexOf(line);
                if (index < 0) continue;
                var score = scorer.Score(index).Score;
                // 严格大于：同分时保留靠前的行，保证结果稳定可复现。
                if (score <= bestScore) continue;
                best = k;
                bestScore = score;
            }
            if (anchorAt < 0 || best < 0 || best == anchorAt) continue;

            var context = item.Context.ToArray();
            (context[best], context[anchorAt]) = (context[anchorAt], context[best]);
            evidence[e] = item with
            {
                Id = context[anchorAt].EvidenceId,
                Anchor = context[anchorAt],
                Context = context,
            };
        }
        return evidence.AsReadOnly();
    }

    /// <summary>
    /// 行级打分。主排序与「窗口内重锚」共用同一个实例，所以两处的权重、特征和
    /// RRF 融合口径必然一致——不存在为某个用途单独调参的岔路。
    /// 想改打分口径，只需要改这里一处。
    /// </summary>
    private sealed class LineScorer
    {
        private readonly StoryLineIndex _owner;
        private readonly StoryQueryPlan _plan;
        private readonly Dictionary<int, int> _originalRanks;
        private readonly Dictionary<int, int> _expandedRanks;
        private readonly Dictionary<string, int> _semanticRanks;
        private readonly HashSet<string> _memoryScenes;
        private readonly Dictionary<string, double> _memories;
        private readonly double _memoryMax;
        private readonly IReadOnlyList<string> _chapterHints;
        private readonly double _chapterWeight;

        public LineScorer(StoryLineIndex owner, StoryQueryPlan plan,
            Dictionary<int, int> originalRanks, Dictionary<int, int> expandedRanks,
            Dictionary<string, int> semanticRanks, HashSet<string> memoryScenes,
            Dictionary<string, double> memories, double memoryMax, IReadOnlyList<string> chapterHints,
            double chapterWeight)
        {
            _owner = owner;
            _plan = plan;
            _originalRanks = originalRanks;
            _expandedRanks = expandedRanks;
            _semanticRanks = semanticRanks;
            _memoryScenes = memoryScenes;
            _memories = memories;
            _memoryMax = memoryMax;
            _chapterHints = chapterHints;
            _chapterWeight = chapterWeight;
        }

        /// <summary>把一行映射回索引；不在索引里（例如缺行）返回 -1。</summary>
        public int IndexOf(StoryDialogueLine line) =>
            _owner._byId.TryGetValue(line.EvidenceId, out var index) ? index : -1;

        /// <summary>
        /// RRF 的 k。**这是「排名差有多重要」的唯一旋钮。**
        ///
        /// 实测记录（2026-09-13）：曾把它从 60 调到 20，想让 RRF 上限（0.44）与覆盖度同量级，
        /// 结果**全面变差**——regression Context 100.0%→97.8%、legacy 全事实 88.7%→84.9%、
        /// challenge Context 68.5%→65.4%，并且挂掉了 `nte_retrieval_finds_evidence`。
        /// 原因：RRF 只看排名，把它放大等于给所有「BM25 排得上号」的句子一律加一个大平项，
        /// 噪声与信号被同等放大。所以「RRF 太弱」不是本层的真问题，保持 60。
        /// </summary>
        private const double RrfK = 60;

        public (double Score, double Coverage, bool Exact, string Kind) Score(int index)
        {
            var line = _owner._corpus.Lines[index];
            var text = _owner._texts[index];
            var exact = _plan.Variants.Any(v => v.Length >= 5 && text.Contains(v, StringComparison.Ordinal));
            var coverage = _plan.Variants.Select(v => _owner.Coverage(v, text)).DefaultIfEmpty().Max();
            // RRF 只融合排名，不把不同来源的原始得分混为一种“概率”。
            var fusion = (_originalRanks.TryGetValue(index, out var r) ? 1.0 / (RrfK + r) : 0) +
                         (_expandedRanks.TryGetValue(index, out r) ? 0.4 / (RrfK + r) : 0) +
                         (_semanticRanks.TryGetValue(line.EvidenceId, out r) ? 0.8 / (RrfK + r) : 0);
            var self = _plan.IsSelf && _owner._lexicon.IsSelfSpeaker(line.Speaker) ? 0.08 : 0;
            // 记忆场景先验。**实测记录（2026-09-13）：不要调低它。**
            // 曾按「先验太大、淹没了证据」的判断从 0.30 降到 0.15，结果全面崩：
            // regression Context 100.0%→93.3%、legacy 全事实 88.7%→83.0%、
            // challenge R@5 71.7%→68.5%、MRR 0.635→0.606。
            // 这个先验回答的是「这条属于角色本人的故事吗」，而本轮真正要的答案句
            // （例如帽子那条 seq1）恰恰靠它才进得了候选——它是在帮忙，不是在添乱。
            // 「高置信过松」的根在状态判定（已由 StoryRagOptions.MinAnswerCoverage 处理），
            // 不在这里。
            if (_memoryScenes.Contains(line.SceneKey))
                self += 0.30 * _memories[line.SceneKey] / Math.Max(1, _memoryMax);
            var entity = _plan.Entities.Count(e => line.Text.Contains(e) || line.Speaker.Contains(e)) * 0.06;
            var chapter = _chapterHints.Contains(line.ChapterId) ? _chapterWeight : 0;
            var score = (exact ? 0.55 : 0) + coverage * 0.44 + fusion * 4 + self + entity + chapter;
            return (score, coverage, exact, exact ? "phrase" :
                _semanticRanks.ContainsKey(line.EvidenceId) ? "bm25+dense+rrf" : "bm25+expansion+rrf");
        }
    }

    private Dictionary<int, double> ScoreTerms(IEnumerable<string> texts)
    {
        var result = new Dictionary<int, double>();
        foreach (var term in texts.SelectMany(StoryQueryAnalyzer.BigramHashes).Distinct())
        {
            if (!_postings.TryGetValue(term, out var postings)) continue;
            var idf = Idf(term);
            foreach (var p in postings)
            {
                var norm = 1.2 * (0.25 + 0.75 * _lengths[p.Index] / _averageLength);
                result[p.Index] = result.GetValueOrDefault(p.Index) + idf * p.Tf * 2.2 / (p.Tf + norm);
            }
        }
        return result;
    }

    /// <summary>
    /// BM25 的 IDF。**只此一处**：<see cref="ScoreTerms"/> 与 <see cref="Coverage"/> 必须共用
    /// 同一个公式，否则「排序用一套罕见度观感、覆盖度用另一套」正是要避免的口径漂移。
    /// df = 0（语料里没有这个 gram）时按平滑公式给出很大的值：用户问了一个语料里不存在的词，
    /// 覆盖率本来就该被拉低，而不是靠一条特例规则去挡。
    /// </summary>
    private double Idf(uint term)
    {
        var df = _postings.TryGetValue(term, out var postings) ? postings.Count : 0;
        return Math.Log(1 + (_texts.Length - df + 0.5) / (df + 0.5));
    }

    /// <summary>
    /// 变体对一行的字面覆盖度 = **未加权命中率**与**IDF 加权命中率**各一半。
    ///
    /// 两条曲线各有偏科，实测记录（2026-09-13）：
    /// · 纯未加权（命中 gram 数 / 变体 gram 数）= 召回向。在变体准入修好之前，
    ///   两字变体只有一个 gram，撞上就必然 1.000（噪声句拿 0.7400 压过真正沾边的证据）。
    ///   准入修好后这个漏洞从上游堵死了，未加权本身不再虚高。
    /// · 纯 IDF 加权 = 精度向。常见 gram 几乎不贡献，且不可命中的跨字假词（`桃现`/`在几`）
    ///   不进分母。但它会让「只共享常见词」的正确句子覆盖率骤降而掉出候选：
    ///   实测 challenge R@5 从 73.2% 掉到 71.7%、MRR 0.644→0.635。
    /// 各取一半后两条短板互相补：常见词共享仍算命中（保召回），罕见词命中权重更高（保精度）。
    /// </summary>
    private double Coverage(string query, string text)
    {
        var grams = StoryQueryAnalyzer.BigramHashes(query).ToHashSet();
        if (grams.Count == 0) return 0;
        var present = StoryQueryAnalyzer.BigramHashes(text).ToHashSet();
        var plain = (double)grams.Count(present.Contains) / grams.Count;
        var total = 0.0;
        var matched = 0.0;
        foreach (var gram in grams)
        {
            var idf = MatchableIdf(gram);
            if (idf <= 0) continue;
            total += idf;
            if (present.Contains(gram)) matched += idf;
        }
        var weighted = total <= 0 ? 0 : matched / total;
        return (plain + weighted) / 2;
    }

    /// <summary>只给语料里真实存在的 gram 记 IDF；不存在的返回 0，表示「不参与覆盖度分母」。</summary>
    private double MatchableIdf(uint term) =>
        _postings.TryGetValue(term, out var postings) && postings.Count > 0 ? Idf(term) : 0;

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
