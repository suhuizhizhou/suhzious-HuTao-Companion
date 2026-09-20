using System.Diagnostics;
using System.Text.Json;
using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;
using HuTao.Persona;

namespace HuTao.Knowledge.Rag;

/// <summary>共享只读语料快照；会话通过参数传入，缓存不保存角色回复或推理历史。</summary>
public sealed class StoryRagService : IStoryRagService
{
    private readonly StoryVectorStore _summaries;
    private readonly StoryLexicon _lexicon;
    private readonly StoryRagOptions _options;
    private readonly IStorySemanticSearch? _semantic;
    private readonly Lazy<Task<StoryLineIndex>> _index;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (StoryRagResult Result, DateTimeOffset Time)> _cache = [];
    private readonly Queue<string> _order = new();

    /// <param name="characterLexicon">
    /// 角色的词表（自称、别名、私人话题）。语料自己的词表会从 <c>&lt;语料根&gt;/lexicon.json</c> 自动加载并合并。
    /// 不传就只有语料词表——检索照常工作，只是不再认识这个角色。
    /// </param>
    public StoryRagService(string dialogueRoot, StoryVectorStore summaries, StoryRagOptions? options = null,
        IStorySemanticSearch? semantic = null, StoryLexicon? characterLexicon = null)
    {
        _summaries = summaries;
        _options = options ?? new StoryRagOptions();
        _options.Validate();
        _semantic = semantic;

        // 语料词表放在 `<语料根>/lexicon.json`（dialogueRoot 的上一级）。
        var corpusRoot = Path.GetDirectoryName(Path.GetFullPath(dialogueRoot)) ?? dialogueRoot;
        var merged = StoryLexicon.Merge(characterLexicon, StoryLexicon.Load(StoryLexicon.CorpusLexiconPath(corpusRoot)));

        // 索引里已经带了「这部语料出现过哪些说话人」，它是最可靠的实体来源，直接并进词表。
        merged.Entities = [.. merged.Entities, .. summaries.Characters.Where(c => !merged.Entities.Contains(c))];
        _lexicon = merged;

        _index = new Lazy<Task<StoryLineIndex>>(() =>
            Task.Run(() => StoryLineIndex.Load(Path.Combine(dialogueRoot, "chapters"), _lexicon)));
    }

    /// <summary>当前生效的词表（角色 + 语料 + 索引实体）。</summary>
    public StoryLexicon Lexicon => _lexicon;

    public async Task WarmupAsync(CancellationToken ct = default) =>
        await _index.Value.WaitAsync(ct).ConfigureAwait(false);

    public async Task<StoryCorpusStats> GetStatsAsync(CancellationToken ct = default) =>
        (await _index.Value.WaitAsync(ct).ConfigureAwait(false)).Stats;

