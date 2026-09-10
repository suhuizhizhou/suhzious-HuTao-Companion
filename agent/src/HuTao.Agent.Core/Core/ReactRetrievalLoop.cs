using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Rag;
using HuTao.Agent.Core.Tools;
using System.Text.Json;

namespace HuTao.Agent.Core.Core;

/// <summary>受控的 ReAct 检索循环：拆分子问题、逐个观察证据并在覆盖足够时停止。</summary>
public sealed record ReactRetrievalOptions
{
    public int MaxRounds { get; init; } = 6;
    public int MaxSubQueries { get; init; } = 6;
    public int MaxEvidencePerRound { get; init; } = 5;
    public double MinimumCoverage { get; init; } = 0.66;
    public bool EnableLlmPlanner { get; init; } = true;
    public int MaxRepairRounds { get; init; } = 2;
}

public sealed record ReactSubTask(string Id, string Query, IReadOnlyList<string> RequiredFacts,
    IReadOnlyList<string> DependsOn);

public sealed record ReactRetrievalStep(
    int Round, string Query, StoryStatus Status, int EvidenceCount,
    double Coverage, bool Sufficient, string Reason);

public sealed record ReactRetrievalResult(
    StoryRagResult Primary,
    StoryRagResult EvidencePool,
    IReadOnlyList<StoryRagResult> Results,
    IReadOnlyList<ReactRetrievalStep> Steps,
    string Observation);

public sealed class ReactRetrievalLoop
{
    private readonly StoryKnowledgeTool _tool;
    private readonly ReactRetrievalOptions _options;
    private readonly ILLMProvider? _llm;

    public ReactRetrievalLoop(StoryKnowledgeTool tool, ILLMProvider? llm = null, ReactRetrievalOptions? options = null)
    {
        _tool = tool;
        _llm = llm;
        _options = options ?? new ReactRetrievalOptions();
    }

    public async Task<ReactRetrievalResult?> RunAsync(
        string input,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var tasks = await PlanAsync(input, history, ct).ConfigureAwait(false);
        var results = new List<StoryRagResult>();
        var steps = new List<ReactRetrievalStep>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 0; round < _options.MaxRounds && completed.Count < tasks.Count; round++)
        {
            var ready = tasks.Where(t => !completed.Contains(t.Id) && t.DependsOn.All(completed.Contains)).ToArray();
            if (ready.Length == 0) ready = tasks.Where(t => !completed.Contains(t.Id)).Take(1).ToArray();
            var batch = await Task.WhenAll(ready.Select(t => _tool.RetrieveAsync(t.Query, history, ct))).ConfigureAwait(false);
            for (var i = 0; i < ready.Length; i++)
            {
                var task = ready[i]; var result = batch[i]; results.Add(result); completed.Add(task.Id);
                var coverage = result.Evidence.Count == 0 ? 0 : result.Evidence.Average(e => e.Coverage);
                var sufficient = result.Status is StoryStatus.Answer or StoryStatus.Tentative &&
                                 (coverage >= _options.MinimumCoverage || result.Evidence.Count >= _options.MaxEvidencePerRound);
                steps.Add(new ReactRetrievalStep(round + 1, task.Query, result.Status, result.Evidence.Count, coverage, sufficient,
                    sufficient ? "证据足够" : "证据可能缺失，后续执行补检索"));
            }
        }

