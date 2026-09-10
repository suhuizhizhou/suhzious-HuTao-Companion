using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Tts;
using HuTao.Agent.Core.Diagnostics;

namespace HuTao.Agent.Core.DocumentReading;

/// <summary>
/// 长文朗读应用服务：只负责编排步骤，不关心具体文档格式、情绪策略或编码器。
/// 这是 UI 与 Agent Tool 的共同入口。
/// </summary>
public sealed class DocumentReadingService : IDocumentReadingService
{
    private readonly ITtsEngine? _tts;
    private readonly IDocumentTextExtractor _extractor;
    private readonly IReadingPlanner _planner;
    private readonly IReadingEmotionSelector _emotionSelector;
    private readonly IReadingJobStore _jobStore;
    private readonly IAudioSegmentMerger _audioMerger;
    private readonly string _fallbackAudio;
    private readonly string _fallbackText;
    private readonly string _speakerName;
    private readonly int _progressEveryChunks;
    private readonly Action<string>? _progress;
    private readonly SemaphoreSlim _jobGate = new(1, 1);
    private string? _activeJobDirectory;
    private int _completedChunks;

    public DocumentReadingService(
        ITtsEngine? tts,
        string? refAudio,
        string? refText,
        EmotionReferenceCatalog? emotionReferences,
        string outputRoot,
        string? ffmpegPath = null,
        string speakerName = "角色",
        Action<string>? progress = null,
        DocumentReadingOptions? options = null)
        : this(
            tts,
            refAudio,
            refText,
            new DocumentTextExtractor(),
            new ReadingPlanBuilder(options),
            new ReadingEmotionSelector(emotionReferences, refAudio, refText),
            new FileReadingJobStore(outputRoot),
            new FfmpegAudioSegmentMerger(ffmpegPath, options?.Mp3BitrateKbps ?? 192),
            speakerName,
            options?.ProgressEveryChunks ?? 5,
            progress)
    {
    }

    public DocumentReadingService(
        ITtsEngine? tts,
        string? refAudio,
        string? refText,
        IDocumentTextExtractor extractor,
        IReadingPlanner planner,
        IReadingEmotionSelector emotionSelector,
        IReadingJobStore jobStore,
        IAudioSegmentMerger audioMerger,
        string speakerName = "角色",
        int progressEveryChunks = 5,
        Action<string>? progress = null)
    {
        _tts = tts;
        _fallbackAudio = refAudio ?? "";
        _fallbackText = refText ?? "";
        _extractor = extractor;
        _planner = planner;
        _emotionSelector = emotionSelector;
        _jobStore = jobStore;
        _audioMerger = audioMerger;
        _speakerName = string.IsNullOrWhiteSpace(speakerName) ? "角色" : speakerName.Trim();
        _progressEveryChunks = Math.Max(1, progressEveryChunks);
        _progress = progress;
    }

    public async Task<DocumentReadingResult> ReadAsync(
        string documentPath,
        CancellationToken ct = default)
    {
        var source = DocumentPath.Normalize(documentPath);
        if (source is null)
            return Failed(documentPath, "只支持存在的 .txt 或 .docx 文件。", 0);

        // 云端 TTS 可能不需要参考音频；本地 GPT-SoVITS 的参考音频校验由具体引擎负责。
        if (_tts is null)
        {
            return Failed(source, "当前角色没有可用的语音模型或参考音频，无法生成 MP3。", 0);
        }

        await _jobGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _activeJobDirectory = null;
            _completedChunks = 0;
            return await ReadCoreAsync(source, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Report("（朗读任务已取消，已保留已生成的分段音频）");
            return Failed(
                source,
                "任务已取消，已保留已生成的分段 WAV。",
                _completedChunks,
                _activeJobDirectory ?? "",
                cancelled: true);
        }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("document.pipeline", ex);
            Report("（朗读暂时卡住了，已保留能够完成的部分）");
            return Failed(
                source,
                "文档朗读未完成。",
                _completedChunks,
                _activeJobDirectory ?? "");
        }
        finally
        {
            _jobGate.Release();
        }
    }

    private async Task<DocumentReadingResult> ReadCoreAsync(
        string source,
        CancellationToken ct)
    {
        Report($"（{_speakerName}正在整理稿件，先按章节和段落分好朗读阶段~）");
        var paragraphs = _extractor.Extract(source);
        var chunks = _planner.Build(paragraphs);
        if (chunks.Count == 0)
            return Failed(source, "文档中没有可朗读的正文。", 0);

        var job = _jobStore.Create(source);
        _activeJobDirectory = job.JobDirectory;
        await _jobStore.SavePlanAsync(job, paragraphs, chunks, ct).ConfigureAwait(false);

        var wavs = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            if (chunk.Index == 1 ||
                chunk.Index == chunks.Count ||
                (chunk.Index - 1) % _progressEveryChunks == 0)
            {
                Report($"（{_speakerName}正在念稿子：第 {chunk.Index}/{chunks.Count} 段，{chunk.Stage}，{EmotionLabel(chunk.Emotion)}）");
            }

            var style = _emotionSelector.Select(chunk);
            var ttsResult = await _tts!.SynthesizeAsync(
                new TtsRequest(
                    chunk.Text,
                    SelectReferenceAudio(style),
                    SelectReferenceText(style),
                    SpeedFactor: style.SpeedFactor,
                    Temperature: style.Temperature),
                ct).ConfigureAwait(false);
            var wavPath = _jobStore.GetSegmentPath(job, chunk);
            File.Copy(ttsResult.AudioPath, wavPath, overwrite: true);
            wavs.Add(wavPath);
            _completedChunks = wavs.Count;
        }

        Report($"（稿子已经念完啦，{_speakerName}正在把分段音频合成一份 MP3~）");
        await _audioMerger.MergeToMp3Async(wavs, job.Mp3Path, ct).ConfigureAwait(false);
        Report($"（MP3 做好啦：{job.Mp3Path}）");
        return new DocumentReadingResult(
            true,
            source,
            job.Mp3Path,
            job.JobDirectory,
            chunks.Count);
    }

    private string SelectReferenceAudio(EmotionVoiceStyle style)
        => !string.IsNullOrWhiteSpace(style.RefAudioPath) && File.Exists(style.RefAudioPath)
            ? style.RefAudioPath
            : _fallbackAudio;

    private string SelectReferenceText(EmotionVoiceStyle style)
        => !string.IsNullOrWhiteSpace(style.RefText)
            ? style.RefText
            : _fallbackText;

    private void Report(string message) => _progress?.Invoke(message);

    private static DocumentReadingResult Failed(
        string source,
        string error,
        int chunks,
        string jobDirectory = "",
        bool cancelled = false)
        => new(false, source, null, jobDirectory, chunks, error, cancelled);

    private static string EmotionLabel(string emotion)
        => emotion switch
        {
            "cheerful" => "活泼元气",
            "teasing" => "俏皮",
            "concerned" => "温柔关心",
            "angry" => "严肃有力",
            "sleepy" => "轻声舒缓",
            _ => "自然叙述",
        };
}

internal static class DocumentPath
{
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var fullPath = Path.GetFullPath(path.Trim().Trim('"', '\''));
            var extension = Path.GetExtension(fullPath);
            return File.Exists(fullPath) &&
                   (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
                ? fullPath
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