    public async Task ExportAsync(string path, CancellationToken ct = default)
    {
        var index = await _index.Value.WaitAsync(ct).ConfigureAwait(false);
        await using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { corpus_version = index.Stats.Version }));
        foreach (var line in index.Lines)
        {
            ct.ThrowIfCancellationRequested();
            var speaker = line.EvidenceKind == "character_story" && !string.IsNullOrWhiteSpace(_lexicon.SelfName)
                ? _lexicon.SelfName
                : line.Speaker;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = line.EvidenceId,
                text = speaker + "：" + line.Text
            }));
        }
    }

    /// <summary>
    /// 检索。**这里是全部 RAG 后端结果的唯一咽喉点**：无论调用来自 agent 的某一轮、
    /// 来自 <c>--query</c> 还是别的调用方，都在这里落一条可读的运行记录（见 <see cref="BackendTrace"/>）。
    /// 记录发生在**返回之前、且包住所有提前返回分支**（旁路/玩梗/安慰/超时都算后端结果），
    /// 所以「rag 跑了但结果是 Bypass」这种事也看得见。
    /// </summary>
    public async Task<StoryRagResult> RetrieveAsync(string input, IReadOnlyList<ChatMessage>? history = null,
        CancellationToken ct = default, RetrievalStrategy? strategy = null,
        IReadOnlyList<string>? chapterScope = null)
    {
        var trace = BackendTrace.Default.Enabled ? new RetrievalTraceCollector() : null;
        var result = await RetrieveCoreAsync(input, history, ct, strategy, chapterScope, trace).ConfigureAwait(false);
        if (trace is not null)
        {
            trace.Plan.Add($"检索文本 ▸ {StoryQueryAnalyzer.Normalize(result.Plan.SearchText)}");
            trace.Plan.Add($"标记 ▸ 引号原文={result.Plan.IsQuote} · 自称={result.Plan.IsSelf} · 追问={result.Plan.IsFollowUp}");
            if (result.Plan.Entities.Count > 0)
                trace.Plan.Add($"实体 ▸ {string.Join('、', result.Plan.Entities)}");
            if (!string.IsNullOrWhiteSpace(result.Plan.SelfName))
                trace.Plan.Add($"自称锚点 ▸ {result.Plan.SelfName}");
            // 变体分两类列全：原句变体（已过准入闸门）与概念扩展（同义词组）。
            var variants = result.Plan.Variants
                .Select(v => new RetrievalTraceVariant(v, "原句变体", true))
                .Concat(StoryQueryAnalyzer.Expand(result.Plan, _lexicon)
                    .Select(v => new RetrievalTraceVariant(v, "概念扩展", true)))
                .ToArray();
            var partsById = (trace.Ranking ?? [])
                .GroupBy(c => c.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Parts ?? [], StringComparer.Ordinal);
            BackendTrace.Default.Retrieval(new RetrievalTraceRecord(
                input,
                result.Plan.Route.ToString(),
                result.Plan.Reason,
                result.Status.ToString(),
                (strategy ?? _options.Strategy).ToString(),
                chapterScope?.Count ?? 0,
                result.Trace.CacheHit,
                result.Trace.Candidates,
                result.Trace.ElapsedMs,
                variants,
                result.Evidence.Select(e => new RetrievalTraceEvidence(
                    e.Id, e.Anchor.Speaker, e.Anchor.Text, e.Score, e.Coverage,
                    e.Anchor.ChapterId, e.Exact, e.MatchKind,
                    // 上下文 = 模型真正读到的行；锚点单独标出，避免「只记锚点」把窗口邻行丢掉。
                    e.Context.Concat([e.Anchor]).DistinctBy(l => l.EvidenceId)
                        .OrderBy(l => l.Sequence).ThenBy(l => l.Variant)
                        .Select(l => new RetrievalTraceContextLine(l.EvidenceId, l.Speaker, l.Text,
                            string.Equals(l.EvidenceId, e.Id, StringComparison.Ordinal)))
                        .ToArray(),
                    partsById.TryGetValue(e.Id, out var parts) ? parts : null)).ToArray(),
                result.Trace.Warnings,
                trace.Plan,
                trace.Channels,
                trace.Ranking,
                trace.Gates,
                trace.Reselect,
                trace.Budget,
                trace.Sufficiency,
                trace.Notes));
        }
        return result;
    }

    private async Task<StoryRagResult> RetrieveCoreAsync(string input, IReadOnlyList<ChatMessage>? history,
        CancellationToken ct, RetrievalStrategy? strategy, IReadOnlyList<string>? chapterScope,
        RetrievalTraceCollector? trace = null)
    {
        ct.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        // 本次调用实际使用的选项：agent 可以按轮次换臂，不传则用服务默认臂。
        var effective = strategy is { } perCall ? _options with { Strategy = perCall } : _options;
        if (input.Length > 4000) return Empty(StoryQueryAnalyzer.Plan("", _lexicon), StoryStatus.Clarify, clock, "输入超过 4000 字，请缩小问题");
        var plan = StoryQueryAnalyzer.Plan(input, _lexicon, history);
        if (plan.Route != StoryRoute.Retrieve)
            return Empty(plan, plan.Route switch
            {
                StoryRoute.Bypass => StoryStatus.Bypass,
                StoryRoute.Playful => StoryStatus.Playful,
                StoryRoute.Comfort => StoryStatus.Comfort,
                StoryRoute.Boundary => StoryStatus.Boundary,
                _ => StoryStatus.Clarify
            }, clock);
        try
        {
            var cold = !_index.IsValueCreated || !_index.Value.IsCompleted;
            var index = await _index.Value.WaitAsync(cold ? _options.ColdStartTimeout : _options.QueryTimeout, ct).ConfigureAwait(false);
            // 缓存键必须带上**本次实际使用的臂**。否则 agent 对同一查询换臂时会命中上一臂的缓存，
            // 换臂等于没换——这是「每调用可传策略」引入的新失效模式，必须与它一起处理。
            // 两级召回的 chapterScope 同理：同一个查询、同一个臂、不同章节，结果必然不同。
            var scopeKey = chapterScope is { Count: > 0 } ? string.Join(',', chapterScope) : "-";
            var cacheKey = index.Stats.Version + effective.Strategy + scopeKey + JsonSerializer.Serialize(plan);
            lock (_cacheLock)
                if (_cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromMinutes(5))
                    return cached.Result with { Trace = cached.Result.Trace with { CacheHit = true, ElapsedMs = clock.Elapsed.TotalMilliseconds } };
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(_options.QueryTimeout);
            var warnings = index.Stats.Warnings.ToList();
            IReadOnlyList<StorySemanticHit> semanticHits = [];
            if (_semantic is not null)
            {
                try { semanticHits = await _semantic.SearchAsync(plan.SearchText, index.Stats.Version, 40, budget.Token).ConfigureAwait(false); }
                catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException) && !ct.IsCancellationRequested)
                { warnings.Add("Dense unavailable; used lexical retrieval"); }
            }
            var summaries = _summaries.Search(plan.SearchText, 3);
            // 章节从哪来：**agent 显式给的优先**（它上一级已经定位到具体章节，这才是「先句子、再章节」），
            // 没给才退回摘要级匹配。两者都为空时 ChapterScope 臂没有候选可加，等于退回 Baseline——
            // 这是如实的降级，不是偷偷换成别的信号。
            var chapterHints = chapterScope is { Count: > 0 }
                ? chapterScope
                : summaries.Select(h => h.Document.Id).ToArray();
            var (evidence, count) = await Task.Run(() => index.Retrieve(plan, effective,
                chapterHints, semanticHits, budget.Token, trace), budget.Token).ConfigureAwait(false);
            var selected = new List<StoryEvidence>();
            var characters = 0;
            var included = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in evidence)
            {
                // 首先保障锚点，超长台词整条跳过，绝不截断原文伪装成完整引用。
                var context = new List<StoryDialogueLine>();
                foreach (var line in new[] { e.Anchor }.Concat(e.Context).DistinctBy(l => l.EvidenceId))
                {
                    var length = line.Text.Length + line.EvidenceId.Length + line.Speaker.Length + 80;
                    if (!included.Contains(line.EvidenceId) && characters + length > _options.ContextCharacterBudget)
                    {
                        // 预算裁剪**必须留痕**：被丢掉的行不会出现在任何「证据」清单里，
                        // 而它很可能就是答案行——不记的话，日志会显示「证据里没有」，
                        // 让人误以为检索没找到，实际是预算把它挤掉了。
                        trace?.Budget.Add($"超预算丢掉 [{line.EvidenceId}] {line.Speaker}：{RetrievalTraceCollector.Shorten(line.Text, 40)}" +
                            $"（已用 {characters}/{_options.ContextCharacterBudget} 字符）");
                        continue;
                    }
                    context.Add(line);
                    if (included.Add(line.EvidenceId)) characters += length;
                }
                if (context.Any(l => l.EvidenceId == e.Id))
                    selected.Add(e with { Context = context.OrderBy(l => l.Sequence).ThenBy(l => l.Variant).ToArray() });
            }
            // 高置信不能只看绝对分：场景先验单项就有 0.30，`0.44×0.2 + 0.30 = 0.388` 已经越线，
            // 于是「几乎没有字面证据」也能判 Answer（实测 IndexError 那条 Coverage 仅 0.032）。
            // 「检索不到只是没答上，给错了高置信才是真危险」，所以 Answer 追加一条对**所有问题**
            // 一视同仁的要求：被引证据必须真的承载了问题里足够份额的信息量。
            // IDF 加权的 Coverage 正好就是这个含义，不需要任何特例规则。
            var status = selected.Count == 0 ? StoryStatus.NotFound :
                selected[0].Exact ||
                (selected[0].Score >= _options.AnswerScore && selected[0].Coverage >= _options.MinAnswerCoverage)
                    ? StoryStatus.Answer : StoryStatus.Tentative;
            // 引用型问题仅有共同人名/词语不能当作“找到了原话”。
            if (plan.IsQuote && selected.Count > 0 && !selected[0].Exact && selected[0].Coverage < 0.35)
                status = StoryStatus.Clarify;
            var sufficiency = StorySufficiencyPolicy.Apply(plan, selected, status);
            status = sufficiency.Status;
            if (trace is not null)
            {
                trace.Sufficiency.Add($"状态判定 ▸ Top1 分数 {selected.FirstOrDefault()?.Score ?? 0:F3} / 覆盖 {selected.FirstOrDefault()?.Coverage ?? 0:F3}" +
                    $" · 阈值 AnswerScore={_options.AnswerScore:F2} MinAnswerCoverage={_options.MinAnswerCoverage:F2}" +
                    $" → {status}");
                trace.Sufficiency.Add(sufficiency.Caution is null
                    ? "充分性策略 ▸ 未追加约束（通用门禁已足够）"
                    : $"充分性策略 ▸ 追加约束：{sufficiency.Caution}");
                trace.Sufficiency.Add($"上下文 ▸ {selected.Count} 条证据 / 已用 {characters} 字符 / 预算 {_options.ContextCharacterBudget}");
            }
            if (sufficiency.Caution is not null) warnings.Add("证据边界：" + sufficiency.Caution);
            var result = new StoryRagResult(plan, status, selected.AsReadOnly(),
                _summaries.FindByIds(selected.Select(e => e.Anchor.ChapterId)).Take(2).ToArray(),
                new StoryRagTrace(index.Stats.Version, clock.Elapsed.TotalMilliseconds, false, count, characters,
                    semanticHits.Count > 0 ? "bm25+dense+rrf" : "bm25+expansion+rrf", warnings.AsReadOnly()));
            lock (_cacheLock)
            {
                if (_options.CacheCapacity > 0 && !warnings.Any(w => w.StartsWith("Dense unavailable")))
                {
                    if (!_cache.ContainsKey(cacheKey)) _order.Enqueue(cacheKey);
                    _cache[cacheKey] = (result, DateTimeOffset.UtcNow);
                    while (_cache.Count > _options.CacheCapacity) _cache.Remove(_order.Dequeue());
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Empty(plan, StoryStatus.Unavailable, clock, "检索超时"); }
        catch (Exception ex) when (ex is IOException or JsonException or TimeoutException or InvalidDataException)
        { return Empty(plan, StoryStatus.Unavailable, clock, ex.GetType().Name); }
    }

    private static StoryRagResult Empty(StoryQueryPlan plan, StoryStatus status, Stopwatch clock, string? warning = null) =>
        new(plan, status, [], [], new StoryRagTrace("", clock.Elapsed.TotalMilliseconds, false, 0, 0, "none",
            warning is null ? [] : [warning]));
}
