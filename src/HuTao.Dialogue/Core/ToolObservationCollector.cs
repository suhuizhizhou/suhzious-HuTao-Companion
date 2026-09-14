using System.Text;
using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Core;

public interface IToolObservationCollector
{
    Task<string> CollectAsync(
        IReadOnlyDictionary<string, IAgentTool> tools,
        string? input,
        CancellationToken ct = default);
}

/// <summary>统一执行并格式化工具观察结果；以后可在此增加并行、超时和工具级审计。</summary>
public sealed class ToolObservationCollector : IToolObservationCollector
{
    public async Task<string> CollectAsync(
        IReadOnlyDictionary<string, IAgentTool> tools,
        string? input,
        CancellationToken ct = default)
    {
        var observation = new StringBuilder();
        foreach (var (name, tool) in tools.OrderBy(tool => tool.Key))
        {
            ct.ThrowIfCancellationRequested();
            var result = await tool.ExecuteAsync(input, ct).ConfigureAwait(false);
            observation.AppendLine($"- {name}: {result}");
        }
        return observation.ToString().TrimEnd();
    }
}
