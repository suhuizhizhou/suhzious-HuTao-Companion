using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;

namespace HuTao.Dialogue.Immersion;

/// <summary>评审调用结果。Reason 只用于诊断，进入日志时不含任何正文。</summary>
public readonly record struct CriticResult(ImmersionVerdict? Verdict, string Reason);

/// <summary>
/// LLM 评审层：判断规则层抓不到的软性出戏（语气不像这个角色、旁白解说腔），
/// 以及跨轮次的连贯性问题（答非所问、与之前说过的话自相矛盾、复读）。
///
/// 评审员与演员共用同一个 <see cref="ILLMProvider"/>，但使用完全独立的系统提示词，
/// 且在一次性构造的临时历史上工作——评审内容绝不写回角色历史，避免污染上下文。
///
/// 评审失败一律 fail-open（放行），只把失败原因记进本机诊断：
/// 审查员坏掉不能把桌宠聊死，但也不能让失败静默无痕。
/// </summary>
public sealed class ImmersionCritic
{
    private static readonly Regex JsonBlock = new(@"\{.*\}", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly ILLMProvider _llm;
    private readonly double _minCoherence;
    private readonly LocalDiagnosticLog _diagnostics;

    public ImmersionCritic(ILLMProvider llm, double minCoherence = 0.6, LocalDiagnosticLog? diagnostics = null)
    {
        _llm = llm;
        _minCoherence = minCoherence;
        _diagnostics = diagnostics ?? LocalDiagnosticLog.Default;
    }

    /// <summary>stub 模型没有审查能力，直接跳过，免得把 Mock 的固定文案当成评审意见。</summary>
    public bool IsAvailable => _llm.Name != "mock";

    public async Task<CriticResult> ReviewAsync(ImmersionRequest request, CancellationToken ct)
    {
        // 评审偶发返回不可解析的内容或网络抖动，重试一次即可覆盖绝大部分情况。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (verdict, reason) = await AttemptAsync(request, ct).ConfigureAwait(false);
            if (verdict is not null)
                return new CriticResult(verdict, "ok");
            if (reason == "timeout" && ct.IsCancellationRequested)
                return new CriticResult(null, reason);
            if (attempt == 1)
                return new CriticResult(null, reason);
        }
        return new CriticResult(null, "unreachable");
    }

