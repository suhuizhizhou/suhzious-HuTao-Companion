using System.Diagnostics;
using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Runtime;

namespace HuTao.Agent.Core.Tts;

/// <summary>
/// GPT-SoVITS v3 合成引擎：通过启动 Python 子进程调用 few_shot_infer.py。
/// 这是当前默认实现；未来换 CosyVoice / 微调模型 / 流式引擎时，
/// 只需新增一个 ITtsEngine 实现，再在配置里改 TtsEngine 名称即可，业务层无感知。
/// </summary>
public sealed class GptSovitsTtsEngine : ITtsEngine
{
    private readonly string _python;
    private readonly string _inferScript;
    private readonly string _gptModel;
    private readonly string _sovitsModel;
    private readonly string _outputDir;
    private readonly string? _pythonPath;

    public GptSovitsTtsEngine(
        string python,
        string inferScript,
        string gptModel,
        string sovitsModel,
        string outputDir,
        string? pythonPath = null)
    {
        _python = python;
        _inferScript = inferScript;
        _gptModel = gptModel;
        _sovitsModel = sovitsModel;
        _outputDir = outputDir;
        _pythonPath = pythonPath;
    }

    public string Name => "gpt-sovits";

    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct = default)
    {
        if (!PortablePythonRuntime.TryPrepare(_python, _pythonPath))
            throw new InvalidOperationException("便携 Python 初始化失败");

        Directory.CreateDirectory(_outputDir);
        var outPath = Path.Combine(_outputDir, $"agent_tts_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wav");

        var psi = new ProcessStartInfo
        {
            FileName = _python,
            UseShellExecute = false,
            // 按需模式也不弹出新的控制台窗口；常态化模式由 ResidentGptSovitsTtsEngine 负责复用进程。
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        PortablePythonRuntime.ConfigureProcess(psi, _python, _pythonPath);
        psi.ArgumentList.Add(_inferScript);
        psi.ArgumentList.Add("--ref_audio");
        psi.ArgumentList.Add(request.RefAudioPath ?? throw new ArgumentNullException(nameof(request.RefAudioPath), "few-shot 需要参考音频"));
        psi.ArgumentList.Add("--ref_text");
        psi.ArgumentList.Add(request.RefText ?? "");
        psi.ArgumentList.Add("--text");
        psi.ArgumentList.Add(request.Text);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(outPath);
        psi.ArgumentList.Add("--gpt_model");
        psi.ArgumentList.Add(_gptModel);
        psi.ArgumentList.Add("--sovits_model");
        psi.ArgumentList.Add(_sovitsModel);
        psi.ArgumentList.Add("--temperature");
        psi.ArgumentList.Add(Math.Clamp(request.Temperature, 0.4, 0.9).ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--speed_factor");
        psi.ArgumentList.Add(Math.Clamp(request.SpeedFactor, 0.8, 1.15).ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 TTS Python 进程");
        // 两条管道同时排空，避免输出缓冲区写满导致子进程死锁。只保留 stderr 尾部。
        var stdout = ReadDiagnosticTailAsync(process.StandardOutput, 0);
        var stderr = ReadDiagnosticTailAsync(process.StandardError, 16384);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已在取消回调与此处之间退出。
            }
            throw;
        }

        await stdout.ConfigureAwait(false);
        var diagnostic = await stderr.ConfigureAwait(false);
        // 推理异常可能打印当前文本/参考台词，落日志前移除。
        foreach (var value in new[] { request.Text, request.RefText }.Where(v => !string.IsNullOrEmpty(v)))
            diagnostic = diagnostic.Replace(value!, "[speech-text]", StringComparison.Ordinal);

        if (process.ExitCode != 0)
            throw new TtsProcessException(process.ExitCode, diagnostic);

        if (!File.Exists(outPath))
            throw new FileNotFoundException($"TTS 未生成音频文件: {outPath}");

        return new TtsResult(outPath, SampleRate: 24000, DurationSeconds: 0);
    }

    private static async Task<string> ReadDiagnosticTailAsync(StreamReader reader, int capacity)
    {
        var buffer = new char[2048];
        var tail = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (capacity == 0) continue;
            tail.Append(buffer, 0, count);
            if (tail.Length > capacity) tail.Remove(0, tail.Length - capacity);
        }
        return tail.ToString();
    }
}
