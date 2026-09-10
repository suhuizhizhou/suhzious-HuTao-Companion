using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Rag;
using HuTao.Agent.Core.Core;
using HuTao.Agent.Core.Tools;
using HuTao.Agent.Core.Llm;

var root = FindRoot(Environment.CurrentDirectory);
var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    Converters = { new JsonStringEnumConverter() }
};
string? Option(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
if (args.Contains("--conversation-checks"))
{
    var conversationChecks = await ConversationChecks.RunAsync();
    foreach (var check in conversationChecks) Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Name} {check.Detail}");
    Console.WriteLine($"Conversation: {conversationChecks.Count(c => c.Passed)}/{conversationChecks.Count}; offline, no GPU.");
    return conversationChecks.All(c => c.Passed) ? 0 : 1;
}
if (args.Contains("--speech-checks"))
{
    var speechChecks = await SpeechDeliveryChecks.RunAsync();
    foreach (var check in speechChecks) Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Name} {check.Detail}");
    Console.WriteLine($"Speech delivery: {speechChecks.Count(c => c.Passed)}/{speechChecks.Count}; offline, no GPU.");
    return speechChecks.All(c => c.Passed) ? 0 : 1;
}
if (args.Contains("--live") && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")))
    throw new InvalidOperationException("--live requires DEEPSEEK_API_KEY. Set it before evaluation; no offline cases were executed.");
var summaries = StoryVectorStore.Load(Path.Combine(root, "data/story/index.json"));
using var semantic = Option("--semantic") is { } endpoint ? new LocalStorySemanticSearch(new Uri(endpoint)) : null;
var ablation = Option("--ablation") ?? "full";
if (ablation is not ("full" or "no-memory" or "no-expansion")) throw new ArgumentException("--ablation: full/no-memory/no-expansion");
var service = new StoryRagService(Path.Combine(root, "data/story/dialogue"), summaries,
    new StoryRagOptions { EnablePersonalMemory = ablation != "no-memory", EnableQueryExpansion = ablation != "no-expansion" }, semantic);
if (Option("--export") is { } export) { await service.ExportAsync(Path.GetFullPath(export)); Console.WriteLine("Exported " + export); return 0; }
if (Option("--query") is { } query)
{
    var result = await service.RetrieveAsync(query);
    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return 0;
}
var requestedCases = Option("--cases");
var reactLive = args.Contains("--react-live");
if (reactLive && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")))
    throw new InvalidOperationException("--react-live requires DEEPSEEK_API_KEY; use --react-limit for a bounded live run.");
var inputs = requestedCases is null
    ? new[]
    {
        Path.Combine(root, "evaluation/story-rag/benchmark-v2.jsonl"),
        Path.Combine(root, "evaluation/story-rag/benchmark-v3-complex.jsonl"),
        Path.Combine(root, "evaluation/story-rag/benchmark-v4-agentic.jsonl")
    }
    : requestedCases.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Path.GetFullPath).ToArray();
foreach (var input in inputs)
    if (!File.Exists(input)) throw new FileNotFoundException("Benchmark file not found", input);
var cases = inputs.SelectMany(File.ReadLines).Where(l => !string.IsNullOrWhiteSpace(l))
    .Select(l => JsonSerializer.Deserialize<BenchmarkCase>(l, json) ?? throw new InvalidDataException("Null case")).ToArray();
if (cases.Select(c => c.Id).Distinct().Count() != cases.Length || cases.Any(c => c.Queries.Length == 0))
    throw new InvalidDataException("Duplicate case id or no query");
BenchmarkV4Audit.Validate(cases, root);
foreach (var c in cases)
{
    if (c.Difficulty is < 0 or > 5 || c.MinimumFactGroups < 0 || c.MinimumFactGroups > c.EvidenceGroups.Length)
        throw new InvalidDataException($"Invalid complexity annotation: {c.Id}");
    if (c.ReasoningTypes.Any(string.IsNullOrWhiteSpace) || c.SourceScopes.Any(string.IsNullOrWhiteSpace))
        throw new InvalidDataException($"Blank reasoning/source annotation: {c.Id}");
}
var cold = Stopwatch.StartNew();
await service.WarmupAsync();
cold.Stop();
var corpusStats = await service.GetStatsAsync();
if (semantic is not null)
{
    try
    {
        var probe = await semantic.SearchAsync("胡桃 往生堂", corpusStats.Version, 1,
            new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        if (probe.Count == 0) throw new InvalidOperationException("semantic service returned no probe result");
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
    {
        throw new InvalidOperationException(
            $"--semantic endpoint is not ready ({ex.GetType().Name}). Start and verify the local service before evaluation.", ex);
    }
}
// 验证标注确实存在于当前快照，不能用不存在的行号做“金标”。
var exportPath = Path.Combine(Path.GetTempPath(), "hutao-eval-" + Guid.NewGuid() + ".jsonl");
var ids = new HashSet<string>();
try
{
    await service.ExportAsync(exportPath);
    foreach (var row in File.ReadLines(exportPath).Skip(1))
    {
        using var d = JsonDocument.Parse(row);
        ids.Add(d.RootElement.GetProperty("id").GetString()!);
    }
}
finally { File.Delete(exportPath); }
foreach (var c in cases)
    foreach (var id in c.EvidenceGroups.SelectMany(g => g))
        if (!ids.Contains(id)) throw new InvalidDataException($"Unknown gold evidence: {c.Id}: {id}");
var checks = await StructuralChecks.RunAsync(service, summaries);
var reactReport = reactLive ? await RunReactEvaluationAsync(cases, service, root, json, Option("--react-limit")) : null;
var rows = new List<CaseResult>();
foreach (var c in cases)
{
    for (var variant = 0; variant < c.Queries.Length; variant++)
    {
        var result = await service.RetrieveAsync(c.Queries[variant], c.History);
        var anchors = result.Evidence.Select(e => e.Id).ToArray();
        var context = result.Evidence.SelectMany(e => e.Context).Select(l => l.EvidenceId).ToHashSet();
        bool Relevant(string id) => c.EvidenceGroups.Any(group => group.Contains(id));
        var rank = Array.FindIndex(anchors, Relevant) + 1;
        var coveredGroups = c.EvidenceGroups.Count(g => g.Any(context.Contains));
        var groupCoverage = c.EvidenceGroups.Length == 0 ? (double?)null : coveredGroups / (double)c.EvidenceGroups.Length;
        var requiredGroups = c.MinimumFactGroups == 0 ? c.EvidenceGroups.Length : c.MinimumFactGroups;
        var relevance = anchors.Select((id, i) => Relevant(id) ? 1 / Math.Log2(i + 2) : 0).Sum();
        var ideal = Enumerable.Range(0, Math.Min(5, c.EvidenceGroups.SelectMany(g => g).Distinct().Count()))
            .Sum(i => 1 / Math.Log2(i + 2));
        var routeOk = result.Plan.Route == c.Route;
        var statusOk = c.Statuses.Length == 0 || c.Statuses.Contains(result.Status);
        var pass = routeOk && statusOk && coveredGroups >= requiredGroups;
        var difficulty = c.Difficulty == 0 ? InferDifficulty(c) : c.Difficulty;
        var reasoningTypes = c.ReasoningTypes.Length == 0 ? new[] { c.Category } : c.ReasoningTypes;
        var sourceScopes = c.SourceScopes.Length == 0 ? InferSourceScopes(c) : c.SourceScopes;
        rows.Add(new CaseResult(c.Id, variant, c.Split, c.Category, difficulty, reasoningTypes, sourceScopes,
            c.EvidenceGroups.Length, requiredGroups, coveredGroups, c.Queries[variant], c.Route, result.Plan.Route,
            result.Status, pass, routeOk, statusOk, rank == 1, rank > 0, rank > 0 ? 1d / rank : 0,
            ideal > 0 ? relevance / ideal : null, groupCoverage, c.Unanswerable,
            result.Trace.ElapsedMs, result.Trace.CacheHit, result.Trace.ContextCharacters, result.Trace.RetrievalMode, result.Trace.Warnings,
            result.Evidence.Select(e => new Hit(e.Id, e.Score, e.Coverage, e.Anchor.Text, e.Context.Select(l => l.EvidenceId).ToArray())).ToArray())
            { Domain = c.Domain });
    }
}
var cacheTimes = new List<double>();
foreach (var c in cases.Where(c => c.Route == StoryRoute.Retrieve).Take(20))
{
    await service.RetrieveAsync(c.Queries[0], c.History);
    var repeated = await service.RetrieveAsync(c.Queries[0], c.History);
    if (repeated.Trace.CacheHit) cacheTimes.Add(repeated.Trace.ElapsedMs);
}
var dev = rows.Where(r => r.Split == "regression").ToArray();
var gates = new Dictionary<string, bool>
{
    ["structural"] = checks.All(c => c.Passed),
    ["configured_dense_actually_used"] = semantic is null || rows.Count(r => r.RetrievalMode.Contains("dense")) >= rows.Count(r => r.ActualRoute == StoryRoute.Retrieve) * .95,
    ["regression_route_accuracy_ge_0.95"] = dev.Length == 0 || dev.Average(r => r.RouteOk ? 1d : 0) >= .95,
    ["regression_context_all_facts_ge_0.90"] = dev.Length == 0 || dev.Where(r => r.ContextRecall is not null).DefaultIfEmpty().Average(r => r is null || r.ContextRecall == 1 ? 1d : 0) >= .90,
    ["regression_status_accuracy_ge_0.95"] = dev.Length == 0 || dev.Average(r => r.StatusOk ? 1d : 0) >= .95,
    ["lexical_warm_p95_lt_250ms"] = semantic is not null || Metrics.Percentile(rows.Where(r => !r.CacheHit && r.ActualRoute == StoryRoute.Retrieve).Select(r => r.ElapsedMs), .95) < 250
};
var outDir = Path.GetFullPath(Option("--out") ?? Path.Combine(root, "evaluation/story-rag/results/latest"));
Directory.CreateDirectory(outDir);
var report = new
{
    created_utc = DateTimeOffset.UtcNow,
    dataset_sha256 = DatasetHash(inputs),
    dataset_files = inputs.Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToArray(),
    corpus = corpusStats,
    mode = semantic is null ? "lexical" : "hybrid",
    ablation,
    family_all_variants_pass = rows.GroupBy(r => r.Id).Average(g => g.All(r => r.Passed) ? 1d : 0),
    independent_families = cases.Length,
    executions = rows.Count,
    cold_index_ms = cold.Elapsed.TotalMilliseconds,
    cache_p50_ms = Metrics.Percentile(cacheTimes, .5),
    cache_p95_ms = Metrics.Percentile(cacheTimes, .95),
    overall = Metrics.Summarize(rows),
    splits = rows.GroupBy(r => r.Split).ToDictionary(g => g.Key, g => Metrics.Summarize(g)),
    categories = rows.GroupBy(r => r.Category).ToDictionary(g => g.Key, g => Metrics.Summarize(g)),
    domains = rows.GroupBy(r => r.Domain).ToDictionary(g => g.Key, g => Metrics.Summarize(g)),
    routing_scope = new
    {
        unnecessary_retrieval_count = rows.Count(r => r.ExpectedRoute != StoryRoute.Retrieve && r.ActualRoute == StoryRoute.Retrieve),
        no_retrieval_expected_count = rows.Count(r => r.ExpectedRoute != StoryRoute.Retrieve),
        missed_retrieval_count = rows.Count(r => r.ExpectedRoute == StoryRoute.Retrieve && r.ActualRoute != StoryRoute.Retrieve),
        retrieval_expected_count = rows.Count(r => r.ExpectedRoute == StoryRoute.Retrieve)
    },
    difficulty = rows.GroupBy(r => $"L{r.Difficulty}").ToDictionary(g => g.Key, g => Metrics.Summarize(g)),
    reasoning_types = rows.SelectMany(r => r.ReasoningTypes.Distinct().Select(t => (Type: t, Row: r)))
        .GroupBy(x => x.Type).ToDictionary(g => g.Key, g => Metrics.Summarize(g.Select(x => x.Row))),
    source_scopes = rows.SelectMany(r => r.SourceScopes.Distinct().Select(s => (Scope: s, Row: r)))
        .GroupBy(x => x.Scope).ToDictionary(g => g.Key, g => Metrics.Summarize(g.Select(x => x.Row))),
    complexity_by_fact_groups = rows.GroupBy(r => r.GoldFactGroups).OrderBy(g => g.Key)
        .ToDictionary(g => g.Key.ToString(), g => Metrics.Summarize(g)),
    multi_group = Metrics.Summarize(rows.Where(r => r.GoldFactGroups >= 2)),
    cross_source = Metrics.Summarize(rows.Where(r => r.SourceScopes.Distinct().Count() >= 2)),
    route_confusion = rows.GroupBy(r => $"{r.ExpectedRoute}->{r.ActualRoute}").ToDictionary(g => g.Key, g => g.Count()),
    gates,
    react = reactReport,
    structural_checks = checks,
    cases = rows,
    limitations = new[] { "No live LLM factuality or naturalness claims from offline metrics.",
        "Regression set used during development; challenge set is visible, not an untouched held-out test.",
        "Unknown-query high-score rate measures retrieval risk, not factual answer correctness.",
        "Evidence groups enumerate acceptable sources, not every duplicate anywhere in corpus." }
};
await File.WriteAllTextAsync(Path.Combine(outDir, "report.json"), JsonSerializer.Serialize(report, json), new UTF8Encoding(false));
var md = new StringBuilder("# Story RAG 离线评估\n\n")
    .AppendLine($"独立问题族：{cases.Length}；执行：{rows.Count}；模式：{report.mode}；冷索引：{report.cold_index_ms:F0} ms。")
    .AppendLine("\n指标只验证本地检索与工程结构，不代表 DeepSeek 的事实正确率、自然度或语音延迟。\n")
    .AppendLine("| 分组 | 路由准确率 | Anchor R@1 | Anchor R@5 | Context 全事实 | MRR | 热查 P95 ms |")
    .AppendLine("|---|---:|---:|---:|---:|---:|---:|");
foreach (var g in rows.GroupBy(r => r.Split))
{
    var m = Metrics.Summarize(g);
    md.AppendLine($"| {g.Key} | {m.RouteAccuracy:P1} | {m.AnchorRecall1:P1} | {m.AnchorRecall5:P1} | {m.AllFactRecall:P1} | {m.Mrr:F3} | {m.WarmP95Ms:F1} |");
}
md.AppendLine("\n## 意图范围（不检索题不计入事实召回分母）\n")
    .AppendLine("| 范围 | 题次 | 路由准确率 | 全事实覆盖 |")
    .AppendLine("|---|---:|---:|---:|");
foreach (var g in rows.GroupBy(r => r.Domain))
{
    var m = Metrics.Summarize(g);
    md.AppendLine($"| {g.Key} | {g.Count()} | {m.RouteAccuracy:P1} | {m.AllFactRecall:P1} |");
}
md.AppendLine($"\n不应查却检索：{report.routing_scope.unnecessary_retrieval_count}/{report.routing_scope.no_retrieval_expected_count}；应查却未检索：{report.routing_scope.missed_retrieval_count}/{report.routing_scope.retrieval_expected_count}。")
    .AppendLine("\n范围、证据可自动评分；expected_plan、现实建议、观点边界仍需人工核查，不以多次调用或节点数量证明推理正确。")
    .AppendLine("\n## 难度分层\n")
    .AppendLine("| 难度 | 执行 | 路由准确率 | Context 全事实 | Fact Group 平均覆盖 |")
    .AppendLine("|---|---:|---:|---:|---:|");
foreach (var g in rows.GroupBy(r => r.Difficulty).OrderBy(g => g.Key))
{
    var m = Metrics.Summarize(g);
    md.AppendLine($"| L{g.Key} | {g.Count()} | {m.RouteAccuracy:P1} | {m.AllFactRecall:P1} | {m.ContextRecall:P1} |");
}
md.AppendLine("\n## 推理类型\n")
    .AppendLine("| 类型 | 执行 | Context 全事实 | Anchor R@5 |")
    .AppendLine("|---|---:|---:|---:|");
foreach (var g in rows.SelectMany(r => r.ReasoningTypes.Distinct().Select(t => (Type: t, Row: r)))
             .GroupBy(x => x.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
{
    var m = Metrics.Summarize(g.Select(x => x.Row));
    md.AppendLine($"| {g.Key} | {g.Count()} | {m.AllFactRecall:P1} | {m.AnchorRecall5:P1} |");
}
md.AppendLine("\n## 来源范围与事实组复杂度\n")
    .AppendLine("| 分组 | 执行 | Context 全事实 | Fact Group 平均覆盖 |")
    .AppendLine("|---|---:|---:|---:|");
foreach (var g in rows.Where(r => r.GoldFactGroups > 0)
             .GroupBy(r => r.SourceScopes.Length == 0 ? "unlabeled" : string.Join('+', r.SourceScopes.Order(StringComparer.Ordinal)))
             .OrderBy(g => g.Key, StringComparer.Ordinal))
{
    var m = Metrics.Summarize(g);
    md.AppendLine($"| source:{g.Key} | {g.Count()} | {m.AllFactRecall:P1} | {m.ContextRecall:P1} |");
}
foreach (var g in rows.Where(r => r.GoldFactGroups > 0).GroupBy(r => r.GoldFactGroups).OrderBy(g => g.Key))
{
    var m = Metrics.Summarize(g);
    md.AppendLine($"| gold-groups:{g.Key} | {g.Count()} | {m.AllFactRecall:P1} | {m.ContextRecall:P1} |");
}
md.AppendLine($"\n缓存命中 P50/P95：{report.cache_p50_ms:F2}/{report.cache_p95_ms:F2} ms。")
    .AppendLine($"\n结构测试：{checks.Count(c => c.Passed)}/{checks.Count}。")
    .AppendLine("\n## 门禁\n");
foreach (var gate in gates) md.AppendLine($"- {gate.Key}: {(gate.Value ? "PASS" : "FAIL")}");
md.AppendLine("\n## 失败执行（不隐藏难例）\n");
foreach (var row in rows.Where(r => !r.Passed))
    md.AppendLine($"- {row.Id}/{row.Variant} [L{row.Difficulty}; {string.Join(',', row.ReasoningTypes)}]: {row.Query}；route={row.ActualRoute}, status={row.Status}, groups={row.CoveredFactGroups}/{row.RequiredFactGroups}");
await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), md.ToString(), new UTF8Encoding(false));
Console.WriteLine(md.ToString());
if (args.Contains("--live")) await LiveEvaluation.RunAsync(cases, service, summaries, outDir, json,
    Option("--live-limit"), args.Contains("--live-all-variants"));
return checks.Any(c => !c.Passed) || args.Contains("--strict") && gates.Any(g => !g.Value) ? 1 : 0;

static async Task<object> RunReactEvaluationAsync(BenchmarkCase[] cases, StoryRagService service, string root,
    JsonSerializerOptions json, string? limitText)
{
    var limit = int.TryParse(limitText, out var parsed) ? Math.Clamp(parsed, 1, cases.Length) : cases.Length;
    var llm = new DeepSeekLlmProvider(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")!);
    var tool = new StoryKnowledgeTool(service);
    var loop = new ReactRetrievalLoop(tool, llm, new ReactRetrievalOptions { EnableLlmPlanner = true });
    var rows = new List<object>();
    var plannerTasks = 0; var parallelBatches = 0; var repairs = 0; var covered = 0;
    foreach (var c in cases.Take(limit))
    {
        var query = c.Queries[0];
        var started = Stopwatch.StartNew();
        var result = await loop.RunAsync(query, c.History);
        started.Stop();
        if (result is null) continue;
        plannerTasks += result.Steps.Count;
        parallelBatches += result.Steps.GroupBy(s => s.Round).Count(g => g.Count() > 1);
        repairs += result.Steps.Count(s => s.Query.Contains("补查", StringComparison.Ordinal));
        var context = result.EvidencePool.Evidence.SelectMany(e => e.Context).Select(x => x.EvidenceId).ToHashSet();
        var groups = c.EvidenceGroups.Count(g => g.Any(context.Contains));
        covered += groups;
        rows.Add(new
        {
            id = c.Id, query, status = result.EvidencePool.Status.ToString(), rounds = result.Steps.Count,
            evidence = result.EvidencePool.Evidence.Count, covered_groups = groups, gold_groups = c.EvidenceGroups.Length,
            repairs = result.Steps.Count(s => s.Query.Contains("补查", StringComparison.Ordinal)), elapsed_ms = started.Elapsed.TotalMilliseconds,
            steps = result.Steps,
            domain = c.Domain, expected_route = c.Route, actual_route = result.EvidencePool.Plan.Route,
            expected_plan = c.ExpectedPlan, source_refs = c.SourceRefs, rubric = c.Rubric,
            plan_semantic_grade = "not_automatically_scored"
        });
    }
    return new
    {
        enabled = true, provider = llm.Name, cases = rows.Count,
        planner_task_count = plannerTasks, average_tasks = rows.Count == 0 ? 0 : plannerTasks / (double)rows.Count,
        parallel_batches = parallelBatches, repair_queries = repairs,
        evidence_group_coverage = covered / (double)Math.Max(1, cases.Take(limit).Sum(c => c.EvidenceGroups.Length)),
        rows
    };
}

static string FindRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, "data/story"))) return dir.FullName;
    throw new DirectoryNotFoundException("Run inside hutao-companion.");
}