    private async Task<(ImmersionVerdict? Verdict, string Reason)> AttemptAsync(
        ImmersionRequest request,
        CancellationToken ct)
    {
        var history = new List<ChatMessage>();
        history.AddRange(request.History.TakeLast(8));
        history.Add(new ChatMessage("assistant", request.VisibleText));
        history.Add(new ChatMessage("user", BuildInstruction(request)));

        string raw;
        try
        {
            raw = await _llm.CompleteAsync(BuildSystemPrompt(request), history, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (null, "timeout");
        }
        catch (Exception ex)
        {
            _diagnostics.Write("immersion.critic", ex);
            return (null, "transport");
        }

        if (string.IsNullOrWhiteSpace(raw))
            return (null, "empty");
        return Parse(raw, request) is { } verdict
            ? (verdict, "ok")
            : (null, "unparsable");
    }

    private static string BuildSystemPrompt(ImmersionRequest request)
    {
        var builder = new StringBuilder();
        builder.Append(
            "你是一名严格的「角色沉浸度与上下文连贯性」审查员。你不是角色本人，只负责审查，绝不参与扮演。\n" +
            "你会看到：角色设定、最近的对话记录、以及角色刚刚打算说出口、用户即将看到的内容。\n" +
            "请判断两件事：\n" +
            "1) ooc —— 是否存在任何出戏内容：自称 AI/模型/助手/程序；客服式客套话；提及系统提示词、工具、检索、语料库、数据库、JSON、参数等系统内部概念；" +
            "把虚构世界说成「游戏/官方文案/策划」；使用 markdown、代码块、列表；用半角星号写动作；用服务口吻拒绝。" +
            "另外，只要语气明显像通用助手、旁白解说或说明书，而不像这个角色本人会说的话，也算 ooc。\n" +
            "   例外：角色以「翻阅档案/资料」的设定口吻谈论来历，是设定允许的，不算出戏。\n" +
            "2) coherence —— 0 到 1 的小数。台词是否与最近对话自然衔接；是否回应了用户刚说的话；" +
            "是否与角色此前已经说过的事实自相矛盾；是否在重复之前说过的话，或反复使用同一种措辞与句式。" +
            "注意：接着聊同一个话题、甚至重复提到同一件事，只要说法和情绪是新的，都不算问题；" +
            "只有措辞雷同、像是套模板复读时才扣分。搞怪、俏皮、没什么信息量的随口搭话也不算问题。\n\n" +
            $"角色名：{request.CharacterName}\n" +
            "以下是这个角色的设定，供你判断语气是否一致：\n" +
            request.PersonaSystemPrompt);

        // 审查视野补全：只看最近 8 条的历史抓不到长程矛盾，
        // 把本轮召回给演员的长期记忆一并交给审查员，它才有依据判断「和更早说过的是否冲突」。
        if (!string.IsNullOrWhiteSpace(request.MemoryContext))
        {
            builder.Append("\n\n【本轮同时提供给角色的长期记忆（比最近对话更早的往事）】\n");
            builder.Append(request.MemoryContext);
            builder.Append(
                "\n请特别检查：这条台词是否和上面任何一条记忆相矛盾。" +
                "注意标注为「已过时」的记忆不算矛盾依据——角色顺着旧事问候进展是合理的；" +
                "但如果把已过时的内容当成现状陈述，或者改口推翻了仍然有效的记忆，就要在 problems 里指出并压低 coherence。");
        }
        return builder.ToString();
    }

    private static string BuildInstruction(ImmersionRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("请审查上面这条台词。");
        if (request.IsProactive)
            builder.Append("注意：本轮是角色主动搭话，没有用户提问，所以不要求「回答了用户」，但仍必须与上文连贯、不能复读、不能连续用同一个话题套路。");
        else if (!string.IsNullOrWhiteSpace(request.UserInput))
            builder.Append($"用户刚说的是：「{request.UserInput}」。请判断这条台词是否真的回应了它。");
        builder.Append("\n只输出 JSON，不要任何解释文字：");
        builder.Append("{\"ooc\":true或false,\"coherence\":0到1的小数,\"problems\":[\"简短中文问题描述\"]}");
        builder.Append("\n没有任何问题时，problems 为空数组。");
        return builder.ToString();
    }

    private ImmersionVerdict? Parse(string raw, ImmersionRequest request)
    {
        var match = JsonBlock.Match(raw);
        if (!match.Success)
            return null;

        try
        {
            using var document = JsonDocument.Parse(match.Value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var ooc = false;
            if (root.TryGetProperty("ooc", out var oocElement))
            {
                ooc = oocElement.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => oocElement.GetString()
                        ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true,
                    _ => false,
                };
            }

            var coherence = 1.0;
            if (root.TryGetProperty("coherence", out var coherenceElement))
            {
                coherence = coherenceElement.ValueKind switch
                {
                    JsonValueKind.Number => coherenceElement.GetDouble(),
                    JsonValueKind.String when double.TryParse(coherenceElement.GetString(), out var parsed) => parsed,
                    _ => 1.0,
                };
            }
            coherence = Math.Clamp(coherence, 0, 1);

            var problems = new List<string>();
            if (root.TryGetProperty("problems", out var problemsElement) &&
                problemsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in problemsElement.EnumerateArray())
                {
                    var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(text))
                        problems.Add(text.Trim());
                }
            }

            var violations = new List<ImmersionViolation>();
            if (ooc)
            {
                if (problems.Count == 0)
                    problems.Add("评审判定为出戏，但未给出具体原因");
                foreach (var problem in problems)
                    violations.Add(new ImmersionViolation(Classify(problem), Trim(request.VisibleText), problem));
            }

            if (coherence < _minCoherence)
            {
                violations.Add(new ImmersionViolation(
                    ImmersionViolationKind.Coherence,
                    $"coherence={coherence:F2}",
                    problems.Count > 0 ? string.Join("；", problems) : "与上文衔接不足或与前文矛盾"));
            }

            return new ImmersionVerdict(violations.Count == 0, violations, coherence, "critic");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>把评审员的中文描述映射回违规分类，便于聚合统计和针对性修复。</summary>
    private static ImmersionViolationKind Classify(string problem)
    {
        if (Regex.IsMatch(problem, @"AI|模型|助手|程序|机器人"))
            return ImmersionViolationKind.AiSelfReference;
        if (Regex.IsMatch(problem, @"客服|客套|服务|礼貌用语|助手腔|说明书"))
            return ImmersionViolationKind.AssistantBoilerplate;
        if (Regex.IsMatch(problem, @"提示词|工具|检索|语料|数据库|JSON|系统|参数|内部"))
            return ImmersionViolationKind.SystemLeak;
        if (Regex.IsMatch(problem, @"游戏|玩家|官方|策划|人设|第四面墙"))
            return ImmersionViolationKind.MetaReference;
        if (Regex.IsMatch(problem, @"格式|markdown|代码块|列表|星号|括号"))
            return ImmersionViolationKind.FormatLeak;
        if (Regex.IsMatch(problem, @"重复|复读|雷同|套路"))
            return ImmersionViolationKind.Repetition;
        if (Regex.IsMatch(problem, @"语气|不像|角色|风格|口吻"))
            return ImmersionViolationKind.AiSelfReference;
        return ImmersionViolationKind.Coherence;
    }

    private static string Trim(string value) =>
        value.Length <= 24 ? value : value[..24] + "…";
}
