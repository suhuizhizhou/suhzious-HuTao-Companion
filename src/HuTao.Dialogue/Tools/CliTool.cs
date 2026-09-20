using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Tools;

/// <summary>Audited version probes only. ArgumentList is transport, not an authorization boundary.</summary>
public sealed class CliTool : IAgentTool
{
    private readonly string _executable;
    private readonly string[] _arguments;
    private readonly byte[] _fingerprint;
    private readonly string _workingDirectory;
    private readonly TimeSpan _timeout;
    private readonly int _maxOutput;

    public CliTool(string name, string description, string executable,
        IEnumerable<string>? fixedArguments = null, TimeSpan? timeout = null,
        int maxOutputCharacters = 4000, AgentToolPolicy? policy = null, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("missing_tool_name");
        if (!Path.IsPathFullyQualified(executable) || executable.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("local_absolute_executable_required");
        ToolWorkspace.RejectLinks(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("CLI executable not found.", executable);
        _arguments = (fixedArguments ?? []).ToArray();
        var binary = Path.GetFileName(executable).ToLowerInvariant();
        var permitted = binary switch
        {
            "dotnet.exe" or "git.exe" or "rg.exe" => _arguments.SequenceEqual(["--version"]),
            "ffmpeg.exe" or "ffprobe.exe" => _arguments.SequenceEqual(["-version"]),
            _ => false
        };
        if (!permitted) throw new ArgumentException("cli_profile_not_allowed: only fixed version probes are supported");
        _executable = Path.GetFullPath(executable);
        _workingDirectory = Path.GetFullPath(workingDirectory ?? Path.GetDirectoryName(_executable)!);
        ToolWorkspace.RejectLinks(_workingDirectory);
        if (!Directory.Exists(_workingDirectory)) throw new DirectoryNotFoundException(_workingDirectory);
        using (var file = File.OpenRead(_executable)) _fingerprint = SHA256.HashData(file);
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _maxOutput = Math.Clamp(maxOutputCharacters, 256, 8000);
        Name = name; Description = description;
        // Callers cannot downgrade a process launch to an automatic observation.
        Policy = new(AgentToolEffect.ExternalMutation, RequiresExplicitIntent: true,
            CanRunInParallel: false, RequiresConfirmation: true, TimeoutSeconds: 30, MaxOutputCharacters: _maxOutput);
    }

    public string Name { get; }
    public string Description { get; }
    public AgentToolPolicy Policy { get; }
    public ToolInputSchema InputSchema => ToolInputSchema.Empty;
    public bool UsesJsonArguments => true;
    public string DescribeAction(JsonElement arguments) =>
        $"启动固定版本查询：{_executable} {string.Join(' ', _arguments)}\n工作目录：{_workingDirectory}\n" +
        "不接受用户参数、输入路径、输出路径、网络地址或脚本。";

    public string? ValidateArguments(JsonElement arguments)
    {
        ToolWorkspace.RejectLinks(_executable);
        ToolWorkspace.RejectLinks(_workingDirectory);
        using var file = File.OpenRead(_executable);
        return CryptographicOperations.FixedTimeEquals(_fingerprint, SHA256.HashData(file)) ? null : "executable_changed";
    }

    public async Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default)
    {
        using var args = JsonDocument.Parse(input ?? "{}");
        var error = InputSchema.Validate(args.RootElement) ?? ValidateArguments(args.RootElement);
        if (error is not null) throw new ArgumentException(error);
        var start = new ProcessStartInfo(_executable) { WorkingDirectory = _workingDirectory };
        foreach (var argument in _arguments) start.ArgumentList.Add(argument);
        // Do not inherit API keys, proxy settings, runtime startup hooks or user-supplied CLI configuration.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment.Clear();
        start.Environment["SystemRoot"] = windows;
        start.Environment["WINDIR"] = windows;
        start.Environment["PATH"] = Path.GetDirectoryName(_executable)! + Path.PathSeparator + Path.Combine(windows, "System32");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        var result = await OwnedProcessRunner.RunAsync(start, _timeout, _maxOutput, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("cli_nonzero_exit:" + result.ExitCode);
        return JsonSerializer.Serialize(result);
    }

    public static IAgentTool? TryCreate(string name, string description, string executable,
        IEnumerable<string>? fixedArguments = null, Action<string>? log = null, string? workingDirectory = null)
    {
        try { return new CliTool(name, description, executable, fixedArguments, workingDirectory: workingDirectory); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { log?.Invoke($"[tools] CLI {name} unavailable: {ex.GetType().Name}"); return null; }
    }
}
