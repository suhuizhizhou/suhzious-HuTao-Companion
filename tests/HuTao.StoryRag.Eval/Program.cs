using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;
using HuTao.Knowledge.Rag;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Tools;
using HuTao.Dialogue.Llm;
using HuTao.Persona;

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
// 召回策略臂。缺省是 Baseline，与历史行为逐字节等价；
// 每个臂都是确定性的，所以「同一个 benchmark 跑 N 个臂」就能得到离线反事实矩阵，
// 从中算出 OracleGain / UnionR@k / BestFixed / 可学习性（见 notes/CORE.md §11.6）。
var strategy = Enum.TryParse<RetrievalStrategy>(Option("--strategy") ?? "Baseline", ignoreCase: true, out var parsedStrategy)
    ? parsedStrategy
    : throw new ArgumentException("--strategy: Baseline/LexicalOnly/ConceptOnly/SemanticOnly/HierarchyFirst/Fusion/ChapterScope");
// 角色词表从人设包读，和运行时走同一条路径。
// ⚠️ 这里不传词表的话，100 场景回归会失去「本堂主 / 桃桃 / 胡堂主 / 大咪」这些自称，
// 路由准确率会掉——而且看起来像检索退化，其实是评测自己没装词表。
var hutaoLexicon = StoryLexicon.Load(Path.Combine(root, "data/persona/hutao/lexicon.json"));
var service = new StoryRagService(Path.Combine(root, "data/story/dialogue"), summaries,
    new StoryRagOptions { EnablePersonalMemory = ablation != "no-memory", EnableQueryExpansion = ablation != "no-expansion", Strategy = strategy },
    semantic, hutaoLexicon);
if (Option("--export") is { } export) { await service.ExportAsync(Path.GetFullPath(export)); Console.WriteLine("Exported " + export); return 0; }
// 面向人阅读的后端追踪日志（报告之外单独一份可读叙事）。
// 评测里默认写进本次输出目录（`.cache` / `results` 都是 gitignored），**不写用户本机的日志**。
var traceOption = Option("--trace");
var tracePath = Path.GetFullPath(traceOption ??
    Path.Combine(Option("--out") ?? "evaluation/story-rag/results/latest", "backend-trace.log"));
// 这里只**定路径**，不开开关：真正打开由下面在「在线回放」那两段显式控制。
// （踩过：一开始就在这里 enabled:true，结果离线结构检查里几十个桩回合把日志灌满，
//   真正要读的在线回合被淹没——追踪日志的价值全在信噪比。）
if (traceOption is not null)
    BackendTrace.Configure(tracePath, enabled: false);
