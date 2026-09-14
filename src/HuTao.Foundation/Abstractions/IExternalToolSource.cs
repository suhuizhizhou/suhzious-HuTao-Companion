namespace HuTao.Foundation.Abstractions;

/// <summary>
/// 外部工具的**描述**，与来源无关。
///
/// 存在的理由：工具现在有两个来源——进程内直接实现的 <see cref="IAgentTool"/>，
/// 和外部协议（MCP / CLI）暴露出来的工具。上层（提示词拼装、审批、并行调度）
/// 只应该看到这一层描述，不该知道它背后是 C# 类还是一个 stdio JSON-RPC 服务。
/// </summary>
/// <param name="Name">工具唯一名。</param>
/// <param name="Description">给模型看的一句话说明，决定它会不会调用这个工具。</param>
/// <param name="InputSchema">
/// 入参的 JSON Schema（可空）。MCP 服务会带；进程内工具通常为 null，
/// 表示「只接受一段自由文本入参」。
/// </param>
/// <param name="Source">来源标识，仅用于诊断（如 "builtin" / "cli" / "mcp:playwright"）。</param>
public sealed record AgentToolDescriptor(
    string Name,
    string Description,
    string? InputSchema = null,
    string Source = "builtin");

/// <summary>
/// 外部工具来源：能把远端工具**列出来**并**调用**。
///
/// MCP 客户端、CLI 包装器都实现它。这样接一个新工具来源只需要加一个实现，
/// 不用改 Agent、提示词或宿主。
/// </summary>
public interface IExternalToolSource : IAsyncDisposable
{
    /// <summary>来源标识，用于诊断与日志（如 "mcp:playwright"）。</summary>
    string SourceName { get; }

    /// <summary>列出这个来源能提供的工具。</summary>
    Task<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(CancellationToken ct = default);

    /// <summary>调用一个工具，返回一段可读结果文本。</summary>
    Task<string> CallToolAsync(string toolName, string? input, CancellationToken ct = default);
}
