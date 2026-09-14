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
}

/// <summary>沉浸闸门配置。默认全开：规则层免费，评审层多一次 LLM 调用换最严把关。</summary>
public sealed record ImmersionOptions
{
    public bool EnableRuleGate { get; init; } = true;
    public bool EnableCritic { get; init; } = true;
    public bool AllowLlmRepair { get; init; } = true;
    public double MinCoherence { get; init; } = 0.6;
    public int MaxRepairAttempts { get; init; } = 1;
    /// <summary>
    /// observe 阶段「判定不过 → 换新检索提示词重走 RAG → 重新起草」的额外轮次上限。
    /// 用尽后退回闸门给的兜底台词，绝不放行出戏内容。0 表示只起草一次（旧行为）。
    /// </summary>
    public int MaxObserveRetries { get; init; } = 2;
    public TimeSpan CriticTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RepairTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>HU_TAO_IMMERSION=off 可整体关闭；HU_TAO_IMMERSION_CRITIC=false 只留规则层。</summary>
    public static ImmersionOptions FromEnvironment()
    {
        var master = Environment.GetEnvironmentVariable("HU_TAO_IMMERSION")?.Trim();
        if (string.Equals(master, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(master, "false", StringComparison.OrdinalIgnoreCase) ||
            master == "0")
            return new ImmersionOptions { EnableRuleGate = false, EnableCritic = false, AllowLlmRepair = false };

        return new ImmersionOptions
        {
            EnableRuleGate = true,
            EnableCritic = ReadFlag("HU_TAO_IMMERSION_CRITIC", defaultValue: true),
            AllowLlmRepair = ReadFlag("HU_TAO_IMMERSION_REPAIR", defaultValue: true),
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

    /// <summary>本地修复或模型重写后，用新的原始文本替换待审内容。</summary>
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
