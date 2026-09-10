using System.Text.Json;
using System.Text.RegularExpressions;
using HuTao.Agent.Core.Tts;

namespace HuTao.Agent.Core.Diagnostics;

/// <summary>技术诊断只落本机文件，不进入角色消息或 LLM 历史。日志故障不得影响聊天。</summary>
public sealed class LocalDiagnosticLog
{
    private readonly string _path;
    private readonly object _gate = new();
    public static LocalDiagnosticLog Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HuTaoCompanion", "logs", "runtime.log"));

    public LocalDiagnosticLog(string path) => _path = path;
    public string FilePath => _path;

    public void Write(string stage, Exception error)
    {
        try
        {
            // 普通异常不存任意 Message（HTTP 返回体可能含密钥或用户原文）。
            var row = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.Now, stage, type = error.GetType().FullName,
                stack = error.StackTrace,
                inner_type = error.InnerException?.GetType().FullName,
                exit_code = (error as TtsProcessException)?.ExitCode,
                stderr = error is TtsProcessException process ? Redact(process.Diagnostic) : null
            });
            Append(row);
        }
        catch
        {
            // 无权限/磁盘满：不得因记日志失败再次抛异常。
        }
    }

    /// <summary>只存状态码和计数，不存用户输入、模型正文、请求头或密钥。</summary>
    public void Turn(string turnId, string trigger, string stage, string path,
        string? status = null, int evidenceCount = 0, int segmentCount = 0,
        IReadOnlyList<string>? issues = null)
    {
        try { Append(JsonSerializer.Serialize(new { time = DateTimeOffset.Now, turn_id = turnId,
            trigger, stage, path, status, evidence_count = evidenceCount, segment_count = segmentCount,
            issues = issues ?? [] })); }
        catch { }
    }

    private void Append(string row)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            if (File.Exists(_path) && new FileInfo(_path).Length >= 2 * 1024 * 1024)
                File.Move(_path, _path + ".previous", overwrite: true);
            File.AppendAllText(_path, row + Environment.NewLine);
        }
    }

    internal static string Redact(string value) => Regex.Replace(
        Regex.Replace(value, @"(?i)\bsk-[a-z0-9_-]+", "[redacted-key]"),
        @"(?i)(Bearer\s+|(?:api[_-]?key|token|password)\s*[:=]\s*)[^\s,;]+", "$1[redacted]");
}
