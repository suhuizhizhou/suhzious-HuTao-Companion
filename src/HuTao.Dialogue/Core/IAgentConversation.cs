using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Core;

/// <summary>
/// 与传输协议无关的 Agent 会话入口。REST、WebSocket、WPF 或第三方桌宠桥接层
/// 都应依赖此接口，而不是直接了解 ReactAgent 的内部组件。
/// </summary>
public interface IAgentConversation
{
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
}