static int InferDifficulty(BenchmarkCase c) => c.Unanswerable || c.History.Length > 1 || c.EvidenceGroups.Length > 1 ? 3 :
    c.History.Length == 1 || c.Category.Contains("wrong", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

static string[] InferSourceScopes(BenchmarkCase c) => c.EvidenceGroups.SelectMany(g => g).Select(id =>
    id.StartsWith("character:", StringComparison.Ordinal) ? "character" :
    id.StartsWith("textmap:", StringComparison.Ordinal) ? "dialogue" :
    id.StartsWith("archive:", StringComparison.Ordinal) ? "archive" : "unknown")
    .Distinct(StringComparer.Ordinal).ToArray();

static string DatasetHash(IEnumerable<string> paths)
{
    using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
    foreach (var path in paths)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
        hash.AppendData(File.ReadAllBytes(path));
    }
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}

internal sealed record BenchmarkCase
{
    public string Domain { get; init; } = "legacy";
    public string[] SourceRefs { get; init; } = [];
    public string Provenance { get; init; } = "";
    public ExpectedPlanTask[] ExpectedPlan { get; init; } = [];
    public string Id { get; init; } = "";
    public string Split { get; init; } = "regression";
    public string Category { get; init; } = "";
    public int Difficulty { get; init; }
    public string[] ReasoningTypes { get; init; } = [];
    public string[] SourceScopes { get; init; } = [];
    public int MinimumFactGroups { get; init; }
    public string[] RelationChain { get; init; } = [];
    public string[] IndependentClaims { get; init; } = [];
    public string[] ExpectedCorrections { get; init; } = [];
    public string[] ForbiddenInferences { get; init; } = [];
    public string? ConversationId { get; init; }
    public int? TurnId { get; init; }
    public string[] Queries { get; init; } = [];
    public ChatMessage[] History { get; init; } = [];
    public StoryRoute Route { get; init; } = StoryRoute.Retrieve;
    public StoryStatus[] Statuses { get; init; } = [];
    public string[][] EvidenceGroups { get; init; } = [];
    public string[] RequiredFacts { get; init; } = [];
    public string[] ForbiddenFacts { get; init; } = [];
    public string Perspective { get; init; } = "";
    public bool Unanswerable { get; init; }
    public string Rubric { get; init; } = "";
}
internal sealed record Hit(string Id, double Score, double Coverage, string Text, string[] ContextIds);
internal sealed record CaseResult(string Id, int Variant, string Split, string Category, int Difficulty,
    string[] ReasoningTypes, string[] SourceScopes, int GoldFactGroups, int RequiredFactGroups, int CoveredFactGroups, string Query,
    StoryRoute ExpectedRoute, StoryRoute ActualRoute, StoryStatus Status, bool Passed, bool RouteOk, bool StatusOk,
    bool Anchor1, bool Anchor5, double ReciprocalRank, double? Ndcg5, double? ContextRecall, bool Unanswerable,
    double ElapsedMs, bool CacheHit, int ContextCharacters, string RetrievalMode, IReadOnlyList<string> Warnings, Hit[] Hits)
{
    public string Domain { get; init; } = "legacy";
}
internal sealed record Summary(int Count, double RouteAccuracy, double RouteMacroF1, double? AnchorRecall1,
    double? AnchorRecall5, double? AllFactRecall, double? ContextRecall, double? Mrr, double? Ndcg5,
    double? AnswerableOverRejection, double? UnknownHighScoreRate, double WarmP50Ms, double WarmP95Ms,
    double ContextP95Characters);
