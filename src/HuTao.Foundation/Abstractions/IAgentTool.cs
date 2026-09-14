namespace HuTao.Foundation.Abstractions;

public enum AgentToolEffect
{
    ReadOnly,
    LocalOutput,
    ExternalMutation,
}

public enum AgentToolSensitivity
{
    None,
    ActivityMetadata,
    ScreenContent,
}

/// <summary>工具的执行边界，供未来审批、并行调度和 MCP/Function schema 使用。</summary>
public sealed record AgentToolPolicy(
    AgentToolEffect Effect = AgentToolEffect.ReadOnly,
    AgentToolSensitivity Sensitivity = AgentToolSensitivity.None,
    bool RequiresExplicitIntent = false,
    bool CanRunInParallel = true);

/// <summary>
/// Agent 可调用的工具。对应 ReAct 里的 Observe 环节：
/// 感知电脑前的人在做什么、当前时间、系统状态等。
/// 工具以「名称 + 描述」注册，LLM 或规则据此决定何时调用。
/// </summary>
public interface IAgentTool
{
    /// <summary>工具唯一名，如 "time"、"active_window"。</summary>
    string Name { get; }

    /// <summary>一句话描述工具的用途（供 LLM 决策时参考）。</summary>
    string Description { get; }

    /// <summary>默认是无敏感数据的只读工具；高风险实现必须显式覆盖。</summary>
    AgentToolPolicy Policy => new();

    /// <summary>执行工具，返回一段可读的结果文本。</summary>
    Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default);
}
