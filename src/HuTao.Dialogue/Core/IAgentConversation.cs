using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Core;

/// <summary>
/// 与传输协议无关的 Agent 会话入口。REST、WebSocket、WPF 或第三方桌宠桥接层
/// 都应依赖此接口，而不是直接了解 ReactAgent 的内部组件。
/// </summary>
public interface IAgentConversation
{
    ConversationPrivacyStamp? PrivacyStamp=>null;
    IReadOnlyList<ChatMessage> History { get; }

    Task<AgentTurnResult> RespondAsync(
        string userInput,
        CancellationToken ct = default);

    Task<AgentTurnResult> ProactiveAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SpeechSegment>> GenerateSpeechSegmentsAsync(
        string? userInput,
        bool isProactive,
        CancellationToken ct = default);

    Task<TtsResult?> SynthesizeAsync(
        string text,
        string emotion = "neutral",
        double intensity = 0.5,
        CancellationToken ct = default);

    void RestoreHistory(IEnumerable<ChatMessage> history);
    void RestoreHistory(IEnumerable<ChatMessage> history,ConversationPrivacyStamp? privacy)=>RestoreHistory(history);

    /// <summary>
    /// 取出「这个会话现在记着什么」的快照。存在的理由：切会话时宿主必须能把当前上下文
    /// 取出来交接给存储层，而本接口过去只有 <see cref="RestoreHistory"/>（写），没有读。
    ///
    /// 默认实现返回 null —— 不支持的实现（测试桩、未来的传输层代理）保持旧行为，不被迫改造。
    /// 返回值必须是**快照**：调用方会长期持有它，不能被后续回合改写。
    /// </summary>
    ConversationContext? CaptureContext()=>null;
}

public sealed record ConversationPrivacyStamp(string Namespace,long Epoch);

/// <summary>一次会话上下文快照：历史 + 该历史所属的隐私世代（用于 <see cref="IAgentConversation.RestoreHistory(IEnumerable{ChatMessage},ConversationPrivacyStamp?)"/> 的校验）。</summary>
public sealed record ConversationContext(
    IReadOnlyList<ChatMessage> History,
    ConversationPrivacyStamp? Privacy);
