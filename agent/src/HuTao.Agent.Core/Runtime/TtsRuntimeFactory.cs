using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Tts;

namespace HuTao.Agent.Core.Runtime;

/// <summary>集中创建 GPT-SoVITS 运行时，Host 与 WPF 共用路径、模式和降级规则。</summary>
public static class TtsRuntimeFactory
{
    public static ITtsEngine? CreateGptSovits(
        string repoRoot,
        string? pythonOverride = null,
        bool preferResident = true,
        Action<string>? log = null)
    {
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
}
