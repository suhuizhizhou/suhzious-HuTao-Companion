namespace HuTao.Agent.Core.Abstractions;

public sealed record SpeechRecognitionRequest(
    Stream Audio,
    string Language = "zh-CN");

public sealed record SpeechRecognitionResult(
    string Text,
    double Confidence);

/// <summary>未来语音输入的 ASR 边界；实现可来自本地 Python 服务或云端接口。</summary>
public interface ISpeechRecognitionEngine
{
    Task<SpeechRecognitionResult> TranscribeAsync(
        SpeechRecognitionRequest request,
        CancellationToken ct = default);
}
