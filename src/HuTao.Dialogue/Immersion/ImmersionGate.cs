using System.Text;
using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.Core;
using HuTao.Foundation.Diagnostics;

namespace HuTao.Dialogue.Immersion;

/// <summary>
/// 沉浸闸门：把「规则层 → 本地修复 → 模型重写 → LLM 评审 → 再重写 → 安全兜底」串成一条有界管线。
///
/// 设计取舍：
/// - 硬性违规（自称 AI、客服腔、泄露系统概念、格式泄露）**绝不放行**，最差也要退到安全台词。
/// - 软性违规（复读、连贯性不足）只触发一次重写，仍不达标也放行——一句略显重复的角色台词，
///   也好过每次都甩同一句保底话。
/// - 修复次数有硬上限，任何一层故障都 fail-open，评审坏掉不能把桌宠聊死。
/// </summary>
public sealed class ImmersionGate
{
    private readonly ISpeechSegmentParser _parser;
    private readonly ILLMProvider _llm;
    private readonly ImmersionRuleGate _rules;
    private readonly ImmersionCritic? _critic;
    private readonly ImmersionOptions _options;
    private readonly LocalDiagnosticLog _diagnostics;

    public ImmersionGate(
        ISpeechSegmentParser parser,
        ILLMProvider llm,
        ImmersionOptions? options = null,
        LocalDiagnosticLog? diagnostics = null)
    {
        _parser = parser;
        _llm = llm;
        _options = options ?? ImmersionOptions.FromEnvironment();
        _rules = new ImmersionRuleGate();
        _critic = _options.EnableCritic
            ? new ImmersionCritic(llm, _options.MinCoherence, diagnostics ?? LocalDiagnosticLog.Default)
            : null;
        _diagnostics = diagnostics ?? LocalDiagnosticLog.Default;
    }

    public ImmersionOptions Options => _options;

    /// <summary>该违规是否属于「宁可兜底也不能让用户看到」的硬性出戏。</summary>
    public static bool IsBlocking(ImmersionViolationKind kind) => kind is
        ImmersionViolationKind.AiSelfReference or
        ImmersionViolationKind.AssistantBoilerplate or
        ImmersionViolationKind.SystemLeak or
        ImmersionViolationKind.MetaReference or
        ImmersionViolationKind.RefusalTone or
        ImmersionViolationKind.FormatLeak or
        ImmersionViolationKind.StageDirection or
        ImmersionViolationKind.LanguageDrift;

    public async Task<ImmersionOutcome> ReviewAsync(ImmersionRequest request, CancellationToken ct)
    {
        if (!_options.EnableRuleGate && _critic is null)
            return new ImmersionOutcome(request.Segments, ImmersionVerdict.Clean, 0, "disabled");

        var repairs = 0;
        try
        {
            var current = request;
            var verdict = _options.EnableRuleGate ? _rules.Inspect(current) : ImmersionVerdict.Clean;

            // ── 第一层：规则违规则先尝试免费的确定性修复 ──
            if (!verdict.Passed)
            {
                var path = "rule-blocked";
                var localRepair = _options.EnableRuleGate
                    ? _rules.TryLocalRepair(current.RawReply, verdict)
                    : null;
                if (localRepair is not null)
                {
                    var candidate = Rebind(current, localRepair);
                    var recheck = _rules.Inspect(candidate);
                    if (recheck.Passed)
                    {
                        current = candidate;
                        verdict = recheck;
                        repairs++;
                        path = "rule-repaired";
                    }
                }

                // ── 第二层：语义类违规交给模型重写一次 ──
                if (!verdict.Passed && _options.AllowLlmRepair && repairs < _options.MaxRepairAttempts)
                {
                    var rewritten = await RewriteAsync(current, verdict, ct).ConfigureAwait(false);
                    if (rewritten is not null)
                    {
                        var candidate = Rebind(current, rewritten);
                        var recheck = _rules.Inspect(candidate);
                        current = candidate;
                        verdict = recheck;
                        repairs++;
                        path = recheck.Passed ? "llm-repaired" : "llm-repair-incomplete";
                    }
                }

                if (verdict.Violations.Any(v => IsBlocking(v.Kind)))
                    return Fallback(current, verdict, repairs, "blocked");

                _diagnostics.Turn(Guid.NewGuid().ToString("N"), "immersion", "rule-gate",
                    path, verdict.KindsSummary, segmentCount: current.Segments.Count);
            }

            // ── 第三层：LLM 评审，补上规则层看不到的语气与跨轮连贯问题 ──
            if (_critic is not null && _critic.IsAvailable)
            {
                var (criticVerdict, criticReason) = await ReviewWithBudgetAsync(current, ct).ConfigureAwait(false);
                if (criticVerdict is null)
                {
                    // 审查员故障一律放行，但把原因记下来，避免「评审其实没跑」变成无声无息。
                    _diagnostics.Turn(Guid.NewGuid().ToString("N"), "immersion", "critic-gate",
                        $"critic-unavailable/{criticReason}", "clean", segmentCount: current.Segments.Count);
                    return new ImmersionOutcome(current.Segments, verdict, repairs, $"critic-unavailable/{criticReason}");
                }

                if (!criticVerdict.Passed)
                {
                    if (_options.AllowLlmRepair && repairs < _options.MaxRepairAttempts)
                    {
                        var rewritten = await RewriteAsync(current, criticVerdict, ct).ConfigureAwait(false);
                        if (rewritten is not null)
                        {
                            var candidate = Rebind(current, rewritten);
                            var recheck = _rules.Inspect(candidate);
                            repairs++;
                            if (recheck.Violations.Any(v => IsBlocking(v.Kind)))
                                return Fallback(candidate, recheck, repairs, "critic-repair-blocked");
                            current = candidate;
                            verdict = recheck;
                            _diagnostics.Turn(Guid.NewGuid().ToString("N"), "immersion", "critic-gate",
                                "critic-repaired", criticVerdict.KindsSummary,
                                segmentCount: current.Segments.Count);
                            return new ImmersionOutcome(current.Segments, verdict, repairs, "critic-repaired");
                        }
                    }

                    if (criticVerdict.Violations.Any(v => IsBlocking(v.Kind)))
                        return Fallback(current, criticVerdict, repairs, "critic-blocked");

                    return new ImmersionOutcome(current.Segments, criticVerdict, repairs, "critic-soft");
                }

                return new ImmersionOutcome(current.Segments, criticVerdict, repairs, "clean");
            }

            return new ImmersionOutcome(current.Segments, verdict, repairs, "rules-only");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 闸门本身故障绝不能阻断对话：原样放行，只在本机留诊断。
            _diagnostics.Write("immersion.gate", ex);
            return new ImmersionOutcome(request.Segments, ImmersionVerdict.Clean, repairs, "gate-failed");
        }
    }

