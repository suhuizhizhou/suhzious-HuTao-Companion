using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Immersion;
using HuTao.Dialogue.Llm;
using HuTao.Dialogue.Tools;
using HuTao.Foundation;
using HuTao.Foundation.Diagnostics;
using HuTao.Knowledge.Memory;
using HuTao.Knowledge.Rag;
using HuTao.Persona;

internal static class CompanionLiveEvaluation
{
    private sealed record Scenario(string Id, string[] Seeds, string[] Inputs);
    public static async Task<int> RunAsync(string root, string output, JsonSerializerOptions json)
    {
        ProjectEnvironment.LoadDotEnv([Path.Combine(root, ".env")]);
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("DEEPSEEK_API_KEY is required.");
        Directory.CreateDirectory(output);
        BackendTrace.Configure(Path.Combine(output, "backend-trace.log"), enabled: true);
        var persona = PersonaLoader.Load(Path.Combine(root, "data/persona/hutao"));
        var service = new StoryRagService(Path.Combine(root, "data/story/dialogue"),
            StoryVectorStore.Load(Path.Combine(root, "data/story/index.json")), characterLexicon: persona.EffectiveLexicon);
        await service.WarmupAsync();
        var scenarios = new Scenario[]
        {
            new("drink", ["我喝咖啡会心悸，平时只喝无糖乌龙茶。"],
                ["写了好久，想喝点东西再继续。", "改啦，我最近喝茶也睡不着，现在晚上只喝温水。", "那今晚你给我挑什么喝？"]),
            new("promise", ["我们约好周六晚上一起改简历，我投的是嵌入式岗位。"],
                ["还记得我们约好做什么吗？"]),
            new("craft", ["我在给妹妹做一条毕业礼物围巾，已经拆了三遍，总想快点做好。"],
                ["我的手工又拆了。你帽子边上那朵花也是自己做的吗，怎么弄的？", "那得等多久才能戴上呀？", "先不聊这个，帮我算一下17乘23。"]),
            new("comfort", ["我的毕业论文研究单目深度估计，导师让我补消融实验。"],
                ["论文又卡住了，今天有点难受，陪我坐一会儿吧。"]),
            new("unknown", [], ["我没告诉过你我的小学叫什么吧，你知道吗？"])
        };
        var rows = new List<object>();
        var markdown = new StringBuilder("# 桌宠真实对话召回对照\n\n相同人设、相同模型、独立会话。full 使用合成用户记忆与本机剧情；no-recall 关闭两种召回。不是旧版对照，也不代表统计显著性。\n\n");
        var errors = 0;
        foreach (var scenario in scenarios)
        foreach (var mode in new[] { "full", "no-recall" })
        {
            var memoryPath = Path.Combine(output, $"{scenario.Id}-{mode}-{Guid.NewGuid():N}.json");
            var store = new ConversationMemoryStore(memoryPath, new MemoryOptions { EnableConsolidation = false });
            foreach (var seed in scenario.Seeds) store.ObserveTurn("user", seed, DateTimeOffset.Now.AddDays(-2));
            var llm = new DeepSeekLlmProvider(key, temperature: .3);
            var agent = new ReactAgent(persona, llm, null, mode == "full" ? [new StoryKnowledgeTool(service)] : [],
                memory: mode == "full" ? store : null,
                immersionOptions: new ImmersionOptions { EnableRuleGate = true, EnableCritic = true });
            markdown.AppendLine($"## {scenario.Id} / {mode}\n");
            foreach (var input in scenario.Inputs)
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    var result = await agent.GenerateTextAsync(input, false, deadline.Token);
                    rows.Add(new { scenario = scenario.Id, mode, input, seeds = scenario.Seeds,
                        result.Reply, elapsed_ms = timer.ElapsedMilliseconds, result.Recall,
                        result.StoryAnswer, result.Immersion, result.ImmersionPath });
                    markdown.AppendLine($"用户：{input}\n\n胡桃：{result.Reply}\n\n" +
                        $"召回：{result.Recall?.Memories.Count ?? 0} 条个人记忆 / {result.Story?.Evidence.Count ?? 0} 条剧情；" +
                        $"作答：{result.StoryAnswer?.Path ?? "conversation"}；耗时：{timer.Elapsed.TotalSeconds:F1}s\n");
                    Console.WriteLine($"{scenario.Id}/{mode}: {timer.Elapsed.TotalSeconds:F1}s {result.Reply.Replace('\n', ' ')}");
                }
                catch (Exception ex)
                {
                    errors++;
                    rows.Add(new { scenario = scenario.Id, mode, input, error = ex.GetType().Name });
                    Console.WriteLine($"{scenario.Id}/{mode}: ERROR {ex.GetType().Name}");
                }
                await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(rows, json));
                await File.WriteAllTextAsync(Path.Combine(output, "conversation.md"), markdown.ToString());
            }
        }
        return errors == 0 ? 0 : 1;
    }
}
