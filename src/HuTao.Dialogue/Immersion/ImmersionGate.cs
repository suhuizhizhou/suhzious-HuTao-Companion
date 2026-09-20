using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.Core;
using HuTao.Foundation.Diagnostics;

namespace HuTao.Dialogue.Immersion;

/// <summary>
/// 沉浸闸门：**只审查、不创作**。
///
/// 职责边界（这条边界是刻意的）：
/// - 闸门产出「这版行不行 + 不行在哪 + 下一版要怎么做」（<see cref="ImmersionVerdict.Instructions"/>）；
/// - **重新生产台词是 agent 的事**——因为只有 agent 手里有本轮的剧情证据。
///   闸门曾经自己调 LLM 重写，而那次重写调用里没有证据，等于绕开了整套事实校验。
///
/// 内部管线：规则层 → **本地确定性清理**（格式类，零成本）→ LLM 评审 → 如实返回 verdict。
/// 刻意不做的事：调 LLM 写台词、替调用方决定兜底。这两件事由调用方（ReactAgent）按预算决定。
///
/// 设计取舍：
/// - 硬性违规（自称 AI、客服腔、泄露系统概念、格式泄露）**绝不放行**，由调用方兜底。
/// - 软性违规（复读、连贯性不足）调用方重试一次后放行——一句略显重复的角色台词，
///   也好过每次都甩同一句保底话。
/// - 任何一层故障都 fail-open，评审坏掉不能把桌宠聊死。
/// </summary>
public sealed class ImmersionGate
{
    private readonly ISpeechSegmentParser _parser;
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
        _options = options ?? ImmersionOptions.FromEnvironment();
        _rules = new ImmersionRuleGate();
        _critic = _options.EnableCritic
            ? new ImmersionCritic(llm, _options.MinCoherence, diagnostics ?? LocalDiagnosticLog.Default)
            : null;
        _diagnostics = diagnostics ?? LocalDiagnosticLog.Default;
    }

    public ImmersionOptions Options => _options;

    /// <summary>该违规是否属于「宁可兜底也不能让用户看到」的硬性出戏。</summary>
    public static bool IsBlocking(ImmersionViolationKind kind) => ImmersionDirectives.IsBlocking(kind);

    /// <summary>
    /// 审查入口。规则层关闭时给路径加 <c>rules-off/</c> 前缀——
    /// 否则「关着规则层」与「开着但一次没命中」在 trace 和诊断里长得一模一样。
    /// </summary>
    public async Task<ImmersionOutcome> ReviewAsync(ImmersionRequest request, CancellationToken ct)
    {
        var outcome = await ReviewCoreAsync(request, ct).ConfigureAwait(false);
        return _options.EnableRuleGate ? outcome : outcome with { Path = "rules-off/" + outcome.Path };
    }

    private async Task<ImmersionOutcome> ReviewCoreAsync(ImmersionRequest request, CancellationToken ct)
    {
        if (!_options.EnableRuleGate && _critic is null)
            return new ImmersionOutcome(request.Segments, ImmersionVerdict.Clean, 0, "disabled");

        var repairs = 0;
        try
        {
            var current = request;
            var verdict = _options.EnableRuleGate ? _rules.Inspect(current) : ImmersionVerdict.Clean;

            // ── 第一层：规则违规先尝试**免费的确定性清理** ──
            // 这是「清理」不是「创作」：只把 markdown 星号换成全角括号这类格式问题就地修好，
            // 省掉一次往返。**生成新台词不在闸门职责内**——它没有证据，见类注释。
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

                _diagnostics.Turn(Guid.NewGuid().ToString("N"), "immersion", "rule-gate",
                    path, verdict.KindsSummary, segmentCount: current.Segments.Count);

                // 硬违规本地修不好就**如实上报**，由调用方带证据重新生成或兜底；
                // 软违规继续走评审层——连贯性是规则层看不到的，得让评审员一起判断。
                if (!verdict.Passed && verdict.Violations.Any(v => IsBlocking(v.Kind)))
                    return new ImmersionOutcome(current.Segments, verdict, repairs, path);
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
                    _diagnostics.Turn(Guid.NewGuid().ToString("N"), "immersion", "critic-gate",
                        "critic-blocked", criticVerdict.KindsSummary,
                        segmentCount: current.Segments.Count);
                    // 只上报，不重写：评审员的意见经 Instructions 交给调用方，
                    // 由持有证据的 agent 决定重生成还是兜底。软/硬的区分也留给调用方按 IsBlocking 判。
                    return new ImmersionOutcome(current.Segments, criticVerdict, repairs, "critic-blocked");
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

    private IReadOnlyList<SpeechSegment> Parse(string raw)
    {
        var segments = _parser.Parse(raw);
        return segments.Count == 0
            ? [new SpeechSegment("……", "neutral", 0.3)]
            : segments;
    }

    private ImmersionRequest Rebind(ImmersionRequest request, string rawReply)
        => request.With(rawReply, Parse(rawReply));
}
