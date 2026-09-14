using HuTao.Foundation.Abstractions;
using HuTao.Persona;
using HuTao.Foundation;
using HuTao.Voice;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Runtime;

// ── 装配（依赖注入风格，全部走接口，方便后续切换）──
// 用法：dotnet run --project src/HuTao.Agent.Host [persona目录] [可选: tts-python路径]

ProjectEnvironment.LoadDotEnv(log: Console.WriteLine); // 先加载项目根目录的 .env，不覆盖已有环境变量

var personaRoot = ResolvePersonaRoot(args);
var repoRoot = ProjectEnvironment.FindRepositoryRoot(Environment.CurrentDirectory) ?? AppContext.BaseDirectory;
var personaId = Path.GetFileName(Path.GetFullPath(personaRoot)).ToLowerInvariant();
// 优先按「角色 id」解析：异环角色的 persona 目录名是通用的 persona，
// 从目录名推不出 id，只有显式传 id 才能选中。
var definition = CharacterCatalog.Find(args.Length > 0 ? args[0] : null)
                 ?? CharacterCatalog.Find(personaId)
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
    allowAppAwareness: () => ReadBooleanEnvironment("HU_TAO_ALLOW_APP_AWARENESS", defaultValue: true),
    documentProgress: message => Console.WriteLine(message));
var agent = runtime.Agent;
Console.WriteLine($"[persona] 已加载人设: {runtime.Persona.Name}");
// 可读后端日志（agent + RAG 的回合叙事）默认开着；把路径打出来，否则等于没有。
Console.WriteLine(HuTao.Foundation.Diagnostics.BackendTrace.Default.Enabled
    ? $"[trace] 可读后端日志: {HuTao.Foundation.Diagnostics.BackendTrace.Default.FilePath}"
    : "[trace] 已关闭（HU_TAO_TRACE=off）");

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

// ── 演示 4：长期记忆（检索 + 后台整合）──
Console.WriteLine("\n========== 演示 4：长期记忆 ==========");
if (runtime.MemoryStore is null || runtime.Memory is null)
{
    Console.WriteLine("  已通过 HU_TAO_MEMORY=off 关闭记忆层。");
}
else
{
    Console.WriteLine($"  记忆条数      : {runtime.MemoryStore.Count}");
    Console.WriteLine($"  待提炼轮次    : {runtime.MemoryStore.UnconsolidatedTurnCount}");
    // 演示里直接把空闲视为满足，真实宿主由桌宠的 15 秒轮询按真实空闲时间触发。
    var result = await runtime.Memory.RunIfDueAsync(
        TimeSpan.FromHours(1), DateTimeOffset.Now, CancellationToken.None);
    Console.WriteLine(result is null
        ? "  后台整合      : 未到触发条件（待提炼轮次不足或仍在冷却期）"
        : $"  后台整合      : 处理 {result.ProcessedTurns} 轮 → 新增 {result.Added}、更新 {result.Updated}、取代 {result.Superseded}（{result.Path}）");
    foreach (var record in runtime.MemoryStore.Snapshot()
                 .Where(r => r.Kind != HuTao.Knowledge.Memory.MemoryKind.Turn)
                 .TakeLast(5))
        Console.WriteLine($"    · [{record.Kind}] {record.Text}");
    runtime.MemoryStore.Flush();
}

// ── 演示 5：多人聊天室 ──
// 刻意跨作品组局（原神 × 异环）：聊天室不要求角色来自同一个作品，
// 只要各自装配出 IAgentConversation 就能同场对话。
// 异环角色的人设包与语音全部由 tools/nte 提取，与桌宠既有规范一致。
Console.WriteLine("\n========== 演示 5：聊天室（跨作品）==========");
var chatCharacters = new[]
    {
        CharacterCatalog.Find("hutao"),
        CharacterCatalog.Find("lacrimosa"),
        CharacterCatalog.Find("tajiduo"),
    }
    .Where(c => c is not null).Select(c => c!).ToArray();
if (chatCharacters.Length < 2)
    chatCharacters = CharacterCatalog.All.Take(2).ToArray();
if (chatCharacters.Length < 2)
{
    Console.WriteLine("  角色不足两位，跳过。");
}
else
{
    var participants = new List<HuTao.Dialogue.ChatRoom.ChatRoomParticipant>();
    foreach (var def in chatCharacters)
    {
        // memoryScope 与 WPF 聊天室窗口用同一个：命令行与窗口看到的是同一场对话历史。
        var rt = runtimeFactory.Create(def, allowAppAwareness: () => false, memoryScope: "chatroom");
        participants.Add(new HuTao.Dialogue.ChatRoom.ChatRoomParticipant(
            def.Id, def.Name, rt.Agent, rt.Persona));

        // 原声通道自检：这一行是回答「异环角色到底能不能用原声」的地方。
        // 安魂曲会显示 0 —— 不是配置漏了，是本机游戏容器里没有她的语音，
        // 详见 tools/nte/README.md §2.5。
        var voiceNote = rt.OriginalVoices.HasEntries
            ? $"原声 {rt.OriginalVoices.All.Count} 条"
            : "无原声（走 TTS 或纯文字）";
        Console.WriteLine($"  入场：{def.Name}（{voiceNote}）");
    }

    var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
    HuTao.Foundation.Abstractions.ILLMProvider directorLlm = string.IsNullOrWhiteSpace(apiKey)
        ? new HuTao.Dialogue.Llm.MockLlmProvider(participants[0].Persona)
        : new HuTao.Dialogue.Llm.DeepSeekLlmProvider(apiKey);
    var room = new HuTao.Dialogue.ChatRoom.ChatRoom(
        participants,
        new HuTao.Dialogue.ChatRoom.LlmChatRoomDirector(directorLlm),
        new HuTao.Dialogue.ChatRoom.ChatRoomOptions { MinTurns = 2, MaxTurns = 4 });

    // 用户默认旁观，可随时插话。
    const string interjection = "你们几个第一次见面，互相打个招呼吧。";
    room.Interject(interjection);
    Console.WriteLine($"  [旁观用户] {interjection}");
    await room.RunAsync(t =>
    {
        var voice = t.AudioPath is null ? "（无语音）" : $"🔊 {Path.GetFileName(t.AudioPath)}";
        Console.WriteLine($"  {t.SpeakerName}：{t.Text.Replace("\n", " / ")}  {voice}");
        if (room.LastDecision is { } d)
            Console.WriteLine($"      导演 → {d.NextSpeakerId}：{d.Reason}");
    }, CancellationToken.None);
    Console.WriteLine($"  共 {room.TurnCount} 轮。");
}

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

static bool ReadBooleanEnvironment(string name, bool defaultValue = false)
{
    var value = Environment.GetEnvironmentVariable(name)?.Trim();
    if (string.IsNullOrEmpty(value))
        return defaultValue;
    if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("no", StringComparison.OrdinalIgnoreCase))
        return false;
    return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
