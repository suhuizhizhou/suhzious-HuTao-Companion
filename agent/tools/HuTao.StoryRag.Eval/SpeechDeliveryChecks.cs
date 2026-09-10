using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Core;
using HuTao.Agent.Core.Diagnostics;
using HuTao.Agent.Core.Tts;

/// <summary>无 Python、无网络、无播放器、无 GPU 的语音降级回归。</summary>
internal static class SpeechDeliveryChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool ok) => checks.Add(new(name, ok, ""));
        var root = Path.Combine(Path.GetTempPath(), "hutao-speech-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check("speech_legacy_diagnostics_hidden", SpeechDeliverySession.IsLegacyFailureBubble("assistant", "（说话卡住了：TTS合成失败，退出码1）"));
            Check("speech_user_technical_text_preserved", !SpeechDeliverySession.IsLegacyFailureBubble("user", "（说话卡住了：TTS合成失败，退出码1）"));
            Check("speech_normal_assistant_text_preserved", !SpeechDeliverySession.IsLegacyFailureBubble("assistant", "Python 退出码表示进程结束状态。"));
            var calls = 0;
            var logs = new List<string>();
            var log = new LocalDiagnosticLog(Path.Combine(root, "runtime.log"));
            var session = new SpeechDeliverySession((stage, error) => { logs.Add(stage); log.Write(stage, error); });
            Task<TtsResult?> Fail(CancellationToken ct)
            {
                calls++;
                throw new TtsProcessException(1, "RuntimeError: CUDA out of memory; sk-testsecret123");
            }
            var texts = new[] { "钟离呀？当然认识。", "他是往生堂的客卿。", "（晃了晃帽子）" };
            var entries = new List<(string Text, string? Audio)>();
            foreach (var text in texts)
            {
                var segment = new SpeechSegment(text, "neutral", 0.5);
                entries.Add((segment.Text, await session.PrepareAudioAsync(segment, Fail)));
            }
            Check("speech_failure_preserves_all_text", entries.Select(e => e.Text).SequenceEqual(texts));
            Check("speech_failure_no_audio_or_error_bubble", entries.All(e => e.Audio is null) && entries.Count == texts.Length);
            Check("speech_failure_no_retry_in_turn", calls == 1 && logs.Count == 1);
            var logged = File.ReadAllText(log.FilePath);
            Check("speech_failure_log_exit_and_stderr", logged.Contains("CUDA out of memory") && logged.Contains("\"exit_code\":1"));
            Check("speech_log_redacts_key", !logged.Contains("sk-testsecret123"));

            var file = Path.Combine(root, "stub.wav");
            await File.WriteAllBytesAsync(file, [82, 73, 70, 70]); // 只检验交付路径，不播放伪音频。
            var next = new SpeechDeliverySession((_, _) => { });
            var spoken = new SpeechSegment("下一轮", "neutral", 0.5);
            var recovered = await next.PrepareAudioAsync(spoken, _ => Task.FromResult<TtsResult?>(new(file, 24000, 0)));
            Check("speech_next_turn_can_recover", recovered == file);
            calls = 0;
            var original = await next.PrepareAudioAsync(spoken with { OriginalAudioPath = file }, Fail);
            Check("speech_original_audio_no_synthesis", original == file && calls == 0);
            var action = await next.PrepareAudioAsync(spoken with { Text = "（翻开档案）", OriginalAudioPath = file }, Fail);
            Check("speech_action_always_silent", action is null && calls == 0);

            var missing = await new SpeechDeliverySession((_, _) => { }).PrepareAudioAsync(
                spoken with { OriginalAudioPath = Path.Combine(root, "missing.wav") }, Fail);
            Check("speech_missing_original_text_only", missing is null && calls == 0);
            var nullAudio = await new SpeechDeliverySession().PrepareAudioAsync(spoken, _ => Task.FromResult<TtsResult?>(null));
            Check("speech_disabled_text_only", nullAudio is null);
            var timeout = await new SpeechDeliverySession((_, _) => { }).PrepareAudioAsync(spoken,
                _ => throw new OperationCanceledException("internal timeout"));
            Check("speech_internal_timeout_text_only", timeout is null);
            using var cancel = new CancellationTokenSource();
            var canceled = false;
            try
            {
                await new SpeechDeliverySession((_, _) => throw new Exception("must not log cancel"))
                    .PrepareAudioAsync(spoken, ct => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult<TtsResult?>(null); }, cancel.Token);
            }
            catch (OperationCanceledException) { canceled = true; }
            Check("speech_user_cancellation_propagates", canceled);
            var logFailure = await new SpeechDeliverySession((_, _) => throw new IOException("disk full"))
                .PrepareAudioAsync(spoken, Fail);
            Check("speech_logging_failure_still_text_only", logFailure is null);
            var blockedLog = new LocalDiagnosticLog(root); // 路径为目录，写入必失败。
            blockedLog.Write("test", new IOException("private-user-content"));
            Check("speech_logger_io_failure_is_nonfatal", true);
        }
        catch (Exception ex) { checks.Add(new("speech_unexpected_exception", false, ex.GetType().Name)); }
        finally
        {
            // 仅删除本次创建的 GUID 测试目录，不触碰用户音频/训练产物。
            Directory.Delete(root, recursive: true);
        }
        return checks;
    }
}