if (Option("--query") is { } query)
{
    if (traceOption is not null)
        BackendTrace.Configure(tracePath, enabled: true);
    var result = await service.RetrieveAsync(query);
    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return 0;
}
var requestedCases = Option("--cases");
var reactLive = args.Contains("--react-live");
if (reactLive && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")))
    throw new InvalidOperationException("--react-live requires DEEPSEEK_API_KEY; use --react-limit for a bounded live run.");
// --agent-live 走的是**完整 ReAct 回合**（检索 → 起草 → 沉浸判定 → 必要时换检索词重来），
// 而不是只跑检索循环。必须如此才能有「答案」可判：判官评的是答案的沉浸与连贯，
// 只跑检索循环时根本没有答案，判官只能退化成给证据打分——那正是本次要拆掉的越界。
var agentLive = args.Contains("--agent-live");
if (agentLive && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")))
    throw new InvalidOperationException("--agent-live requires DEEPSEEK_API_KEY; use --agent-limit for a bounded live run.");
// 追踪日志只在**在线回放**那两段打开。离线矩阵 + 两级探针有上千次「无回合」的 RAG 调用，
// 混进来会把真正要读的回合淹掉——日志的价值全在信噪比。
var traceLive = traceOption is not null || reactLive || agentLive;
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
var checks = await StructuralChecks.RunAsync(service, summaries, root);
if (traceLive) BackendTrace.Configure(tracePath, enabled: true);
var reactReport = reactLive ? await RunReactEvaluationAsync(cases, service, root, json, Option("--react-limit")) : null;
var agentReport = agentLive ? await RunAgentEvaluationAsync(cases, service, root, json, Option("--agent-limit")) : null;
if (traceLive) BackendTrace.Configure(tracePath, enabled: false);
var twoLevelReport = await RunTwoLevelProbeAsync(cases, service);
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
    // 跳过的检查（本机缺数据）既不算通过也不算失败，直接排除在门禁之外——
    // 但仍然会被单独统计出来，见下面的「跳过 N」。
    ["structural"] = checks.All(c => c.Passed || c.Skipped),
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
    // 报告必须**自描述**它跑的是哪个召回臂：否则多臂对照只能靠目录名认臂，
    // artifact 自身无法证明身份（本次就踩过 report.json 被覆盖、事后分不清是哪一臂的坑）。
    strategy = strategy.ToString(),
    // 两级召回（第三章具名策略）的**指定测法**必须自描述地落盘：
    // 它测的不是「一次召回排得准不准」，而是「先定位到某章之后，该章的其余内容能不能补上」。
    // 单次调用的臂矩阵**原理上测不了它**（矩阵里的 ChapterScope 结果与 Baseline 逐项相同，
    // 因为摘要级章节提示在 TopK=5 下从未进入前五）——所以它有独立的测法与独立的分母。
    strategy_probe = strategy == RetrievalStrategy.ChapterScope
        ? "two_level (level1=Baseline locate -> level2=ChapterScope expand); single-call matrix cannot exercise this arm"
        : "single_call",
    // 报告自描述「可读日志写在哪」，否则跑完只知道有日志、不知道去哪儿读。
    backend_trace = traceLive ? Path.GetRelativePath(root, tracePath).Replace('\\', '/') : null,
    // 臂目录随报告一起落盘：任何下游（脚本、可视化）都不许自己再抄一份中文描述，
    // 否则「每个臂是干什么的」会分成几份并悄悄漂移。
    arm_catalog = RetrievalStrategyCatalog.All.Select(x => new { arm = x.Arm.ToString(), intent = x.Intent }).ToArray(),
    // 分层计数：这是「不同策略不统一标准」的地基，必须由**评测本体**定义并落盘，
    // 不能让分层定义只活在外部对照脚本里（否则脚本改了层、评测不知道）。
    // 层口径：只统计「有金标事实组且可答」的用例——
    //   ranked_out    进了 top-5 却没排上第一  → 该由**排序类**手段治
    //   never_reached 从没进 top-5            → 该由**召回类**手段治
    strata = new
    {
        answerable_with_gold = rows.Count(r => !r.Unanswerable && r.GoldFactGroups > 0),
        ranked_out = rows.Count(r => !r.Unanswerable && r.GoldFactGroups > 0 && r.Anchor5 && !r.Anchor1),
        never_reached = rows.Count(r => !r.Unanswerable && r.GoldFactGroups > 0 && !r.Anchor5),
    },
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
    two_level = twoLevelReport,
    react = reactReport,
    agent = agentReport,
    structural_checks = checks,
    cases = rows,
    limitations = new[] { "No live LLM factuality or naturalness claims from offline metrics.",
        "Regression set used during development; challenge set is visible, not an untouched held-out test.",
        "Unknown-query high-score rate measures retrieval risk, not factual answer correctness.",
        "Evidence groups enumerate acceptable sources, not every duplicate anywhere in corpus.",
        "Online judge covers immersion / context coherence / logic coherence only; it makes no factual-validity claim. " +
        "Fact-side conclusions must come from the deterministic sufficiency layer and gold fact-group coverage." }
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
    .AppendLine($"\n结构测试：{checks.Count(c => c.Passed)}/{checks.Count(c => !c.Skipped)} 通过"
        + $"，{checks.Count(c => c.Skipped)} 跳过（本机缺数据，不计入通过率）。")
    .AppendLine("\n## 门禁\n");
foreach (var gate in gates) md.AppendLine($"- {gate.Key}: {(gate.Value ? "PASS" : "FAIL")}");
md.AppendLine("\n## 失败执行（不隐藏难例）\n");
foreach (var row in rows.Where(r => !r.Passed))
    md.AppendLine($"- {row.Id}/{row.Variant} [L{row.Difficulty}; {string.Join(',', row.ReasoningTypes)}]: {row.Query}；route={row.ActualRoute}, status={row.Status}, groups={row.CoveredFactGroups}/{row.RequiredFactGroups}");
await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), md.ToString(), new UTF8Encoding(false));
Console.WriteLine(md.ToString());
if (args.Contains("--live")) await LiveEvaluation.RunAsync(cases, service, summaries, outDir, json,
    Option("--live-limit"), args.Contains("--live-all-variants"));
return checks.Any(c => !c.Passed && !c.Skipped) || args.Contains("--strict") && gates.Any(g => !g.Value) ? 1 : 0;

