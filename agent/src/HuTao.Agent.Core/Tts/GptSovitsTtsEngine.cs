using System.Diagnostics;
using HuTao.Agent.Core.Abstractions;

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

    public GptSovitsTtsEngine(
        string python,
        string inferScript,
        string gptModel,
        string sovitsModel,
        string outputDir)
    {
        _python = python;
        _inferScript = inferScript;
        _gptModel = gptModel;
        _sovitsModel = sovitsModel;
        _outputDir = outputDir;
    }

    public string Name => "gpt-sovits";

    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_outputDir);
        var outPath = Path.Combine(_outputDir, $"agent_tts_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wav");

        var psi = new ProcessStartInfo
        {
            FileName = _python,
            UseShellExecute = false,
            // 注意：沙箱/受控环境下子进程若走命名管道捕获输出可能受限；这里让输出直接继承终端，
            // 只在必要时才重定向。生产部署时再按需改为重定向。
        };
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

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 TTS Python 进程");

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"TTS 合成失败，退出码 {process.ExitCode}");

        if (!File.Exists(outPath))
            throw new FileNotFoundException($"TTS 未生成音频文件: {outPath}");

        return new TtsResult(outPath, SampleRate: 24000, DurationSeconds: 0);
    }
}
