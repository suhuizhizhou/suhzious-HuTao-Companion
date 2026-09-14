namespace HuTao.Foundation.Abstractions;

public enum AgentStage
{
    Reason,
    Observe,
    Think,
    Act,
}

/// <summary>可通过 UI、日志、REST 或 WebSocket 发布的稳定 Agent 事件。</summary>
public abstract record AgentEvent(DateTimeOffset Timestamp);

public sealed record AgentStageEvent(
    AgentStage Stage,
    string Summary,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record AgentAudioEvent(
    string AudioPath,
    string? VoiceId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

/// <summary>外部可观测通道；实现方应自行保证发布失败不阻断 Agent 主流程。</summary>
public interface IAgentEventSink
{
    ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken ct = default);
}

public sealed class NullAgentEventSink : IAgentEventSink
{
    public static NullAgentEventSink Instance { get; } = new();
    private NullAgentEventSink() { }

    public ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