/// <summary>
/// 两级召回的**指定测法**（第三章具名策略）。离线、确定性、零 LLM 调用。
///
/// 为什么不能只靠 `--strategy` 那一栏：那一栏是**单次调用**，而本臂的机制是
/// 「上一级的命中决定第二级的范围」。实测证据：矩阵里 ChapterScope 的四个指标
/// 与 Baseline **逐项相同**——摘要级章节提示在 TopK=5 下从来没进过前五，
/// 所以单次调用等于什么都没做。**测法与机制不匹配时，指标只会给出假的「无差别」。**
///
/// 这里的测法严格照 ReAct 循环里的 `ScopeFor` 复刻：
/// 第一级 Baseline 定位句子 → 取证据锚点/上下文落在的章节（去重、上限 3）→ 第二级 ChapterScope 扩章节。
/// 分母只算「有金标事实组且可答」的执行，与分层口径一致。
/// </summary>
static async Task<object> RunTwoLevelProbeAsync(BenchmarkCase[] cases, StoryRagService service)
{
    var executions = 0; var withGold = 0;
    var complete1 = 0; var complete2 = 0; var gained = 0; var lost = 0; var scopeEmpty = 0;
    var groups1Total = 0; var groups2Total = 0; var goldTotal = 0;
    foreach (var c in cases)
    {
        if (c.Unanswerable || c.EvidenceGroups.Length == 0) continue;
        withGold++;
        for (var variant = 0; variant < c.Queries.Length; variant++)
        {
            executions++;
            var level1 = await service.RetrieveAsync(c.Queries[variant], c.History, default, RetrievalStrategy.Baseline);
            var scope = level1.Evidence.SelectMany(e => e.Context.Prepend(e.Anchor))
                .Select(l => l.ChapterId).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal).Take(3).ToArray();
            if (scope.Length == 0) scopeEmpty++;
            var level2 = await service.RetrieveAsync(c.Queries[variant], c.History, default,
                RetrievalStrategy.ChapterScope, scope);
            var context1 = level1.Evidence.SelectMany(e => e.Context).Select(l => l.EvidenceId).ToHashSet();
            var context2 = level2.Evidence.SelectMany(e => e.Context).Select(l => l.EvidenceId).ToHashSet();
            var g1 = c.EvidenceGroups.Count(g => g.Any(context1.Contains));
            var g2 = c.EvidenceGroups.Count(g => g.Any(context2.Contains));
            groups1Total += g1; groups2Total += g2; goldTotal += c.EvidenceGroups.Length;
            var all1 = g1 >= c.EvidenceGroups.Length;
            var all2 = g2 >= c.EvidenceGroups.Length;
            if (all1) complete1++;
            if (all2) complete2++;
            if (!all1 && all2) gained++;
            if (all1 && !all2) lost++;
        }
    }
    return new
    {
        cases_with_gold = withGold, executions,
        // 第一级 = 只做 Baseline；第二级 = 加上章节扩召回。两者用同一批执行，可直接比。
        fact_groups_complete_level1 = complete1,
        fact_groups_complete_level2 = complete2,
        // 「本来不全、第二级补全了」与「本来全、第二级反而弄丢」必须分开计：
        // 只报净值会把两种相反的失效混成一个数。
        gained_by_second_level = gained,
        lost_by_second_level = lost,
        scope_empty = scopeEmpty,
        group_coverage_level1 = goldTotal == 0 ? 0 : groups1Total / (double)goldTotal,
        group_coverage_level2 = goldTotal == 0 ? 0 : groups2Total / (double)goldTotal,
    };
}

