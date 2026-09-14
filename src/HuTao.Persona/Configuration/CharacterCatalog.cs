namespace HuTao.Persona;

/// <summary>
/// 内置角色目录。新增角色时只需增加一条定义并准备对应 persona/voice 资源，
/// Agent、Host 和 WPF 会复用同一份元数据。
/// </summary>
public static class CharacterCatalog
{
    private static readonly IReadOnlyList<CharacterDefinition> Definitions =
    [
        new(
            "hutao", "胡桃", "往生堂堂主", "🍑",
            "data/persona/hutao",
            "data/voice/hutao/wav/a4eedbf833d51d47.wav",
            "哼哼，切勿质疑我的业务能力！",
            "data/persona/hutao/emotion-references.json",
            "突然来找我，怎么怎么？想我了吗？",
            "data/voice/hutao/wav/7dda0f32efe522a2.wav",
            "胡桃正在忙，可能还没有看到消息哦~",
            "data/story",
            // 参考音频在原声目录里（data/voice/hutao/）
            VoiceDirectory: null,
            // 「记住…/提醒我…」彩蛋。原来是 Id=="hutao" 硬编码，现在由角色包声明。
            ImportantMemoryFile: "data/important_memories_hutao.json"),
        new(
            "furina", "芙宁娜", "枫丹水神", "💧",
            "data/persona/furina",
            "data/voice/furina/wav/4e4c5b22eb6c9354.wav",
            "不要把舞台演出和剧团里的关系混为一谈行吗？",
            "data/persona/furina/emotion-references.json",
            "哟，你们来啦？派对已经开始了。",
            "data/voice/furina/wav/b25b2cc32be97733.wav",
            "芙宁娜正在准备下一幕，请稍候片刻~"),
        new(
            "klee", "可莉", "火花骑士", "💣",
            "data/persona/klee",
            "data/voice/klee/wav/61335463fedb347a.wav",
            "这是可莉创办的小魔女会哦。我是小魔女可莉。",
            "data/persona/klee/emotion-references.json",
            "太好了！可莉又见到你了。",
            "data/voice/klee/wav/793e68bd5fa341c4.wav",
            "可莉正在想新的冒险计划，请等一下下哦~"),
        // 异环角色。人设包、语音与情感参考全部由 tools/nte 从游戏资源提取，
        // 路径与桌宠既有规范一致——这就是「一脉相承」的收口：跨作品角色只是多一条配置。
        // 头衔「异象管理局」取自她的游戏内台词（在发《异象管理局安全手册》），非杜撰。
        //
        // ⚠️ 她的语音在**语言分包**里（HT/Content/TagPatchPaks），不在主 Paks。
        //    tools/nte 的 game.json 必须把 TagPatchPaks 挂上，否则会报「命中语音事件 0 个」。
        //    见 tools/nte/README.md 的「语音链路」一节。
        new(
            "lacrimosa", "安魂曲", "异象管理局", "🎭",
            "data/nte/lacrimosa/persona",
            // 参考音频 5.54 秒（GPT-SoVITS 硬要求 3~10 秒），取自她的一句平稳台词
            "data/nte/lacrimosa/voice/wav/115136269.wav",
            "鉴定师，开车；安魂曲，坐车。",
            "data/nte/lacrimosa/persona/emotion-references.json",
            "安魂曲……想要番茄酱，可以吗？",
            // 问候语直接放她的原声，不走 TTS
            "data/nte/lacrimosa/voice/wav/965806491.wav",
            "安魂曲正在打盹，等一下下哦~",
            "data/nte/_corpus",
            "data/nte/lacrimosa/voice"),
    ];

    public static IReadOnlyList<CharacterDefinition> All => Definitions;

    public static CharacterDefinition? Find(string? id)
        => Definitions.FirstOrDefault(character =>
            string.Equals(character.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static CharacterDefinition Get(string id)
        => Find(id) ?? throw new KeyNotFoundException($"未注册角色: {id}");
}
