using System.Text;
using HuTao.Dialogue.Immersion;
using HuTao.Knowledge.Rag;

namespace HuTao.Dialogue.Core;

/// <summary>无副作用的重试约束策略。仅构造提示约束，不调用模型、修改历史或授予权限。</summary>
public static class ResponseRepairPolicy
{
    /// <summary>
    /// 沉浸判定没过时，给 RAG 换一个新的检索提示词。
    ///
    /// 为什么不直接重检索同一个问题：同一句话检索回来的就是同一批证据，重新起草只会得到
    /// 同一类出戏。所以按**违规类型**给检索加一条明确的取材约束，让下一轮去取
    /// 「角色本人的、故事世界内的」材料。
    ///
    /// 这里只影响「怎么去找证据」，不参与任何通过/不通过的判定——
    /// 判定始终只由 <see cref="ImmersionGate"/> 做，这里不是给某类问题开后门。
    /// </summary>
    public static string? ReviseRetrievalQuery(string? input, ImmersionVerdict? verdict)
    {
        if (string.IsNullOrWhiteSpace(input) || verdict is null || verdict.Violations.Count == 0)
            return input;
        var kinds = verdict.Violations.Select(v => v.Kind).ToHashSet();
        var hints = new List<string>();
        // 出戏常见于「没有可用的世界内材料」，模型只好拿系统/元信息口径来凑。
        if (kinds.Overlaps([
                ImmersionViolationKind.AiSelfReference, ImmersionViolationKind.AssistantBoilerplate,
                ImmersionViolationKind.SystemLeak, ImmersionViolationKind.MetaReference,
                ImmersionViolationKind.RefusalTone]))
            hints.Add("角色本人的经历与原话");
        if (kinds.Contains(ImmersionViolationKind.Coherence)) hints.Add("与上文衔接的后续发展");
        if (kinds.Contains(ImmersionViolationKind.Repetition)) hints.Add("换一个还没提过的片段");
        if (kinds.Contains(ImmersionViolationKind.LanguageDrift)) hints.Add("中文原文台词");
        return hints.Count == 0 ? input : input + "；请只依据：" + string.Join('、', hints);
    }

    /// <summary>
    /// 把闸门给的修改要求拼成一段生成约束。
    ///
    /// 最后那条「只依据给出的资料」是**关键**：重写曾经是无证据的自由重写，模型会退回预训练记忆
    /// ——对知名 IP 往往"看着对"，所以错得很隐蔽。带证据重做时必须显式禁止它引用资料之外的事实。
    /// </summary>
    public static string BuildRetryConstraint(
        IReadOnlyList<string> instructions, string? userInput, bool isProactive, bool relaxed)
    {
        var builder = new StringBuilder();
        if (relaxed)
        {
            // 松协议那一次：下方统一剔除格式类要求，
            // 但**内容类要求必须留下**——否则模型只会给一句最保险的短回答（实测 17 字）。
            builder.Append("【本轮格式要求】上一版没有按输出格式返回，本轮改用宽松输出，内容要求不变。\n");
        }
        else
        {
            if (instructions.Count == 0)
                return "";
            builder.Append("【本轮修改要求】你上一版台词没有通过审查，必须重做。\n必须遵守：\n");
        }

        foreach (var instruction in (relaxed ? RelaxedConstraints(instructions) : instructions).Distinct(StringComparer.Ordinal))
            builder.Append($"- {instruction}\n");

        builder.Append(isProactive || string.IsNullOrWhiteSpace(userInput)
            ? "本轮是你主动搭话，没有用户提问；保持主动关心或闲聊的意图，但要与上文连贯、不要复读。\n"
            : $"必须真正回应用户刚说的这句话：「{userInput}」。\n");
        builder.Append("只依据上面给出的资料作答，资料里没有的不要补。\n");
        builder.Append("不要解释、不要道歉、不要提到修改、审查、规则或模型这些字眼。");
        return builder.ToString();
    }

    /// <summary>
    /// 把 Composer 的校验问题翻译成下一版必须遵守的要求——**每个问题一条，不做拼接**。
    ///
    /// 为什么必须一条一条：松协议重做时要按条剔除格式类要求，
    /// 而拼接成一整块会让"只返回 JSON"这一条把整块带走，连"必须真正回应用户"也一起丢掉。
    /// </summary>
    public static IReadOnlyList<string> DegradedInstructions(StoryAnswerResult? answer)
    {
        if (answer is null || answer.Issues.Count == 0)
            return [];

        return answer.Issues.Select(issue => issue switch
        {
            "invalid_json_contract" => "只返回规定的 JSON（answerability + segments），不要输出任何多余文字",
            "missing_uncertainty" => "证据不足以完全确认时，必须明确说出哪一部分不确定",
            "unknown_citation" or "missing_citation" => "每条 fact/quote 必须绑定资料里给出的 evidence_ids，不得引用未给出的 id",
            "quote_not_verbatim" => "引用原话必须逐字照抄资料中的原句，不得改写",
            "unsupported_number" => "不要写出资料原文里没有的数字",
            "false_personal_experience" => "没亲历过的事不要用第一人称回忆，改用听闻或档案口吻",
            "citation_leak" => "不要把证据 id 写进台词正文",
            "text_length" => "每段不超过 100 字（原话引用不超过 240 字）",
            "segment_count" => "只输出 1~3 段",
            "action_format" => "action只能包含一段完整全角括号内的纯动作，不在括号外接台词。台词另放thought或有引用的fact段；总计仍不超过3段，不能把台词包进动作括号",
            "fact_in_unattributed_segment" => "thought/action 段里不要夹带经历、数量或人物关系",
            var semantic when semantic.StartsWith("semantic-support-missing:",StringComparison.Ordinal) => "逐段删去资料未支持的事实；只回答用户问的已知事实，不添加功效、原因或经历，不给与用户约束矛盾的建议。偏好只支持选择，不支持健康功效。缩短回答，不通过换个说法保留同一个无据断言",
            "semantic-review-invalid" or "semantic-review-unavailable" => "保留最必要且有明确出处的事实，其余不确定内容省略",
            "answerability" or "unknown_kind" or "emotion" => "严格按回答协议里的取值作答",
            "missing_segments" or "segment_not_object" => "按协议用 segments 数组返回，每段是一个对象",
            _ => "严格按上面的回答协议作答",
        }).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 松协议重做时剔掉**格式类**要求（JSON / evidence_ids），保留内容类要求。
    /// 混在一起会让 prompt 自相矛盾，模型只能回一句最保险的短话。
    /// </summary>
    public static List<string> RelaxedConstraints(IReadOnlyList<string> constraints)
        => constraints
            .Where(c => !c.Contains("JSON", StringComparison.Ordinal)
                     && !c.Contains("evidence_ids", StringComparison.Ordinal))
            .ToList();

}
