using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Core;
using HuTao.Agent.Core.Llm;
using HuTao.Agent.Core.Persona;
using HuTao.Agent.Core.Tools;
using HuTao.Agent.Core.Tts;

// ── 装配（依赖注入风格，全部走接口，方便后续切换）──
// 用法：dotnet run --project src/HuTao.Agent.Host [persona目录] [可选: tts-python路径]

LoadEnvFile(); // 先加载项目根目录的 .env（DEEPSEEK_API_KEY 等），不覆盖已有环境变量

var personaRoot = ResolvePersonaRoot(args);
var repoRoot = FindRepoRoot() ?? AppContext.BaseDirectory;
var persona = PersonaLoader.Load(personaRoot);
Console.WriteLine($"[persona] 已加载人设: {persona.Name}");

IEnumerable<IAgentTool> tools =
[
    new TimeTool(),
    new ActiveWindowTool(() => ReadBooleanEnvironment("HU_TAO_ALLOW_APP_AWARENESS")),
    new IdleTool(),
];
var llm = BuildLlm(persona);

// TTS 引擎：预留切换。传入 python 路径则接 GPT-SoVITS，否则只出文字（方便先跑通流程）。
ITtsEngine? tts = BuildTts(args);

var agent = new ReactAgent(
    persona,
    llm,
    tts,
    tools,
    refAudio: Path.Combine(repoRoot, "data", "voice", "hutao", "wav", "a4eedbf833d51d47.wav"),
    refText: "哼哼，切勿质疑我的业务能力！");

// ── 演示 1：回应用户输入 ──
Console.WriteLine("\n========== 演示 1：回应用户 ==========");
var turn1 = await agent.RespondAsync("你好，介绍一下自己吧");
PrintTurn(turn1);

// ── 演示 2：主动搭话（观察环境后自己找话题）──
Console.WriteLine("\n========== 演示 2：主动搭话 ==========");
var turn2 = await agent.ProactiveAsync();
PrintTurn(turn2);

// ── 演示 3：调度器心跳（跑 3 秒即退出，便于演示）──
Console.WriteLine("\n========== 演示 3：调度器心跳（3 秒） ==========");
var scheduler = new ProactiveScheduler(intervalSeconds: 1, cooldownSeconds: 0, minIdleSeconds: 0);
using var cts = new CancellationTokenSource();
var loop = scheduler.RunLoopAsync(async ct =>
{
    Console.WriteLine("\n[心跳触发]");
    var t = await agent.ProactiveAsync(ct);
    PrintTurn(t);
}, cts.Token);

// 3 秒后停止演示
await Task.Delay(TimeSpan.FromSeconds(3));
cts.Cancel();
await Task.WhenAny(loop, Task.Delay(500));
Console.WriteLine("\n[done] Agent 骨架演示完成。");

static void PrintTurn(AgentTurnResult t)
{
    Console.WriteLine($"  Reason : {t.Reason}");
    Console.WriteLine($"  Observe: {t.Observation.Replace("\n", "\n           ")}");
    Console.WriteLine($"  胡桃   : {t.Reply}");
    Console.WriteLine($"  Act    : {t.Action}");
    if (t.Audio is not null)
        Console.WriteLine($"  音频   : {t.Audio.AudioPath}");
}

static string ResolvePersonaRoot(string[] args)
{
    if (args.Length > 0 && Directory.Exists(args[0]))
        return Path.GetFullPath(args[0]);

    // 从 Host 的 bin 目录回溯到仓库根的 data/persona
    foreach (var candidate in new[]
    {
        "data/persona",
        "../data/persona",
        "../../../../data/persona",
        "../../../../../data/persona",
        "../../../../../../data/persona",
    })
    {
        if (Directory.Exists(candidate))
            return Path.GetFullPath(candidate);
    }

    throw new DirectoryNotFoundException(
        "找不到人设目录，请显式传入：dotnet run -- <persona目录>");
}

static string? FindRepoRoot()
{
    // 从当前目录向上回溯，找包含 CORE.md 和 voice/ 的项目根
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "CORE.md")) &&
            Directory.Exists(Path.Combine(dir.FullName, "voice")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

static ILLMProvider BuildLlm(PersonaProfile persona)
{
    // 设了 DEEPSEEK_API_KEY 环境变量就接真实 DeepSeek，否则用 mock 先跑通流程
    var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
    if (!string.IsNullOrWhiteSpace(key))
    {
        Console.WriteLine("[llm] 已接入 DeepSeek");
        return new DeepSeekLlmProvider(key);
    }
    Console.WriteLine("[llm] 未检测到 DEEPSEEK_API_KEY，使用 Mock");
    return new MockLlmProvider(persona);
}

static void LoadEnvFile()
{
    // 从 bin 目录回溯找 hutao-companion/.env，简单解析 KEY=VALUE（支持 # 注释）
    foreach (var candidate in new[]
    {
        ".env",
        "../.env",
        "../../../../.env",
        "../../../../../.env",
        "../../../../../../.env",
    })
    {
        if (!File.Exists(candidate))
            continue;

        foreach (var line in File.ReadAllLines(candidate))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#'))
                continue;
            var eq = t.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = t[..eq].Trim();
            var value = t[(eq + 1)..].Trim();
            if (Environment.GetEnvironmentVariable(key) is null && value.Length > 0)
                Environment.SetEnvironmentVariable(key, value);
        }
        Console.WriteLine($"[env] 已加载 {Path.GetFullPath(candidate)}");
        return;
    }
}

static bool ReadBooleanEnvironment(string name)
{
    var value = Environment.GetEnvironmentVariable(name)?.Trim();
    return value is not null &&
           (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}

static ITtsEngine? BuildTts(string[] args)
{
    var repoRoot = FindRepoRoot();
    if (repoRoot is null)
    {
        Console.WriteLine("[warn] 找不到项目根，跳过 TTS");
        return null;
    }

    // python 路径优先级：命令行参数 > 环境变量 HU_TAO_TTS_PYTHON > 自动找 venv
    var python = args.Length > 1
        ? args[1]
        : Environment.GetEnvironmentVariable("HU_TAO_TTS_PYTHON")
          ?? Path.Combine(repoRoot, "voice", ".venv", "Scripts", "python.exe");

    if (!File.Exists(python))
    {
        Console.WriteLine($"[warn] 找不到 venv python，跳过 TTS: {python}");
        return null;
    }

    Console.WriteLine("[tts] 已接入 GPT-SoVITS 引擎");
    return new GptSovitsTtsEngine(
        python,
        inferScript: Path.Combine(repoRoot, "voice", "infer", "few_shot_infer.py"),
        gptModel: Path.Combine(repoRoot, "voice", "GPT-SoVITS-main", "GPT_SoVITS", "pretrained_models", "s1v3.ckpt"),
        sovitsModel: Path.Combine(repoRoot, "voice", "GPT-SoVITS-main", "GPT_SoVITS", "pretrained_models", "s2Gv3.pth"),
        outputDir: Path.Combine(repoRoot, "data", "voice", "hutao"));
}
