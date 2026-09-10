using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Configuration;
using HuTao.Agent.Core.Core;
using HuTao.Agent.Core.Runtime;

// ── 装配（依赖注入风格，全部走接口，方便后续切换）──
// 用法：dotnet run --project src/HuTao.Agent.Host [persona目录] [可选: tts-python路径]

ProjectEnvironment.LoadDotEnv(log: Console.WriteLine); // 先加载项目根目录的 .env，不覆盖已有环境变量

var personaRoot = ResolvePersonaRoot(args);
var repoRoot = ProjectEnvironment.FindRepositoryRoot(Environment.CurrentDirectory) ?? AppContext.BaseDirectory;
var personaId = Path.GetFileName(Path.GetFullPath(personaRoot)).ToLowerInvariant();
var definition = CharacterCatalog.Find(personaId)
                 ?? CreateCustomDefinition(personaRoot, repoRoot, personaId);
var runtimeFactory = new AgentRuntimeFactory(
    repoRoot,
    _ => TtsRuntimeFactory.CreateGptSovits(
        repoRoot,
        pythonOverride: args.Length > 1 ? args[1] : null,
        preferResident: false,
        log: Console.WriteLine),
    Console.WriteLine);
var runtime = runtimeFactory.Create(
    definition,
    allowAppAwareness: () => ReadBooleanEnvironment("HU_TAO_ALLOW_APP_AWARENESS"),
    documentProgress: message => Console.WriteLine(message));
var agent = runtime.Agent;
Console.WriteLine($"[persona] 已加载人设: {runtime.Persona.Name}");

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
    Console.WriteLine($"  Reply  : {t.Reply}");
    Console.WriteLine($"  Act    : {t.Action}");
    if (t.Audio is not null)
        Console.WriteLine($"  音频   : {t.Audio.AudioPath}");
}

static string ResolvePersonaRoot(string[] args)
{
    var repoRoot = ProjectEnvironment.FindRepositoryRoot(Environment.CurrentDirectory);
    if (args.Length > 0)
    {
        var candidates = new List<string> { args[0] };
        if (repoRoot is not null && !Path.IsPathRooted(args[0]))
            candidates.Add(Path.Combine(repoRoot, args[0]));
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
    }

    if (repoRoot is not null)
    {
        var defaultPersona = Path.Combine(repoRoot, "data", "persona", "hutao");
        if (Directory.Exists(defaultPersona))
            return defaultPersona;
    }

    // 从 Host 的 bin 目录回溯查找默认胡桃人设
    foreach (var candidate in new[]
    {
        "data/persona/hutao",
        "../data/persona/hutao",
        "../../../../data/persona/hutao",
        "../../../../../data/persona/hutao",
        "../../../../../../data/persona/hutao",
    })
    {
        if (Directory.Exists(candidate))
            return Path.GetFullPath(candidate);
    }

    throw new DirectoryNotFoundException(
        "找不到人设目录，请显式传入：dotnet run -- <persona目录>");
}

static CharacterDefinition CreateCustomDefinition(
    string personaRoot,
    string repoRoot,
    string personaId)
{
    var relativePersona = Path.GetRelativePath(repoRoot, personaRoot);
    return new CharacterDefinition(
        personaId,
        Path.GetFileName(personaRoot),
        "自定义角色",
        "🎭",
        relativePersona,
        "",
        "",
        Path.Combine(relativePersona, "emotion-references.json"),
        "",
        "",
        "正在准备下一句话，请稍候~");
}

static bool ReadBooleanEnvironment(string name)
{
    var value = Environment.GetEnvironmentVariable(name)?.Trim();
    return value is not null &&
           (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
