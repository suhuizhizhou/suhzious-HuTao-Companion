using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Immersion;
using HuTao.Dialogue.Llm;
using HuTao.Dialogue.Tools;
using HuTao.Foundation;
using HuTao.Foundation.Abstractions;
using HuTao.Persona;

internal static class ToolLiveEvaluation
{
    private sealed record Scenario(string Id, string Input, string? Tool, ToolRunStatus Status, string? ReplyContains, string? OutputContains = null);

    public static async Task<int> RunAsync(string root, string output, JsonSerializerOptions json)
    {
        ProjectEnvironment.LoadDotEnv([Path.Combine(root, ".env")]);
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("DEEPSEEK_API_KEY is required.");
        Directory.CreateDirectory(output);
        var workspace = Path.Combine(output, "workspace-" + Guid.NewGuid().ToString("N"));
        var tools = BuiltinToolCatalog.Create(workspace).ToList();
        tools.Add(new TimeTool());
        await File.WriteAllTextAsync(Path.Combine(workspace, "meeting.txt"), "周六下午三点复盘，会议室是青竹厅。参会人数是七人。");
        var scenarios = new Scenario[]
        {
            new("calculate", "胡桃，帮我准确算一下17乘23，直接告诉我结果。", "calculator", ToolRunStatus.Succeeded, "391|三百九十一", "\"value\":391"),
            new("date", "2024年2月28日再过两天是几月几日？帮我算算。", "date_offset", ToolRunStatus.Succeeded, "3月1|三月一", "2024-03-01"),
            new("read", "读取专用目录里的 meeting.txt，告诉我会议室叫什么。", "file_read", ToolRunStatus.Succeeded, "青竹厅"),
            new("pending_write", "在专用目录创建 note.txt，内容是：周六复盘。", "file_write", ToolRunStatus.ConfirmationRequired, "确认|点头|答应|同意|准许"),
            new("chat", "胡桃，陪我坐一会儿吧。", null, ToolRunStatus.Succeeded, null)
        };
        var rows = new List<object>();
        var markdown = new StringBuilder("# DeepSeek 工具对话验证\n\n合成文件，独立会话，无 TTS。写入场景无审批宿主，必须保持未执行。\n\n");
        var failed = 0;
        foreach (var scenario in scenarios)
        {
            var llm = new DeepSeekLlmProvider(key, temperature: .3);
            var agent = new ReactAgent(PersonaLoader.Load(Path.Combine(root, "data/persona/hutao")), llm, null, tools,
                immersionOptions: new ImmersionOptions { EnableRuleGate = true, EnableCritic = true });
            var timer = Stopwatch.StartNew();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var result = await agent.GenerateTextAsync(scenario.Input, false, deadline.Token);
                var requested = result.ToolRuns.Where(t => t.Tool != "time").ToArray();
                var passed = scenario.Tool is null ? requested.Length == 0 :
                    requested.Any(t => t.Tool == scenario.Tool && t.Status == scenario.Status &&
                        (scenario.OutputContains is null || t.Output.Contains(scenario.OutputContains, StringComparison.Ordinal)));
                passed &= scenario.ReplyContains is null || scenario.ReplyContains.Split('|').Any(part => result.Reply.Contains(part, StringComparison.Ordinal));
                passed &= !File.Exists(Path.Combine(workspace, "note.txt"));
                if (!passed) failed++;
                rows.Add(new { scenario.Id, scenario.Input, passed, result.Reply, result.ToolRuns, result.ImmersionPath,
                    elapsed_ms = timer.ElapsedMilliseconds });
                markdown.AppendLine($"## {scenario.Id}: {(passed ? "PASS" : "FAIL")}\n\n用户：{scenario.Input}\n\n胡桃：{result.Reply}\n\n" +
                    $"执行记录：{string.Join(", ", requested.Select(t => t.Tool + ":" + t.Status))}；耗时 {timer.Elapsed.TotalSeconds:F1}s\n");
                Console.WriteLine($"{scenario.Id}: {(passed ? "PASS" : "FAIL")} {timer.Elapsed.TotalSeconds:F1}s {result.Reply.Replace('\n', ' ')}");
            }
            catch (Exception ex)
            {
                failed++;
                rows.Add(new { scenario.Id, passed = false, error = ex.GetType().Name });
                Console.WriteLine($"{scenario.Id}: ERROR {ex.GetType().Name}");
            }
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(rows, json));
            await File.WriteAllTextAsync(Path.Combine(output, "conversation.md"), markdown.ToString());
        }
        Console.WriteLine($"Live tools: {scenarios.Length - failed}/{scenarios.Length}");
        return failed == 0 ? 0 : 1;
    }
}