/// <summary>
/// 在线检索循环（只到「证据池」为止，不起草答案）。
/// 它测的是**检索侧机制**：拆了几个子任务、跑了几轮、补了几次差、事实组进来了几组。
/// **这里不做判官**：没有答案可评，硬评只能退化成给证据打分，而证据支持度是知识库层的
/// 确定性职责（见 <see cref="RunAgentEvaluationAsync"/> 与 ImmersionJudge 的类注释）。
/// </summary>
static async Task<object> RunReactEvaluationAsync(BenchmarkCase[] cases, StoryRagService service, string root,
    JsonSerializerOptions json, string? limitText)
{
    var limit = int.TryParse(limitText, out var parsed) ? Math.Clamp(parsed, 1, cases.Length) : cases.Length;
    var llm = new DeepSeekLlmProvider(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")!);
    var tool = new StoryKnowledgeTool(service);
    var loop = new ReactRetrievalLoop(tool, llm, new ReactRetrievalOptions { EnableLlmPlanner = true });
    var rows = new List<object>();
    var plannerTasks = 0; var parallelBatches = 0; var repairs = 0; var covered = 0;
    var allArms = new List<string>(); var allSources = new List<string>(); var multiArmCases = 0;
    foreach (var c in cases.Take(limit))
    {
        var query = c.Queries[0];
        BackendTrace.SetTurnLabel($"case {c.Id} · react-only");
        var started = Stopwatch.StartNew();
        var result = await loop.RunAsync(query, c.History);
        started.Stop();
        if (result is null) continue;
        plannerTasks += result.Steps.Count;
        parallelBatches += result.Steps.GroupBy(s => s.Round).Count(g => g.Count() > 1);
        repairs += result.Steps.Count(s => s.Query.Contains("补查", StringComparison.Ordinal));
        var caseArms = result.Steps.Select(s => s.Strategy?.ToString() ?? "default").ToArray();
        allArms.AddRange(caseArms);
        allSources.AddRange(result.Steps.Select(s => s.StrategySource));
        if (caseArms.Distinct(StringComparer.Ordinal).Count() > 1) multiArmCases++;
        var context = result.EvidencePool.Evidence.SelectMany(e => e.Context).Select(x => x.EvidenceId).ToHashSet();
        var groups = c.EvidenceGroups.Count(g => g.Any(context.Contains));
        covered += groups;
        rows.Add(new
        {
            id = c.Id, query, status = result.EvidencePool.Status.ToString(), steps_total = result.Steps.Count,
            // 轮次口径必须拆开：原来只有一个 rounds = Steps.Count，把同一轮的并行任务与补查步
            // 全都读成"轮"，我上一轮就是这样误读的（3 个 r1 被当成 3 轮）。
            loop_rounds = result.Steps.Where(s => !s.Query.Contains("补查", StringComparison.Ordinal))
                .Select(s => s.Round).DefaultIfEmpty(0).Max(),
            parallel_batches = result.Steps.Where(s => !s.Query.Contains("补查", StringComparison.Ordinal))
                .GroupBy(s => s.Round).Count(g => g.Count() > 1),
            repair_steps = result.Steps.Count(s => s.Query.Contains("补查", StringComparison.Ordinal)),
            // 多跳标签随行落盘，这样不必新增 CLI 开关就能事后把样本过滤到多跳子集。
            reasoning_types = c.ReasoningTypes,
            evidence = result.EvidencePool.Evidence.Count, covered_groups = groups, gold_groups = c.EvidenceGroups.Length,
            repairs = result.Steps.Count(s => s.Query.Contains("补查", StringComparison.Ordinal)), elapsed_ms = started.Elapsed.TotalMilliseconds,
            steps = result.Steps,
            domain = c.Domain, expected_route = c.Route, actual_route = result.EvidencePool.Plan.Route,
            expected_plan = c.ExpectedPlan, source_refs = c.SourceRefs, rubric = c.Rubric
        });
    }
    return new
    {
        enabled = true, provider = llm.Name, cases = rows.Count,
        planner_task_count = plannerTasks, average_tasks = rows.Count == 0 ? 0 : plannerTasks / (double)rows.Count,
        parallel_batches = parallelBatches, repair_queries = repairs,
        // 臂的在线留痕（同 --agent-live 的口径，便于两条路径对照）。
        steps_total = allArms.Count,
        agent_chosen_steps = allSources.Count(s => s == "agent"),
        cases_with_multiple_arms = multiArmCases,
        arm_usage = CountBy(allArms),
        arm_sources = CountBy(allSources),
        evidence_group_coverage = covered / (double)Math.Max(1, cases.Take(limit).Sum(c => c.EvidenceGroups.Length)),
        rows
    };
}

/// <summary>
/// 完整 ReAct 回合的在线评测：检索 → 起草 → 沉浸判定（必要时换检索词重来）→ 输出答案。
///
/// 这是「实际指标只能在真实 ReAct 环境里量」的落点，也是判官唯一有资格工作的场合：
/// 只有走到这一步才存在**答案**，判官才能评沉浸与连贯。
///
/// 每个用例都新建一个 actor：检索记忆与对话历史是跨轮累积的，复用同一个实例会让
/// 「上一个用例的上下文」混进这一轮，在线多轮指标就变成了串味的产物。
/// </summary>
static async Task<object> RunAgentEvaluationAsync(BenchmarkCase[] cases, StoryRagService service, string root,
    JsonSerializerOptions json, string? limitText)
{
    var limit = int.TryParse(limitText, out var parsed) ? Math.Clamp(parsed, 1, cases.Length) : cases.Length;
    var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")!;
    // 演员用默认温度（扮演需要多样性），判官固定 temperature=0（判分必须可复现）。
    // 判官实例只建一次：每个用例新建会连带每例一个 HttpClient，属于自找的套接字压力。
    var judgeLlm = new DeepSeekLlmProvider(key, temperature: 0);
    // 演员的 provider 无状态（只有 HttpClient + key），可以在用例之间共享；
    // 每次新建一个 agent 才是隔离记忆与历史的正确做法（见方法注释）。
    var actorLlm = new DeepSeekLlmProvider(key);
    var persona = PersonaLoader.Load(Path.Combine(root, "data/persona/hutao"));
    var tool = new StoryKnowledgeTool(service);
    var rows = new List<AgentRow>();
    foreach (var c in cases.Take(limit))
    {
        var query = c.Queries[0];
        // 每次记录都贴上「哪个用例」——否则一晚上的日志里几百个回合只有时间戳能区分。
        BackendTrace.SetTurnLabel($"case {c.Id} · {(c.ReasoningTypes.Length == 0 ? c.Category : string.Join('+', c.ReasoningTypes))}");
        var actor = new ReactAgent(persona, actorLlm, null, [tool]);
        actor.RestoreHistory(c.History);
        var started = Stopwatch.StartNew();
        TextResult turn;
        try
        {
            turn = await actor.GenerateTextAsync(query, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 单例失败不能把整轮评测打死，但也不能静默：落一行 agent-failed。
            rows.Add(new AgentRow
            {
                Id = c.Id, Query = query, Reply = "", ReasoningTypes = c.ReasoningTypes,
                ExpectedRoute = c.Route.ToString(), ActualRoute = "failed", Status = "failed",
                JudgeVerdict = "judge-failed", JudgeReason = ex.GetType().Name, ElapsedMs = started.Elapsed.TotalMilliseconds,
            });
            continue;
        }
        started.Stop();

        var evidence = turn.Story?.Evidence ?? [];
        var contextIds = evidence.SelectMany(e => e.Context).Select(x => x.EvidenceId)
            .Concat(evidence.Select(e => e.Id)).ToHashSet(StringComparer.Ordinal);
        var groups = c.EvidenceGroups.Count(g => g.Any(contextIds.Contains));
        var steps = turn.Retrieval?.Steps ?? [];
        var judged = await JudgeAnswerAsync(judgeLlm, persona, c, query, turn.Reply).ConfigureAwait(false);
        var evidenceText = string.Join('\n', evidence.SelectMany(e => e.Context.Prepend(e.Anchor))
            .Select(l => l.Text).Distinct(StringComparer.Ordinal));
        rows.Add(new AgentRow
        {
            Id = c.Id, Query = query, Reply = turn.Reply, ReasoningTypes = c.ReasoningTypes,
            // 起草稿（过闸门之前用户看不到的那一版）与最终答复是**两个不同的产物**：
            // 闸门重写后 storyAnswer 的 Path/Issues 已经不再描述用户看到的那段文字。
            // 不把两版都落盘，就会把「composer 判 fallback」与「用户读到完整回答」读成同一条数据。
            Draft = turn.StoryAnswer?.Reply ?? "",
            EvidenceText = evidenceText.Length > 4000 ? evidenceText[..4000] : evidenceText,
            ReplyEvidenceOverlap = BigramOverlap(turn.Reply, evidenceText),
            DraftEvidenceOverlap = BigramOverlap(turn.StoryAnswer?.Reply ?? "", evidenceText),
            ExpectedRoute = c.Route.ToString(), ActualRoute = (turn.Story?.Plan.Route ?? StoryRoute.Bypass).ToString(),
            Status = (turn.Story?.Status ?? StoryStatus.Bypass).ToString(),
            LoopRounds = steps.Select(s => s.Round).DefaultIfEmpty(0).Max(),
            QueryCount = steps.Select(s => s.Query).Distinct(StringComparer.Ordinal).Count(),
            Queries = steps.Select(s => s.Query).ToArray(),
            // 臂与「谁选的臂」逐步落盘。**这是「agent 自主性」唯一的在线证据**：
            // 只看最终证据池的话，「agent 逐子问题选了不同通道」与「配置里写死了一个臂」
            // 长得完全一样。`default` 表示该步没点名也没配置，用的是服务默认臂。
            Arms = steps.Select(s => s.Strategy?.ToString() ?? "default").ToArray(),
            ArmSources = steps.Select(s => s.StrategySource).ToArray(),
            // 整条召回链路的原始步骤（含每一步真正召回的证据）。
            // 没有它，轨迹只能被读成「几轮、几条、覆盖率多少」的摘要，
            // 既无法复盘也没法可视化——而「召回链路」恰恰是要看每一步做了什么。
            Steps = steps.ToArray(),
            RepairSteps = steps.Count(s => s.Query.Contains("补查", StringComparison.Ordinal)),
            Evidence = evidence.Count, CoveredGroups = groups, GoldGroups = c.EvidenceGroups.Length,
            GatePassed = turn.Immersion?.Passed, GateKinds = turn.Immersion?.KindsSummary ?? "",
            GateCoherence = turn.Immersion?.Coherence, GateSource = turn.Immersion?.Source ?? "",
            GatePath = turn.ImmersionPath ?? "",
            AnswerPath = turn.StoryAnswer?.Path ?? "", AnswerValidated = turn.StoryAnswer?.Validated,
            AnswerIssues = turn.StoryAnswer?.Issues.ToArray() ?? [],
            JudgeScore = judged.Score, JudgeVerdict = judged.Verdict, JudgeReason = judged.Reason,
            ElapsedMs = started.Elapsed.TotalMilliseconds,
        });
    }
    return new
    {
        enabled = true, provider = "deepseek", cases = rows.Count,
        gate_passed = rows.Count(r => r.GatePassed == true),
        gate_failed = rows.Count(r => r.GatePassed == false),
        // 闸门路径必须单独落盘：只报 Passed 会把「评审层抓到并重写」读成「一次通过」。
        gate_paths = CountBy(rows.Select(r => r.GatePath)),
        // 答案契约侧（事实与结构，由知识库层确定性判定，不是判官给的）：
        // fallback 说明这一轮**没能按契约作答**，无论它读起来多像角色在说话。
        answer_paths = CountBy(rows.Select(r => r.AnswerPath)),
        // 闸门是否改写了起草稿。改写本身不是错（出戏必须修），但它有一个必须被看见的副作用：
        // 改写后的文字由人设提示词重写而成，**不经过 composer 的引用/数字校验**，
        // 于是 answer_path / answer_issues 描述的是那一版已作废的草稿。
        draft_rewritten = rows.Count(r => r.Draft.Length > 0 && !string.Equals(r.Draft, r.Reply, StringComparison.Ordinal)),
        // 答复与证据的重合度（代理量）。分组对比才有意义：绝对值不代表有据/无据。
        overlap = new
        {
            contract_validated = Mean(rows.Where(r => r.AnswerPath is "llm-validated-structure" or "direct-quote")
                .Select(r => r.ReplyEvidenceOverlap)),
            fallback_rewritten = Mean(rows.Where(r => r.AnswerPath.Contains("fallback", StringComparison.Ordinal))
                .Select(r => r.ReplyEvidenceOverlap)),
            fallback_draft = Mean(rows.Where(r => r.AnswerPath.Contains("fallback", StringComparison.Ordinal))
                .Select(r => r.DraftEvidenceOverlap)),
        },
        fact_group_coverage = rows.Sum(r => r.CoveredGroups) / (double)Math.Max(1, rows.Sum(r => r.GoldGroups)),
        judge_mean_score = rows.Count == 0 ? 0 : rows.Average(r => r.JudgeScore),
        judge_immersive = rows.Count(r => r.JudgeVerdict == "immersive"),
        judge_minor_break = rows.Count(r => r.JudgeVerdict == "minor-break"),
        judge_broken = rows.Count(r => r.JudgeVerdict == "broken"),
        judge_failed = rows.Count(r => r.JudgeVerdict == "judge-failed"),
        // 「agent 自主性」的在线计量。**只报最终证据池是看不出来的**：
        //   · cases_with_agent_choice —— 有多少个回合里 agent 真的点名了臂；
        //   · cases_with_multiple_arms —— 有多少个回合里出现了不只一个臂（＝确实在逐子问题选，
        //     而不是全程一个臂）；这两个都为 0 就说明自主性只是纸面上的。
        //   · 也覆盖不到「选得对不对」——那是各臂的分层指标该回答的问题。
        steps_total = rows.Sum(r => r.Arms.Length),
        agent_chosen_steps = rows.Sum(r => r.ArmSources.Count(s => s == "agent")),
        cases_with_agent_choice = rows.Count(r => r.ArmSources.Contains("agent")),
        cases_with_multiple_arms = rows.Count(r => r.Arms.Distinct(StringComparer.Ordinal).Count() > 1),
        arm_usage = CountBy(rows.SelectMany(r => r.Arms)),
        arm_sources = CountBy(rows.SelectMany(r => r.ArmSources)),
        // 分层标准**由评测本体定义并落盘**，不让「哪一层看哪个指标」只活在外部脚本里：
        //   multi_hop  单跳查不到，只能靠在线多轮查询把依赖事实补齐 → 看多轮率与事实完整率；
        //   single_shot 一次召回就该够                        → 看事实完整率；
        //   no_retrieval 本来就不该检索                        → 看有没有越权检索。
        // 这三层的**合格线本来就不该是同一个数字**，统一标准只会让每层都测不准。
        strata = Metrics.Strata(rows),
        by_reasoning_type = rows.SelectMany(r => r.ReasoningTypes.Distinct().Select(t => (Type: t, Row: r)))
            .GroupBy(x => x.Type).ToDictionary(g => g.Key, g => Metrics.Summarize(g.Select(x => x.Row))),
        by_route = rows.GroupBy(r => r.ExpectedRoute).ToDictionary(g => g.Key, g => Metrics.Summarize(g)),
        rows
    };
}

/// <summary>
/// 在线判官：只评答案的沉浸式标准、上下文连贯、逻辑连贯，**不评价事实有效性**。
/// 判据、裁决口径与判定规则全在 <see cref="ImmersionJudge"/> 里，这里只负责组装输入。
///
/// 刻意不把证据原文与金标 rubric 塞进提示词：判官看不到证据，就不可能去评价证据支持度。
/// </summary>
static async Task<(double Score, string Verdict, string Reason)> JudgeAnswerAsync(
    ILLMProvider llm, PersonaProfile persona, BenchmarkCase c, string query, string answer)
{
    var history = new List<ChatMessage>();
    foreach (var message in c.History.TakeLast(8))
        history.Add(message);
    history.Add(new ChatMessage("assistant", answer));
    history.Add(new ChatMessage("user", ImmersionJudge.Instruction(query, isProactive: false)));
    try
    {
        var raw = await llm.CompleteAsync(
            ImmersionJudge.SystemPrompt(persona.Name, persona.SystemPrompt), history, CancellationToken.None)
            .ConfigureAwait(false);
        return ImmersionJudge.Parse(raw);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // 判官自身故障必须显式记为 judge-failed，不能静默算 0 分——否则「判官没跑」会被读成「答得差」。
        return (0, "judge-failed", ex.GetType().Name);
    }
}

/// <summary>
/// 按取值计数，并把空值归到 <c>none</c>。
///
/// **空字符串键不是审美问题**：`{"": 12}` 会让 Windows PowerShell 5.1 的 `ConvertFrom-Json`
/// 直接抛 `value of argument "name" is not valid`，整个报告在脚本侧变成不可读——
/// 本轮就踩了（`gate_paths` 里混进了"闸门未接线"的用例，键为空）。
/// </summary>
static Dictionary<string, int> CountBy(IEnumerable<string> values) => values
    .Select(v => string.IsNullOrWhiteSpace(v) ? "none" : v)
    .GroupBy(v => v, StringComparer.Ordinal)
    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

/// <summary>
/// 字符二元组 Jaccard 重合度，用来做「这段文字与本轮检索到的证据有多重合」的**代理量**。
///
/// 它**不能证明**答案无据（改写、概括、口语化都会压低重合度），只能用来对比：
/// 如果「被闸门重写过的那些行」与证据的重合度系统性低于「通过契约校验的那些行」，
/// 那至少说明重写后的文字不是从证据来的，而是从别处（人设提示词）来的。
/// 这正是本轮实测要回答的问题，所以先把这个代理量落盘，而不是靠感觉断言。
/// </summary>
static double BigramOverlap(string text, string reference)
{
    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(reference))
        return 0;
    var a = Bigrams(text);
    var b = Bigrams(reference);
    if (a.Count == 0 || b.Count == 0) return 0;
    var shared = a.Count(b.Contains);
    return shared / (double)(a.Count + b.Count - shared);
}

static HashSet<string> Bigrams(string value)
{
    var result = new HashSet<string>(StringComparer.Ordinal);
    var normalized = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
    for (var i = 0; i + 1 < normalized.Length; i++)
        result.Add(normalized.Substring(i, 2));
    return result;
}

static double Mean(IEnumerable<double> values)
{
    var a = values.ToArray();
    return a.Length == 0 ? 0 : a.Average();
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

/// <summary>
/// 在线单例结果。字段刻意分成四组，因为它们由**不同层**负责、不许互相替代：
/// 检索机制（轮次/查询数/补查）、知识库层（证据与金标事实组覆盖）、
/// 闸门判定（沉浸硬约束）、判官判定（沉浸与连贯的能力评价）。
/// </summary>
internal sealed record AgentRow
{
    public string Id { get; init; } = "";
    public string Query { get; init; } = "";
    /// <summary>用户最终读到的文字（过完沉浸闸门之后）。</summary>
    public string Reply { get; init; } = "";
    /// <summary>起草稿（composer 产出、闸门之前的那一版）。两者不同即为「闸门重写过」。</summary>
    public string Draft { get; init; } = "";
    /// <summary>本轮检索到的证据原文（截断），供离线核对答复与证据的重合关系。</summary>
    public string EvidenceText { get; init; } = "";
    /// <summary>最终答复与证据的二元组重合度（代理量，不是无据的证明）。</summary>
    public double ReplyEvidenceOverlap { get; init; }
    /// <summary>起草稿与证据的二元组重合度。</summary>
    public double DraftEvidenceOverlap { get; init; }
    public string[] ReasoningTypes { get; init; } = [];
    public string ExpectedRoute { get; init; } = "";
    public string ActualRoute { get; init; } = "";
    public string Status { get; init; } = "";
    public int LoopRounds { get; init; }
    public int QueryCount { get; init; }
    public string[] Queries { get; init; } = [];
    /// <summary>逐步生效的臂名（<c>default</c> = 服务默认臂）。</summary>
    public string[] Arms { get; init; } = [];
    /// <summary>逐步「谁定的臂」：agent / configured / default。</summary>
    public string[] ArmSources { get; init; } = [];
    /// <summary>整条召回链路（每一步的查询、臂、状态与真正召回的证据）。</summary>
    public ReactRetrievalStep[] Steps { get; init; } = [];
    public int RepairSteps { get; init; }
    public int Evidence { get; init; }
    public int CoveredGroups { get; init; }
    public int GoldGroups { get; init; }
    public bool? GatePassed { get; init; }
    public string GateKinds { get; init; } = "";
    public double? GateCoherence { get; init; }
    public string GateSource { get; init; } = "";
    public string GatePath { get; init; } = "";
    public string AnswerPath { get; init; } = "";
    public bool? AnswerValidated { get; init; }
    public string[] AnswerIssues { get; init; } = [];
    public double JudgeScore { get; init; }
    public string JudgeVerdict { get; init; } = "";
    public string JudgeReason { get; init; } = "";
    public double ElapsedMs { get; init; }
}

/// <summary>
/// 在线分层聚合。<c>MultiRoundRate</c> 与 <c>FactCompleteRate</c> 分开，
/// 是因为「多问了」与「问到了」是两件事：多轮但没问到，说明查询改写没用；
/// 一轮就问到，说明这条根本不需要多跳，不该按多跳的标准去要求它。
///
/// <c>Answered</c> 只算**真的按契约作答**的路径：<c>StoryAnswerComposer.Fallback</c> 也会返回
/// <c>Validated=true</c>（兜底台词本身是安全的），所以 `Validated` 当「作答率」用会把
/// 「唔…让我再理一理」这种没有内容的兜底读成答对了。
/// </summary>
internal sealed record AgentSummary(int Count, double MeanRounds, double MeanQueries,
    double MultiRoundRate, double? FactGroupCoverage, double FactCompleteRate,
    int GatePassed, int GateFailed, int GateCriticCaught, int GateBlocked,
    int Answered, int AnswerFallback,
    double MeanJudgeScore, double ImmersiveRate, int Immersive, int MinorBreak, int Broken, int JudgeFailed,
    double UnnecessaryRetrievalRate);
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

    /// <summary>
    /// 在线分层聚合。<paramref name="MultiRoundRate"/> 与 <paramref name="FactCompleteRate"/> 分开，
    /// 是因为「多问了」与「问到了」是两件事：多轮但没问到，说明查询改写没用；
    /// 一轮就问到，说明这条根本不需要多跳，不该按多跳的标准去要求它。
    /// </summary>
    internal static AgentSummary Summarize(IEnumerable<AgentRow> input)
    {
        var rows = input.ToArray();
        var withGold = rows.Where(r => r.GoldGroups > 0).ToArray();
        return new AgentSummary(
            rows.Length,
            rows.Length == 0 ? 0 : rows.Average(r => (double)r.LoopRounds),
            rows.Length == 0 ? 0 : rows.Average(r => (double)r.QueryCount),
            rows.Length == 0 ? 0 : rows.Average(r => r.LoopRounds >= 2 ? 1d : 0),
            withGold.Length == 0 ? null : withGold.Sum(r => r.CoveredGroups) / (double)withGold.Sum(r => r.GoldGroups),
            withGold.Length == 0 ? 0 : withGold.Average(r => r.CoveredGroups >= r.GoldGroups ? 1d : 0),
            rows.Count(r => r.GatePassed == true), rows.Count(r => r.GatePassed == false),
            // 「评审层当场抓到并重写」与「规则层一次通过」是完全不同的两件事，必须分开计。
            rows.Count(r => r.GatePath.Contains("critic-repaired", StringComparison.Ordinal)),
            rows.Count(r => r.GatePath.Contains("blocked", StringComparison.Ordinal)),
            rows.Count(r => r.AnswerPath is "llm-validated-structure" or "direct-quote"),
            rows.Count(r => r.AnswerPath.Contains("fallback", StringComparison.Ordinal)),
            rows.Length == 0 ? 0 : rows.Average(r => r.JudgeScore),
            rows.Length == 0 ? 0 : rows.Average(r => r.JudgeVerdict == "immersive" ? 1d : 0),
            rows.Count(r => r.JudgeVerdict == "immersive"), rows.Count(r => r.JudgeVerdict == "minor-break"),
            rows.Count(r => r.JudgeVerdict == "broken"), rows.Count(r => r.JudgeVerdict == "judge-failed"),
            rows.Length == 0 ? 0 : rows.Average(r =>
                r.ExpectedRoute != nameof(StoryRoute.Retrieve) && r.ActualRoute == nameof(StoryRoute.Retrieve) ? 1d : 0));
    }

    /// <summary>
    /// 三层各自的标准。分层的依据是**这一层要靠什么才能完成**，不是用例难度：
    /// 多跳单次召回原理上完成不了，所以它的合格线只能画在「在线多轮有没有真的发生、事实补没补齐」上；
    /// 一次召回就该够的用例，画在多轮上反而是放水。
    /// </summary>
    internal static object Strata(IReadOnlyList<AgentRow> rows) => new
    {
        multi_hop = Summarize(rows.Where(r => r.ReasoningTypes.Contains("dependent-retrieval", StringComparer.Ordinal))),
        single_shot_retrieval = Summarize(rows.Where(r =>
            r.ExpectedRoute == nameof(StoryRoute.Retrieve) &&
            !r.ReasoningTypes.Contains("dependent-retrieval", StringComparer.Ordinal))),
        no_retrieval = Summarize(rows.Where(r => r.ExpectedRoute != nameof(StoryRoute.Retrieve))),
    };
}
