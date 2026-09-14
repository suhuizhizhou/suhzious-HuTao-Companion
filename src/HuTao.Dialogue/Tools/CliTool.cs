using System.Diagnostics;
using System.Text;
using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Tools;

/// <summary>
/// 把一个白名单里的可执行程序包装成 Agent 工具。
///
/// 为什么先做 CLI 而不是 MCP：**很多能力已经有现成的命令行程序**（转录、格式转换、
/// ffmpeg、ripgrep、自定义脚本），包装 CLI 的成本是几十行，而它们不需要常驻、
/// 不需要协议握手。MCP 的价值在「跨宿主复用」和「有状态长连接」，
/// 这两条在单机桌宠上不是刚需。
///
/// 安全边界（这是这个类最重要的部分，因为它是本项目唯一能执行外部程序的工具）：
///
/// 1. **白名单**：只有构造时列出的可执行文件能被调用，路径必须已存在。
///    不接受用户临时指定的任意程序——那等于把 shell 交给模型。
/// 2. **不经过 shell**：用 <c>ProcessStartInfo.ArgumentList</c> 逐项传参，
///    不做字符串拼接，因此不存在引号逃逸与命令注入。
/// 3. **强制超时**：进程超时即杀，避免一个卡住的子进程拖死整轮对话。
/// 4. **输出截断**：CLI 输出可能极大，截断后再交给模型。
/// 5. 策略默认 <see cref="AgentToolEffect.ExternalMutation"/> +
///    <see cref="AgentToolPolicy.RequiresExplicitIntent"/>：
///    外部程序可能改文件、发网络请求，必须由用户明确要求才跑。
/// </summary>
public sealed class CliTool : IAgentTool
{
    private readonly string _executable;
    private readonly IReadOnlyList<string> _fixedArguments;
    private readonly TimeSpan _timeout;
    private readonly int _maxOutputCharacters;

    /// <param name="executable">可执行文件的完整路径（白名单项）。</param>
    /// <param name="fixedArguments">
    /// 固定参数前缀。**不要在这里放用户可控内容**；用户输入只会作为最后一项追加。
    /// </param>
    public CliTool(
        string name,
        string description,
        string executable,
        IEnumerable<string>? fixedArguments = null,
        TimeSpan? timeout = null,
        int maxOutputCharacters = 8000,
        AgentToolPolicy? policy = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("工具名不能为空。", nameof(name));
        if (!File.Exists(executable))
            throw new FileNotFoundException($"CLI 工具的可执行文件不存在（白名单校验）：{executable}", executable);

        Name = name;
        Description = description;
        _executable = Path.GetFullPath(executable);
        _fixedArguments = (fixedArguments ?? []).ToArray();
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _maxOutputCharacters = maxOutputCharacters;
        // 外部程序的默认边界最严：会改外部状态，且必须用户明确要求。
        Policy = policy ?? new AgentToolPolicy(
            AgentToolEffect.ExternalMutation,
            AgentToolSensitivity.None,
            RequiresExplicitIntent: true,
            CanRunInParallel: false);
    }

    public string Name { get; }
    public string Description { get; }
    public AgentToolPolicy Policy { get; }

    public async Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in _fixedArguments)
            startInfo.ArgumentList.Add(argument);
        // 用户输入作为**最后一项单独参数**传入，不参与任何字符串拼接。
        if (!string.IsNullOrWhiteSpace(input))
            startInfo.ArgumentList.Add(input);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return $"{Name}：无法启动 {Path.GetFileName(_executable)}。";

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已经退了 */ }
            return $"{Name}：超过 {_timeout.TotalSeconds:0} 秒未结束，已终止。";
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var combined = stdout.Length > 0 ? stdout : stderr;
        if (string.IsNullOrWhiteSpace(combined))
            return $"{Name}：没有输出（退出码 {process.ExitCode}）。";

        var text = combined.Trim();
        if (text.Length > _maxOutputCharacters)
            text = text[.._maxOutputCharacters] + $"\n…（输出被截断，原长 {combined.Length} 字符）";
        return process.ExitCode == 0 ? text : $"{text}\n（退出码 {process.ExitCode}）";
    }

    /// <summary>
    /// 从配置建一个 CLI 工具。**白名单在调用方**：只有这里列出的程序能被包装。
    /// </summary>
    public static IAgentTool? TryCreate(
        string name, string description, string executable,
        IEnumerable<string>? fixedArguments = null, Action<string>? log = null)
    {
        if (!File.Exists(executable))
        {
            log?.Invoke($"[tools] CLI 工具 {name} 跳过：找不到 {executable}");
            return null;
        }
        return new CliTool(name, description, executable, fixedArguments);
    }
}
