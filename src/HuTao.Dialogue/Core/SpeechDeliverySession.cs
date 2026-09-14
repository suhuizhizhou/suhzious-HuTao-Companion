using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;

namespace HuTao.Dialogue.Core;

/// <summary>单轮聊天的音频是可选附件。一次失败后该轮只出文字，不重新生成回复或重试模型。</summary>
public sealed class SpeechDeliverySession
{
    private readonly Action<string, Exception> _log;
    private bool _textOnly;
    public SpeechDeliverySession(Action<string, Exception>? log = null)
        => _log = log ?? LocalDiagnosticLog.Default.Write;

    /// <summary>仅识别旧版本程序注入的固定错误前缀；不筛掉用户讨论技术问题的消息。</summary>
    public static bool IsLegacyFailureBubble(string role, string text)
        => role == "assistant" && (text.StartsWith("（说话卡住了：", StringComparison.Ordinal) ||
            text.StartsWith("切换角色出错了：", StringComparison.Ordinal));

    public async Task<string?> PrepareAudioAsync(SpeechSegment segment,
        Func<CancellationToken, Task<TtsResult?>> synthesize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (SpeechText.IsAction(segment.Text) || _textOnly) return null;
        try
        {
            var path = segment.OriginalAudioPath;
            if (path is null)
                path = (await synthesize(ct).ConfigureAwait(false))?.AudioPath;
            ct.ThrowIfCancellationRequested();
            if (path is null) { _textOnly = true; return null; }
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new FileNotFoundException("Speech audio unavailable");
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _textOnly = true;
            try { _log("speech.text_only", ex); } catch { /* 诊断旁路不能破坏文字交付 */ }
            return null;
        }
    }
}