internal static class Metrics
{
    public static double Percentile(IEnumerable<double> numbers, double p)
    {
        var a = numbers.Order().ToArray();
        return a.Length == 0 ? 0 : a[Math.Clamp((int)Math.Ceiling(p * a.Length) - 1, 0, a.Length - 1)];
    }
    public static Summary Summarize(IEnumerable<CaseResult> input)
    {
        var rows = input.ToArray();
        var positive = rows.Where(r => r.ContextRecall is not null).ToArray();
        var negative = rows.Where(r => r.Unanswerable).ToArray();
        double? Mean(CaseResult[] a, Func<CaseResult, double> f) => a.Length == 0 ? null : a.Average(f);
        var routeValues = rows.Select(r => r.ExpectedRoute).Concat(rows.Select(r => r.ActualRoute)).Distinct().Select(route =>
        {
            var tp = rows.Count(r => r.ExpectedRoute == route && r.ActualRoute == route);
            var denom = rows.Count(r => r.ExpectedRoute == route) + rows.Count(r => r.ActualRoute == route);
            return denom == 0 ? 0 : 2d * tp / denom;
        }).ToArray();
        var f1 = routeValues.Length == 0 ? 0 : routeValues.Average();
        var times = rows.Where(r => !r.CacheHit && r.ActualRoute == StoryRoute.Retrieve).Select(r => r.ElapsedMs);
        var routeAccuracy = rows.Length == 0 ? 0 : rows.Average(r => r.RouteOk ? 1d : 0);
        return new Summary(rows.Length, routeAccuracy, f1,
            Mean(positive, r => r.Anchor1 ? 1 : 0), Mean(positive, r => r.Anchor5 ? 1 : 0),
            Mean(positive, r => r.ContextRecall == 1 ? 1 : 0), Mean(positive, r => r.ContextRecall!.Value),
            Mean(positive, r => r.ReciprocalRank), Mean(positive, r => r.Ndcg5 ?? 0),
            Mean(positive, r => r.Status is StoryStatus.NotFound or StoryStatus.Clarify or StoryStatus.Unavailable ? 1 : 0),
            Mean(negative, r => r.Status == StoryStatus.Answer ? 1 : 0),
            Percentile(times, .5), Percentile(times, .95), Percentile(rows.Select(r => (double)r.ContextCharacters), .95));
    }
}
