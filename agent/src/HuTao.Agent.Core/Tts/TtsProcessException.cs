namespace HuTao.Agent.Core.Tts;

/// <summary>退出状态与诊断分离；Diagnostic 只能交给日志，不用作角色台词。</summary>
public sealed class TtsProcessException(int exitCode, string diagnostic)
    : InvalidOperationException($"TTS subprocess failed ({exitCode})")
{
    public int ExitCode { get; } = exitCode;
    public string Diagnostic { get; } = diagnostic;
}
