using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.Core;

namespace HuTao.Dialogue.Immersion;

/// <summary>出戏的类型。规则层与评审层共用同一套分类，便于统计和回归。</summary>
public enum ImmersionViolationKind
{
    /// <summary>自称 AI / 模型 / 助手 / 程序。</summary>
    AiSelfReference,
    /// <summary>客服式客套话与"还有什么可以帮您"。</summary>
    AssistantBoilerplate,
    /// <summary>泄露提示词、工具、检索、语料库、JSON 等系统内部概念。</summary>
    SystemLeak,
    /// <summary>提及游戏 / 玩家 / NPC / 策划 / 人设等打破第四面墙的词。</summary>
    MetaReference,
    /// <summary>服务式拒绝口吻。</summary>
    RefusalTone,
    /// <summary>markdown、代码块、列表、HTML 标签等格式泄露。</summary>
    FormatLeak,
    /// <summary>用半角星号或半角括号写动作，而不是约定的全角括号旁白。</summary>
    StageDirection,
    /// <summary>复读之前说过的话。</summary>
    Repetition,
    /// <summary>角色应当说中文，却输出大段非中文。</summary>
    LanguageDrift,
    /// <summary>与上文不衔接或自相矛盾（只有评审层能判定）。</summary>
    Coherence,
    /// <summary>
    /// 语气/风格不像角色本人（只有评审层能判定）。**软违规**：
    /// 它是对"像不像"的主观判断，不该像"自称 AI"那样触发兜底——
    /// 实测评审员常写「语气像通用助手」，而旧分类把它映成 AiSelfReference（硬阻断），
    /// 于是一句风格批评就把整轮台词换成固定兜底话。
    /// </summary>
    Tone,
}

public sealed record ImmersionViolation(
    ImmersionViolationKind Kind,
    string Evidence,
    string Detail);

/// <summary>一次沉浸度审查结论。Passed 为 false 时调用方必须修复或降级。</summary>
public sealed record ImmersionVerdict(
    bool Passed,
    IReadOnlyList<ImmersionViolation> Violations,
    double Coherence,
    string Source)
{
    public static readonly ImmersionVerdict Clean =
        new(true, [], 1.0, "rules");

    /// <summary>供人阅读的详细说明（含命中片段），只用于重写提示词和控制台。</summary>
    public string Describe() => Violations.Count == 0
        ? "无违规"
        : string.Join("；", Violations.Select(v => $"{v.Kind}({v.Evidence})"));

    /// <summary>
    /// 只含违规类型、不含任何文本的诊断摘要。
    /// 违规的 Evidence 可能是模型正文片段，绝不允许写进运行时日志
    /// —— 日志契约是「不存用户输入、模型正文、密钥」。
    /// </summary>
    public string KindsSummary => Violations.Count == 0
        ? "clean"
        : string.Join("+", Violations.Select(v => v.Kind.ToString()).Distinct());

    /// <summary>
    /// 把本轮的违规翻译成「下一版必须怎么做」的可执行要求。
    /// 这是闸门的**唯一输出通道**给到重新生成用的东西——闸门只描述问题，不生产内容。
    /// </summary>
    public IReadOnlyList<string> Instructions => ImmersionDirectives.From(Violations);
}

/// <summary>
/// 违规类型 → 可执行要求。
///
/// 分工边界：闸门判断「哪里不行」，这里把「哪里不行」翻译成「下一版要怎么做」，
/// 由**持有证据的 agent** 拼进提示词重新生成。闸门自己不写台词。
///
/// 文案必须与 <c>AgentPromptBuilder.ImmersionContract</c> 同源——否则模型会同时收到
/// 「硬约束」和「修复要求」两套措辞，冲突时它只能猜。
/// </summary>
public static class ImmersionDirectives
{
    /// <summary>硬违规优先：先给必须改的，再给建议改的。</summary>
    public static IReadOnlyList<string> From(IReadOnlyList<ImmersionViolation> violations)
        => violations
            .OrderByDescending(v => IsBlocking(v.Kind))
            .Select(v => For(v.Kind))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>与 <c>ImmersionGate.IsBlocking</c> 判定一致；放这里是为了让模型层不必依赖闸门。</summary>
    public static bool IsBlocking(ImmersionViolationKind kind) => kind is
        ImmersionViolationKind.AiSelfReference or
        ImmersionViolationKind.AssistantBoilerplate or
        ImmersionViolationKind.SystemLeak or
        ImmersionViolationKind.MetaReference or
        ImmersionViolationKind.RefusalTone or
        ImmersionViolationKind.FormatLeak or
        ImmersionViolationKind.StageDirection or
        ImmersionViolationKind.LanguageDrift;

