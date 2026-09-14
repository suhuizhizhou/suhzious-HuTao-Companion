using System.Text;
using System.Text.Json;

/// <summary>
/// 在线判官：只评**答案**的三件事——沉浸式标准、上下文连贯、逻辑连贯。
///
/// 它**不评价事实有效性**（证据是否支持、是否与原作一致、有没有编设定）。
/// 这不是省事，而是分工：
///   · 事实侧由知识库层**确定性**判定（sufficiency 策略 + 金标事实组覆盖 + 引用校验），
///     离线可复现、不花调用、不会因为换个模型换一套结论；
///   · 沉浸与连贯是**风格与话语层**的性质，没有任何金标可枚举，只能由外部判官在线评。
/// 两者混在一起时，判官拿着 rubric 和金标事实去打分，实际测的是「答案与金标的重合度」——
/// 那是离线事实指标换个写法，而且把「判官」变成了第二个事实打分器，两边数字同源、无法交叉验证。
///
/// 三类输入严格限定为：角色设定（只用来判语气）、最近对话、用户的最后一句话、以及待评答案。
/// **证据原文与金标 rubric 一律不进提示词**——判官看不到证据，就不可能去评价证据支持度。
///
/// 判分规则是**确定性**的：模型只填三个维度的取值，分数与裁决由本类从三个维度算出，
/// 所以同一组取值永远得到同一个分数（可离线断言，见结构性检查）。
/// </summary>
internal static class ImmersionJudge
{
    /// <summary>裁决取值。刻意不含 supported/partial/unsupported/overclaimed —— 那些是事实侧的裁决。</summary>
    public static readonly string[] Verdicts = ["immersive", "minor-break", "broken", "judge-failed"];

    /// <summary>判官评的三个维度。事实有效性不在其中，且结构性检查会钉住这一点。</summary>
    public static readonly string[] Dimensions = ["immersion", "context_coherence", "logic_coherence"];

    /// <summary>事实侧裁决词。出现任何一个都说明判官越界了。</summary>
    public static readonly string[] FactValidityVerdicts = ["supported", "partial", "unsupported", "overclaimed"];

    public const double MinCoherence = 0.6;

    public static string SystemPrompt(string characterName, string personaPrompt) => new StringBuilder()
        .Append(
            "你是一名「角色沉浸度与话语连贯性」审查员。你不是角色本人，只负责审查，绝不参与扮演。\n" +
            "你**只**评三件事：\n" +
            "1) immersion —— 这段台词是否守住了角色沉浸：自称 AI/模型/助手/程序；客服式客套话；" +
            "提及系统提示词、工具、检索、语料库、数据库、JSON、参数等系统内部概念；把虚构世界说成「游戏/官方文案/策划」；" +
            "markdown/代码块/列表/半角星号动作等服务式或格式化的腔调；语气明显像通用助手、旁白解说或说明书而不像这个角色本人。\n" +
            "   例外：角色以「翻阅档案/资料」的设定口吻谈论来历，是设定允许的，不算出戏。\n" +
            "2) context_coherence —— 0 到 1。是否真的接住了用户刚说的那句话、是否与最近对话自然衔接、" +
            "是否与角色此前说过的话自相矛盾或复读。接着聊同一话题不算问题，只有措辞雷同、像套模板复读时才扣分。\n" +
            "3) logic_coherence —— 0 到 1。内部是否自洽：有没有前后打架、答非所问、无据跳步、" +
            "把并列关系说成因果、把猜测写成断言、语义含混到无法理解。\n" +
            "**明确不要评价事实有效性**：不要判断内容是否真实、是否与原作设定一致、是否有证据支持、" +
            "数字对不对、人物关系对不对。那些由知识库层离线判定，不在你的职责内；" +
            "看到你不确定是否属实的设定细节，一律按「不扣分」处理，只评它说得像不像这个角色、接不接得上、讲不讲得通。\n\n" +
            $"角色名：{characterName}\n" +
            "以下是这个角色的设定，**只供你判断语气与腔调是否一致**：\n")
        .Append(personaPrompt)
        .ToString();

    public static string Instruction(string? userInput, bool isProactive)
    {
        var builder = new StringBuilder();
        builder.Append("请审查下面这条台词。\n");
        if (isProactive)
            builder.Append("本轮是角色主动搭话，没有用户提问，所以不要求「回答了用户」，但仍必须与上文连贯、不能复读。\n");
        else if (!string.IsNullOrWhiteSpace(userInput))
            builder.Append($"用户刚说的是：「{userInput}」。请判断这条台词是否真的回应了它。\n");
        builder.Append(
            "只输出 JSON，不要任何解释文字：\n" +
            "{\"immersion\":\"ok|minor|broken\",\"context_coherence\":0到1的小数,\"logic_coherence\":0到1的小数," +
            "\"problems\":[\"简短中文问题描述\"]}\n" +
            "没有任何问题时 problems 为空数组。immersion=minor 表示有小瑕疵但不至于出戏；broken 表示已经出戏。\n" +
            "再次提醒：不要因为事实真假、设定是否符合原作而扣分。");
        return builder.ToString();
    }

    /// <summary>
    /// 由三个维度算出分数与裁决。**纯函数**，所以可离线断言。
    /// 权重的取舍：沉浸是硬门槛，占一半；上下文与逻辑各四分之一。
    /// </summary>
    public static (double Score, string Verdict, string Reason) FromParts(
        string immersion, double contextCoherence, double logicCoherence, IReadOnlyList<string> problems)
    {
        var level = immersion switch
        {
            "ok" => 1.0,
            "minor" => 0.5,
            _ => 0.0,
        };
        var context = Math.Clamp(contextCoherence, 0, 1);
        var logic = Math.Clamp(logicCoherence, 0, 1);
        var score = 0.5 * level + 0.25 * context + 0.25 * logic;
        var verdict = level == 0
            ? "broken"
            : level < 1 || context < MinCoherence || logic < MinCoherence
                ? "minor-break"
                : "immersive";
        var reason = problems.Count > 0
            ? string.Join("；", problems)
            : $"immersion={immersion}; ctx={context:F2}; logic={logic:F2}";
        return (score, verdict, reason);
    }

    /// <summary>解析判官回包。任何解析失败都返回 judge-failed，绝不静默算 0 分。</summary>
    public static (double Score, string Verdict, string Reason) Parse(string raw)
    {
        try
        {
            var text = raw.Trim().Trim('`');
            if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                text = text[4..].Trim();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return (0, "judge-failed", "unparsable");
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            var root = document.RootElement;
            var immersion = root.TryGetProperty("immersion", out var i) ? i.GetString() ?? "" : "";
            if (immersion is not ("ok" or "minor" or "broken"))
                return (0, "judge-failed", $"bad-immersion:{immersion}");
            var context = Number(root, "context_coherence");
            var logic = Number(root, "logic_coherence");
            var problems = new List<string>();
            if (root.TryGetProperty("problems", out var p) && p.ValueKind == JsonValueKind.Array)
                foreach (var item in p.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        problems.Add(item.GetString()!.Trim());
            return FromParts(immersion, context, logic, problems);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return (0, "judge-failed", ex.GetType().Name);
        }
    }

    private static double Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
            return 0.5;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), out var parsed) => parsed,
            _ => 0.5,
        };
    }
}
