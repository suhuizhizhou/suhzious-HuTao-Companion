using System.Diagnostics;
using System.Text;

namespace HuTao.Agent.Core.DocumentReading;

/// <summary>FFmpeg 合并适配器。音频编码细节隔离在此处，便于未来接入其他编码器。</summary>
public sealed class FfmpegAudioSegmentMerger : IAudioSegmentMerger
{
    private readonly string? _configuredPath;
    private readonly int _bitrateKbps;

    public FfmpegAudioSegmentMerger(string? configuredPath, int bitrateKbps = 192)
    {
        _configuredPath = configuredPath;
        _bitrateKbps = bitrateKbps;
    }

    public async Task MergeToMp3Async(
        IReadOnlyList<string> wavs,
        string outputPath,
        CancellationToken ct = default)
    {
        if (wavs.Count == 0)
            throw new ArgumentException("没有可合并的音频片段。", nameof(wavs));

        var ffmpeg = ResolveFfmpeg();
        var concatPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "concat.txt");
        var concatLines = wavs.Select(path => $"file '{EscapeConcatPath(path)}'");
        await File.WriteAllLinesAsync(concatPath, concatLines, new UTF8Encoding(false), ct)
            .ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            WorkingDirectory = Path.GetDirectoryName(outputPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("concat");
        psi.ArgumentList.Add("-safe");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(concatPath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-codec:a");
        psi.ArgumentList.Add("libmp3lame");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add($"{_bitrateKbps}k");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "无法启动 FFmpeg，请安装 FFmpeg 或设置 HU_TAO_FFMPEG。");
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(outputPath))
            throw new InvalidOperationException(
                $"FFmpeg 合并 MP3 失败（退出码 {process.ExitCode}）：{error.Trim()}");
    }

    private string ResolveFfmpeg()
    {
        var candidates = new[]
        {
            _configuredPath,
            Environment.GetEnvironmentVariable("HU_TAO_FFMPEG"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
        };
        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (File.Exists(candidate!))
                return Path.GetFullPath(candidate!);
        }

        // 允许使用 PATH 中的 ffmpeg；Process.Start 会在不可用时返回清晰错误。
        return "ffmpeg";
    }

    private static string EscapeConcatPath(string path)
        => path.Replace("'", "'\\''");
}