        var primary = results.OrderByDescending(r => Score(r)).FirstOrDefault();
        if (primary is null) return null;
        var pool = Merge(primary, results);
        pool = await RepairMissingFactsAsync(input, tasks, history, pool, results, steps, ct).ConfigureAwait(false);
        pool = VerifyClaims(pool);
        var observation = string.Join("\n", steps.Select(step =>
            $"- react[{step.Round}] {step.Status}: {step.Query}; evidence={step.EvidenceCount}; coverage={step.Coverage:F2}; {step.Reason}"));
        return new ReactRetrievalResult(primary, pool, results, steps, observation);
    }

    private async Task<List<ReactSubTask>> PlanAsync(string input, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        // 旁路/玩梗/安慰和纯原文引用不应消耗 Planner 调用；它们必须保持原有低延迟路径。
        var route = StoryQueryAnalyzer.Plan(input).Route;
        var direct = route != StoryRoute.Retrieve ||
                     (input.Contains('“') && input.Contains('”') && input.Contains("原文", StringComparison.Ordinal));
        if (!direct && _options.EnableLlmPlanner && _llm is not null)
        {
            try
            {
                var prompt = "你是 Story RAG Query Planner。只返回 JSON：{\"tasks\":[{\"id\":\"t1\",\"query\":\"...\",\"required_facts\":[\"...\"],\"depends_on\":[]}]}。把用户问题拆成最多6个可独立检索的事实子任务；因果和时间解释任务依赖其前置事实；不要回答问题。用户问题：" + input;
                var raw = await _llm.CompleteAsync(prompt, history, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(raw.Trim().Trim('`'));
                var rows = doc.RootElement.GetProperty("tasks").EnumerateArray().Take(_options.MaxSubQueries).ToArray();
                var parsed = rows.Select((row, i) => new ReactSubTask(
                    row.TryGetProperty("id", out var id) ? id.GetString() ?? $"t{i + 1}" : $"t{i + 1}",
                    row.GetProperty("query").GetString() ?? input,
                    row.TryGetProperty("required_facts", out var facts) ? facts.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [],
                    row.TryGetProperty("depends_on", out var deps) ? deps.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [])).ToList();
                if (parsed.Count > 0) return parsed;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            { /* 降级到确定性规划 */ }
        }
        return BuildSubQueries(input).Select((query, i) => new ReactSubTask($"t{i + 1}", query, [], i == 0 ? [] : [$"t{i}"])).ToList();
    }

    private async Task<StoryRagResult> RepairMissingFactsAsync(string input, IReadOnlyList<ReactSubTask> tasks,
        IReadOnlyList<ChatMessage> history, StoryRagResult pool, List<StoryRagResult> results,
        List<ReactRetrievalStep> steps, CancellationToken ct)
    {
        var text = string.Join('\n', pool.Evidence.SelectMany(e => new[] { e.Anchor }.Concat(e.Context)).Select(x => x.Text));
        var missing = tasks.SelectMany(t => t.RequiredFacts).Distinct().Where(f => !text.Contains(f, StringComparison.OrdinalIgnoreCase)).Take(_options.MaxRepairRounds).ToArray();
        if (missing.Length == 0) return pool;
        var repairResults = await Task.WhenAll(missing.Select(f => _tool.RetrieveAsync(input + "；请只查：" + f, history, ct))).ConfigureAwait(false);
        results.AddRange(repairResults);
        foreach (var repair in repairResults)
            steps.Add(new ReactRetrievalStep(steps.Count + 1, input + "；补查", repair.Status, repair.Evidence.Count,
                repair.Evidence.Count == 0 ? 0 : repair.Evidence.Average(e => e.Coverage), repair.Evidence.Count > 0, "缺失事实组定向补检索"));
        var merged = Merge(pool, results);
        var remaining = missing.Where(f => !string.Join('\n', merged.Evidence.Select(e => e.Anchor.Text)).Contains(f, StringComparison.OrdinalIgnoreCase)).ToArray();
        var warning = merged.Trace.Warnings.Concat(remaining.Length == 0 ? [] : ["证据边界：仍缺失事实组：" + string.Join('、', remaining)]).Distinct().ToArray();
        return merged with { Status = remaining.Length == 0 ? merged.Status : StoryStatus.Tentative, Trace = merged.Trace with { Warnings = warning } };
    }

    private static StoryRagResult VerifyClaims(StoryRagResult pool)
    {
        var warnings = pool.Trace.Warnings.ToList();
        var all = string.Join('\n', pool.Evidence.SelectMany(e => new[] { e.Anchor }.Concat(e.Context)).Select(x => x.Text));
        if (pool.Plan.Original.Contains("为什么") && !all.Contains("因为") && !all.Contains("所以") && !all.Contains("由于"))
            warnings.Add("证据校验：因果方向缺少明确连接词，回答不得把相关性写成确定因果");
        if (pool.Plan.Original.Contains("后来") && !all.Contains("后来") && !all.Contains("之后") && !all.Contains("回家"))
            warnings.Add("证据校验：时间顺序锚点不足，回答需保留时间不确定性");
        return pool with { Trace = pool.Trace with { Warnings = warnings.Distinct().ToArray() } };
    }

    private static StoryRagResult Merge(StoryRagResult primary, IReadOnlyList<StoryRagResult> results)
    {
        var evidence = results.SelectMany(r => r.Evidence)
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(e => e.Score).First())
            .OrderByDescending(e => e.Score).ThenByDescending(e => e.Coverage)
            .ToArray();
        var background = results.SelectMany(r => r.Background).DistinctBy(x => x.Id).Take(8).ToArray();
        var warnings = results.SelectMany(r => r.Trace.Warnings).Distinct(StringComparer.Ordinal).ToArray();
        var trace = primary.Trace with
        {
            Candidates = results.Sum(r => r.Trace.Candidates),
            ContextCharacters = results.Sum(r => r.Trace.ContextCharacters),
            RetrievalMode = primary.Trace.RetrievalMode + "+react-subqueries",
            Warnings = warnings
        };
        var status = evidence.Length == 0 ? primary.Status :
            evidence.Any(e => e.Exact || e.Score >= 0.38) ? StoryStatus.Answer : StoryStatus.Tentative;
        return primary with { Status = status, Evidence = evidence, Background = background, Trace = trace };
    }

    private static double Score(StoryRagResult result) =>
        (result.Status is StoryStatus.Answer ? 2 : result.Status is StoryStatus.Tentative ? 1 : 0) +
        result.Evidence.Sum(e => e.Coverage) + Math.Min(result.Evidence.Count, 5) * 0.05;

    private List<string> BuildSubQueries(string input)
    {
        var parts = input.Split(['。', '？', '?', '！', '!', ';', '；'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 1).ToList();
        if (parts.Count <= 1) return [input];
        var result = new List<string>();
        foreach (var part in parts)
        {
            if (!result.Contains(part, StringComparer.Ordinal)) result.Add(part);
            if (result.Count >= _options.MaxSubQueries) break;
        }
        return result;
    }
}
