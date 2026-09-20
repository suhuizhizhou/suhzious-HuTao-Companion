using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Tools;

/// <summary>
/// 把任何 <see cref="IExternalToolSource"/>（MCP 客户端、远程工具服务…）
/// 暴露的工具包装成进程内的 <see cref="IAgentTool"/>。
///
/// 这一层的意义：**Agent 侧零改动就能用上外部工具**。
/// 提示词拼装、沉浸闸门、可观测性、审批策略看到的都还是普通的 <c>IAgentTool</c>，
/// 不需要知道背后是一个 stdio JSON-RPC 服务。
///
/// 关于「要不要上 MCP」的判断（写在这里免得以后重复讨论）：
/// - **值得**：想复用别人写好的工具（浏览器、文件系统、GitHub…）、
///   想让同一批工具被别的宿主（Claude Desktop / Cursor）使用、想用 Python/JS 写工具。
/// - **不值得**：只是为了「把角色和引擎解耦」。那是角色包的事，MCP 管不着——
///   MCP 里没有 persona 这个概念，`prompts` 只是一个命名模板。
/// - 代价要认：跨进程调试、无进度回调、以及**读窗口标题这类能力的信任边界会变松**
///   （现在是进程内一个 <c>Func&lt;bool&gt;</c> 开关，关掉后一行都不读）。
/// </summary>
public sealed class McpToolAdapter : IAgentTool
{
    private readonly IExternalToolSource _source;
    private readonly AgentToolDescriptor _descriptor;

    private McpToolAdapter(IExternalToolSource source, AgentToolDescriptor descriptor,
        AgentToolPolicy policy)
    {
        _source = source;
        _descriptor = descriptor;
        Policy = policy;
        InputSchema = ToolInputSchema.FromJson(descriptor.InputSchema ??
            throw new ArgumentException("external_tool_schema_required"));
    }

    public string Name => _descriptor.Name;

    public string Description => string.IsNullOrWhiteSpace(_descriptor.Description)
        ? $"{Name}（来自 {_source.SourceName}）"
        : _descriptor.Description;

    public AgentToolPolicy Policy { get; }
    public bool UsesJsonArguments => true;
    public ToolInputSchema InputSchema { get; }

    public Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default)
        => _source.CallToolAsync(_descriptor.Name, input, ct);

    /// <summary>
    /// 列出外部来源的工具并逐个包装。
    ///
    /// <paramref name="effectivePolicy"/> 决定默认边界。外部工具**默认按最严处理**
    /// （会改外部状态、需要用户明确意图），因为我们对它的实现一无所知；
    /// 想放宽必须显式指定。
    /// </summary>
    public static async Task<IReadOnlyList<IAgentTool>> WrapAsync(
        IExternalToolSource source,
        AgentToolPolicy? effectivePolicy = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var policy = effectivePolicy ?? new AgentToolPolicy(
            AgentToolEffect.ExternalMutation,
            AgentToolSensitivity.None,
            RequiresExplicitIntent: true,
            CanRunInParallel: false);

        try
        {
            var descriptors = await source.ListToolsAsync(ct).ConfigureAwait(false);
            log?.Invoke($"[tools] {source.SourceName} 提供 {descriptors.Count} 个工具");
            var tools = new List<IAgentTool>();
            foreach (var descriptor in descriptors)
            {
                try { tools.Add(new McpToolAdapter(source, descriptor, policy)); }
                catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or
                    InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
                { log?.Invoke($"[tools] {descriptor.Name} schema unsupported: {ex.GetType().Name}"); }
            }
            return tools;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 外部工具来源不可用不该让角色起不来——它只是少了一批工具。
            log?.Invoke($"[tools] {source.SourceName} 工具列表获取失败，已跳过：{ex.GetType().Name}");
            return [];
        }
    }
}
