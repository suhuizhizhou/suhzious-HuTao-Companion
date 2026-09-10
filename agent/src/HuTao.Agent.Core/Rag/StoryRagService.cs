using System.Diagnostics;
using System.Text.Json;
using HuTao.Agent.Core.Abstractions;

namespace HuTao.Agent.Core.Rag;

/// <summary>共享只读语料快照；会话通过参数传入，缓存不保存角色回复或推理历史。</summary>
public sealed class StoryRagService : IStoryRagService
{
    private readonly StoryVectorStore _summaries;
    private readonly string[] _entities;
    private readonly StoryRagOptions _options;
    private readonly IStorySemanticSearch? _semantic;
    private readonly Lazy<Task<StoryLineIndex>> _index;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (StoryRagResult Result, DateTimeOffset Time)> _cache = [];
    private readonly Queue<string> _order = new();

    public StoryRagService(string dialogueRoot, StoryVectorStore summaries, StoryRagOptions? options = null,
        IStorySemanticSearch? semantic = null)
    {
        _summaries = summaries;
        _entities = summaries.Characters.ToArray();
        _options = options ?? new StoryRagOptions();
        _options.Validate();
        _semantic = semantic;
        _index = new Lazy<Task<StoryLineIndex>>(() => Task.Run(() => StoryLineIndex.Load(Path.Combine(dialogueRoot, "chapters"))));
    }
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
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = line.EvidenceId,
                text = (line.EvidenceKind == "character_story" ? "胡桃 " + line.MainTitle : line.Speaker) + "：" + line.Text
            }));
        }
    }

    public async Task<StoryRagResult> RetrieveAsync(string input, IReadOnlyList<ChatMessage>? history = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        if (input.Length > 4000) return Empty(StoryQueryAnalyzer.Plan(""), StoryStatus.Clarify, clock, "输入超过 4000 字，请缩小问题");
        var plan = StoryQueryAnalyzer.Plan(input, _entities, history);
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
            var cacheKey = index.Stats.Version + JsonSerializer.Serialize(plan);
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
            var (evidence, count) = await Task.Run(() => index.Retrieve(plan, _options,
                summaries.Select(h => h.Document.Id).ToArray(), semanticHits, budget.Token), budget.Token).ConfigureAwait(false);
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
                    if (!included.Contains(line.EvidenceId) && characters + length > _options.ContextCharacterBudget) continue;
                    context.Add(line);
                    if (included.Add(line.EvidenceId)) characters += length;
                }
                if (context.Any(l => l.EvidenceId == e.Id))
                    selected.Add(e with { Context = context.OrderBy(l => l.Sequence).ThenBy(l => l.Variant).ToArray() });
            }
            var status = selected.Count == 0 ? StoryStatus.NotFound :
                selected[0].Exact || selected[0].Score >= _options.AnswerScore ? StoryStatus.Answer : StoryStatus.Tentative;
            // 引用型问题仅有共同人名/词语不能当作“找到了原话”。
            if (plan.IsQuote && selected.Count > 0 && !selected[0].Exact && selected[0].Coverage < 0.35)
                status = StoryStatus.Clarify;
            var sufficiency = StorySufficiencyPolicy.Apply(plan, selected, status);
            status = sufficiency.Status;
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