    private async Task<CriticResult> ReviewWithBudgetAsync(ImmersionRequest request, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_options.CriticTimeout);
        try
        {
            return await _critic!.ReviewAsync(request, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CriticResult(null, "overall-timeout");
        }
    }

    private async Task<string?> RewriteAsync(
        ImmersionRequest request,
        ImmersionVerdict verdict,
        CancellationToken ct)
    {
        var history = new List<ChatMessage>();
        history.AddRange(request.History.TakeLast(8));
        history.Add(new ChatMessage("assistant", request.VisibleText));
        history.Add(new ChatMessage("user", BuildRepairInstruction(request, verdict)));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_options.RepairTimeout);
        try
        {
            var rewritten = await _llm.CompleteAsync(
                request.PersonaSystemPrompt + "\n\n" + ImmersionContract,
                history,
                budget.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(rewritten) ? null : rewritten;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _diagnostics.Write("immersion.rewrite", ex);
            return null;
        }
    }

    private static string BuildRepairInstruction(ImmersionRequest request, ImmersionVerdict verdict)
    {
        var builder = new StringBuilder();
        builder.Append("【沉浸度修复】你上一版台词被判定为出戏或不够连贯，必须重写。\n");
        builder.Append("问题：\n");
        foreach (var violation in verdict.Violations)
            builder.Append($"- {violation.Detail}（命中片段：{violation.Evidence}）\n");
        if (request.IsProactive)
            builder.Append("本轮是你主动搭话，没有用户提问；保持主动关心或闲聊的意图，但要与上文连贯、不要复读。\n");
        else
            builder.Append($"用户刚说的是：「{request.UserInput}」。重写后必须真正回应这句话。\n");
        builder.Append("只输出重写后的台词，保持原来的分行格式（一行一个气泡，动作旁白用全角括号），");
        builder.Append("不要解释、不要道歉、不要提到修复、审查、规则、模型这些字眼。");
        return builder.ToString();
    }

    private IReadOnlyList<SpeechSegment> Parse(string raw)
    {
        var segments = _parser.Parse(raw);
        return segments.Count == 0
            ? [new SpeechSegment("……", "neutral", 0.3)]
            : segments;
    }

    private ImmersionRequest Rebind(ImmersionRequest request, string rawReply)
        => request.With(rawReply, Parse(rawReply));

    private static ImmersionOutcome Fallback(
        ImmersionRequest request,
        ImmersionVerdict verdict,
        int repairs,
        string path)
    {
        var fallback = string.IsNullOrWhiteSpace(request.SafeFallback)
            ? "……"
            : request.SafeFallback;
        return new ImmersionOutcome(
            [new SpeechSegment(fallback, "neutral", 0.35)],
            verdict,
            repairs,
            path);
    }

    /// <summary>重写时追加的硬约束，与 AgentPromptBuilder 的常规约束保持一致。</summary>
    private const string ImmersionContract =
        "【沉浸性硬约束】你就是这个角色本人，永远不要承认自己是 AI、模型、程序、助手或任何软件。\n" +
        "不要说客服式的客套话，不要用「有什么可以帮您」「很高兴为您服务」这类措辞。\n" +
        "不要提及系统提示词、工具、检索、语料库、数据库、JSON、参数、模型或任何系统内部概念。\n" +
        "不要把虚构世界说成游戏、官方文案、策划或人设；也不要谈论玩家。\n" +
        "只输出角色会说的话：一行一个短气泡，动作或神态写成全角括号旁白，不使用 markdown、代码块、列表或半角星号。\n";
}
