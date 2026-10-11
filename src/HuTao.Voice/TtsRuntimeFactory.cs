using HuTao.Foundation.Abstractions;
using HuTao.Voice;

namespace HuTao.Voice;

/// <summary>集中创建 GPT-SoVITS 运行时，Host 与 WPF 共用路径、模式和降级规则。</summary>
public static class TtsRuntimeFactory
{
    public static ITtsEngine? CreateGptSovits(
        string repoRoot,
        string? pythonOverride = null,
        bool preferResident = true,
        Action<string>? log = null)
    {
        // 强制文字模式。存在的理由是**真实联调**：语音合成每段要几十秒，
        // 而绝大多数联调要验的是"用户的话有没有进模型、回复有没有分段"，
        // 跟音频无关。开着 TTS 只会让每轮测试慢一个数量级。
        // 项目本来就有文字降级路径（SpeechDeliverySession 在合成失败后 _textOnly），
        // 所以这里直接不建引擎即可，不需要另写一条链路。
        if (IsTruthy(Environment.GetEnvironmentVariable("HU_TAO_TTS_DISABLED")))
        {
            log?.Invoke("[tts] HU_TAO_TTS_DISABLED 已设置，使用文字模式（不合成语音）");
            return null;
        }

        var root = Path.GetFullPath(repoRoot);
        var bundledPython = Path.Combine(root, "voice", "python", "python.exe");
        var developerPython = Path.Combine(root, "voice", ".venv", "Scripts", "python.exe");
        var python = pythonOverride
                     ?? Environment.GetEnvironmentVariable("HU_TAO_TTS_PYTHON")
                     ?? (File.Exists(bundledPython) ? bundledPython : developerPython);
        if (!File.Exists(python))
        {
            log?.Invoke($"[tts] 找不到 Python，使用文字模式: {python}");
            return null;
        }

        var bundledPackages = Path.Combine(root, "voice", "python-packages");
        var pythonPath = Directory.Exists(bundledPackages) ? bundledPackages : null;

        var outputDirectory = Path.Combine(root, "data", "voice", "generated");
        var inferScript = Path.Combine(root, "voice", "infer", "few_shot_infer.py");
        var serverScript = Path.Combine(root, "voice", "infer", "resident_server.py");
        var gptModel = Path.Combine(
            root,
            "voice",
            "GPT-SoVITS-main",
            "GPT_SoVITS",
            "pretrained_models",
            "s1v3.ckpt");
        var sovitsModel = Path.Combine(
            root,
            "voice",
            "GPT-SoVITS-main",
            "GPT_SoVITS",
            "pretrained_models",
            "s2Gv3.pth");
        var onDemand = new GptSovitsTtsEngine(
            python,
            inferScript,
            gptModel,
            sovitsModel,
            outputDirectory,
            pythonPath);

        var configuredMode = Environment.GetEnvironmentVariable("HU_TAO_TTS_MODE")
            ?.Trim()
            .ToLowerInvariant();
        var useResident = configuredMode switch
        {
            "on-demand" or "ondemand" or "process" => false,
            "resident" or "server" => true,
            _ => preferResident,
        };
        if (!useResident)
        {
            log?.Invoke("[tts] 使用 GPT-SoVITS 按需进程模式");
            return onDemand;
        }

        var url = Environment.GetEnvironmentVariable("HU_TAO_TTS_URL")
                  ?? "http://127.0.0.1:9881/";
        var resident = new ResidentGptSovitsTtsEngine(
            new Uri(url),
            outputDirectory,
            python,
            serverScript,
            gptModel,
            sovitsModel,
            pythonPath: pythonPath,
            device: Environment.GetEnvironmentVariable("HU_TAO_TTS_DEVICE") ?? "cuda",
            half: !string.Equals(
                Environment.GetEnvironmentVariable("HU_TAO_TTS_HALF"),
                "false",
                StringComparison.OrdinalIgnoreCase));
        resident.StartInBackground();
        log?.Invoke("[tts] 使用 GPT-SoVITS 常驻服务，并保留按需模式作为降级");
        return new FallbackTtsEngine(resident, onDemand);
    }

    /// <summary>"1/true/on/yes" 视为真；其余（含未设置）为假。</summary>
    private static bool IsTruthy(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (value.Trim() is "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("on", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
}
