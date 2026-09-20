namespace HuTao.Foundation.Abstractions;

public enum AgentToolCategory { Observation, ComputeOrRead, SideEffect }

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

/// <summary>Execution boundaries enforced by the shared tool gate.</summary>
public sealed record AgentToolPolicy(
    AgentToolEffect Effect = AgentToolEffect.ReadOnly,
    AgentToolSensitivity Sensitivity = AgentToolSensitivity.None,
    bool RequiresExplicitIntent = false,
    bool CanRunInParallel = true,
    bool RequiresConfirmation = false,
    int TimeoutSeconds = 15,
    int MaxOutputCharacters = 4000);

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

    AgentToolCategory Category => Policy.Effect == AgentToolEffect.ReadOnly
        ? AgentToolCategory.ComputeOrRead : AgentToolCategory.SideEffect;
    ToolInputSchema InputSchema => ToolInputSchema.Legacy;
    bool UsesJsonArguments => false;

    /// <summary>Pure preflight validation before approval or execution.</summary>
    string? ValidateArguments(System.Text.Json.JsonElement arguments) => null;
    string DescribeAction(System.Text.Json.JsonElement arguments) => Description;

    /// <summary>执行工具，返回一段可读的结果文本。</summary>
    Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default);
}
