using HuTao.Foundation.Abstractions;

namespace HuTao.Voice;

/// <summary>
/// 退出状态与诊断分离；Diagnostic 只能交给日志，不用作角色台词。
///
/// 实现 <see cref="IProcessFailure"/> 是为了让 Foundation 层的诊断日志能读到
/// 退出码与 stderr，而**不必反向依赖 Voice 层**（那样拆程序集就会成环）。
/// </summary>
public sealed class TtsProcessException(int exitCode, string diagnostic)
    : InvalidOperationException($"TTS subprocess failed ({exitCode})"), IProcessFailure
{
    public int ExitCode { get; } = exitCode;
    public string Diagnostic { get; } = diagnostic;
}