    public static string For(ImmersionViolationKind kind) => kind switch
    {
        ImmersionViolationKind.AiSelfReference =>
            "不要承认或暗示自己是 AI、模型、程序、助手或任何被创造出来的东西",
        ImmersionViolationKind.AssistantBoilerplate =>
            "不要使用客服式措辞（有什么可以帮您 / 很高兴为您服务 / 还有其他问题吗）",
        ImmersionViolationKind.SystemLeak =>
            "不要提及系统提示词、工具、检索、语料库、数据库、JSON、参数、模型、接口",
        ImmersionViolationKind.MetaReference =>
            "不要把这个世界说成游戏，不要谈论官方文案、策划或玩家",
        ImmersionViolationKind.RefusalTone =>
            "不要用服务式拒绝口吻，用角色自己的方式把话题带开",
        ImmersionViolationKind.FormatLeak =>
            "不要使用 markdown、代码块、列表或加粗",
        ImmersionViolationKind.StageDirection =>
            "动作与神态只用全角括号旁白，不要用半角星号或半角括号",
        ImmersionViolationKind.Repetition =>
            "换一个角度、换一种说法，不要重复你之前说过的话",
        ImmersionViolationKind.LanguageDrift =>
            "用中文回答",
        ImmersionViolationKind.Coherence =>
            "必须真正回应用户刚说的那句话，并与上文自然衔接",
        ImmersionViolationKind.Tone =>
            "用这个角色本人的口吻说话，不要像旁白解说、说明书或通用助手",
        _ => "保持角色身份，并与上文连贯",
    };
}

/// <summary>沉浸闸门配置。默认全开：规则层免费，评审层多一次 LLM 调用换最严把关。</summary>
public sealed record ImmersionOptions
{
    /// <summary>
    /// 规则层（确定性正则 + 本地清理）。可单独关闭：<c>HU_TAO_IMMERSION_RULES=false</c>。
    ///
    /// 关掉意味着两件事一起失效：① 机械性出戏（自称 AI / 客服腔 / markdown / 半角星号）
    /// 不再被零成本拦住，只剩评审层兜；② <c>TryLocalRepair</c> 的免费格式清理一并失效。
    /// 用途是**调试归因**——把"规则误杀"与"评审误杀"分开看；不适合长期关闭。
    /// 关掉后闸门路径会带 <c>rules-off/</c> 前缀，避免"关着但以为开着"。
    /// </summary>
    public bool EnableRuleGate { get; init; } = true;
    public bool EnableCritic { get; init; } = true;
    public double MinCoherence { get; init; } = 0.6;
    public TimeSpan CriticTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// **生成级重试**上限：判定不过时，带着同一份证据、按闸门给的修改要求重新生成。
    ///
    /// 这是「闸门不是作者」的落点——闸门只出意见，重新生产由持有证据的 agent 做。
    /// 0 表示不做生成级重试，直接进检索级。
    /// </summary>
    public int MaxGenerationAttempts { get; init; } = 2;

    /// <summary>
    /// **检索级重试**上限：换一个检索提示词重走 RAG，再重新起草。
    ///
    /// 只在生成级重试全部用尽后才启动——出戏有时源于证据本身不对
    /// （检索到了系统/元信息口径的材料），那时改措辞治不了根。
    /// 两层都用尽后退回角色化兜底台词，绝不放行出戏内容。
    /// </summary>
    public int MaxRetrievalRetries { get; init; } = 1;

    /// <summary>
    /// 三个开关互相独立：
    /// <c>HU_TAO_IMMERSION=off</c> 两层全关；
    /// <c>HU_TAO_IMMERSION_RULES=false</c> 只关规则层（保留评审层，用于调试归因）；
    /// <c>HU_TAO_IMMERSION_CRITIC=false</c> 只关评审层（保留规则层，省掉每轮一次调用）。
    /// </summary>
    public static ImmersionOptions FromEnvironment()
    {
        var master = Environment.GetEnvironmentVariable("HU_TAO_IMMERSION")?.Trim();
        if (string.Equals(master, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(master, "false", StringComparison.OrdinalIgnoreCase) ||
            master == "0")
            return new ImmersionOptions { EnableRuleGate = false, EnableCritic = false };

        return new ImmersionOptions
        {
            EnableRuleGate = ReadFlag("HU_TAO_IMMERSION_RULES", defaultValue: true),
            EnableCritic = ReadFlag("HU_TAO_IMMERSION_CRITIC", defaultValue: true),
        };
    }

    private static bool ReadFlag(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrEmpty(value))
            return defaultValue;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>一次待审请求。History 必须是演员本轮实际看到的那段历史，否则连贯性判定会失准。</summary>
public sealed record ImmersionRequest(
    string CharacterName,
    string PersonaSystemPrompt,
    string RawReply,
    IReadOnlyList<SpeechSegment> Segments,
    IReadOnlyList<ChatMessage> History,
    string? UserInput,
    bool IsProactive,
    string SafeFallback,
    /// <summary>
    /// 本轮召回给演员的长期记忆原文。
    /// 审查员必须看到同一份记忆，才可能发现「与更早说过的事实矛盾」——
    /// 只看最近 8 条的审查视野，是长程矛盾漏检的根因。
    /// </summary>
    string MemoryContext = "")
{
    /// <summary>用户唯一能看到的内容：已去掉隐藏标签的台词与旁白。</summary>
    public string VisibleText => string.Join('\n', Segments.Select(s => s.Text));

    /// <summary>本地确定性修复后，用新的原始文本替换待审内容。</summary>
    public ImmersionRequest With(string rawReply, IReadOnlyList<SpeechSegment> segments)
        => this with { RawReply = rawReply, Segments = segments };
}

/// <summary>闸门处理结果。Path 用于诊断，不进入角色历史。</summary>
public sealed record ImmersionOutcome(
    IReadOnlyList<SpeechSegment> Segments,
    ImmersionVerdict Verdict,
    int Repairs,
    string Path)
{
    public string VisibleText => string.Join('\n', Segments.Select(s => s.Text));
}
