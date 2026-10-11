/// <summary>纯离线模块回归入口；不加载真实语料、不调用外部模型，不启动图数据库。</summary>
internal static class OfflineCheckRunner
{
    private static readonly IReadOnlyDictionary<string, Func<Task<List<CheckResult>>>> Suites =
        new Dictionary<string, Func<Task<List<CheckResult>>>>
        {
            ["--knowledge-sql-checks"] = KnowledgeSqlAsync,
            ["--tool-checks"] = ToolChecks.RunAsync,
            ["--recall-checks"] = RecallAsync,
            ["--conversation-checks"] = ConversationChecks.RunAsync,
            ["--session-checks"] = SessionChecks.RunAsync,
            ["--speech-checks"] = SpeechDeliveryChecks.RunAsync,
            ["--chatroom-checks"] = ChatRoomChecks.RunAsync,
        };

    public static bool Supports(string[] args) =>
        args.Contains("--offline-checks") || args.Any(Suites.ContainsKey);

    public static async Task<int> RunAsync(string[] args, string root)
    {
        // 防止混用离线与真实评估参数后，调用方误以为两种评估都已执行。
        var unknown = args.Where(a => a != "--offline-checks" && !Suites.ContainsKey(a)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException("离线检查只接受 --offline-checks 或模块 --*-checks 参数，不可混用其他评估参数。");
        var selected = args.Contains("--offline-checks")
            ? Suites.Keys.ToArray()
            : args.Distinct(StringComparer.Ordinal).ToArray();
        var all = new List<CheckResult>();
        foreach (var name in selected)
        {
            Console.WriteLine($"Running {name} (offline)");
            var results = await Suites[name]();
            all.AddRange(results);
            Print(name, results);
        }
        if (args.Contains("--offline-checks"))
        {
            var dependencies = DependencyDirectionChecks.Run(root);
            all.AddRange(dependencies);
            Print("dependencies", dependencies);
        }
        var failed = all.Count(c => !c.Passed && !c.Skipped);
        var skipped = all.Count(c => c.Skipped);
        Console.WriteLine($"Offline total: passed={all.Count(c => c.Passed && !c.Skipped)}, failed={failed}, skipped={skipped}");
        // 2 = 未完整评估，不能误报为全部通过。
        return failed > 0 ? 1 : skipped > 0 ? 2 : 0;
    }

    private static void Print(string name, IReadOnlyList<CheckResult> results)
    {
        foreach (var check in results)
            Console.WriteLine($"{(check.Skipped ? "SKIP" : check.Passed ? "PASS" : "FAIL")} {check.Name} {check.Detail}");
        Console.WriteLine($"{name}: passed={results.Count(c => c.Passed && !c.Skipped)}, failed={results.Count(c => !c.Passed && !c.Skipped)}, skipped={results.Count(c => c.Skipped)}");
    }

    private static async Task<List<CheckResult>> KnowledgeSqlAsync()
    {
        var sqlChecks = await KnowledgeSqlChecks.RunAsync();
        sqlChecks.AddRange(KnowledgeSqlReadChecks.Run());
        sqlChecks.AddRange(KnowledgeFactChecks.Run());
        sqlChecks.AddRange(await KnowledgeRetrievalChecks.RunAsync());
        sqlChecks.AddRange(await KnowledgeConversationChecks.RunAsync());
        sqlChecks.AddRange(await ConversationSupportChecks.RunAsync());
        sqlChecks.AddRange(await KnowledgeDeletionChecks.RunAsync());
        sqlChecks.AddRange(ChatPrivacyChecks.Run());
        sqlChecks.AddRange(await KnowledgeRuntimeChecks.RunAsync());
        sqlChecks.AddRange(await KnowledgeHumanReviewChecks.RunAsync());
        sqlChecks.AddRange(KnowledgeOutboxChecks.Run());
        sqlChecks.AddRange(await KnowledgeExtractionChecks.RunAsync());
        sqlChecks.AddRange(await MemoryChecks.RunAsync(sql: true));
        return sqlChecks;
    }

    private static async Task<List<CheckResult>> RecallAsync()
    {
        var recallChecks = await RecallChecks.RunAsync();
        recallChecks.AddRange(await MemoryChecks.RunAsync());
        return recallChecks;
    }

}
