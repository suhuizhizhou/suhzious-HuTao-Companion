using HuTao.Agent.Core.Abstractions;

namespace HuTao.Agent.Core.Tools;

/// <summary>获取当前时间。最基础、最常用的感知工具。</summary>
public sealed class TimeTool : IAgentTool
{
    public string Name => "time";
    public string Description => "获取当前日期和时间";

    public Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default)
        => Task.FromResult(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss dddd"));
}
