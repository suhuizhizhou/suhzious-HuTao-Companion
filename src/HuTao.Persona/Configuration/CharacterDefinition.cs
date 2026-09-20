namespace HuTao.Persona;

/// <summary>
/// 角色的可运行配置。路径统一使用相对于仓库根目录的形式，
/// UI 主题等展示细节留在宿主层，避免 Core 依赖 WPF。
///
/// <paramref name="StoryCorpusDirectory"/> 为 null 表示该角色不挂剧情档案工具；
/// 非 null 时指向语料根目录（其下需有 dialogue/chapters，可选 index.json）。
/// 「胡桃用原神语料、安魂曲用异环语料」因此只是两条配置，检索链路完全共用。
///
/// <paramref name="VoiceDirectory"/> 同理：原声清单默认在 <c>data/voice/&lt;id&gt;/</c>，
/// 异环角色由 tools/nte 提取到 <c>data/nte/&lt;id&gt;/voice/</c>，用这条配置指过去即可，
/// 「原声优先」的检索与播放逻辑一行都不用改。
///
/// 这个 record 就是**角色包清单**在 C# 侧的形态：它只描述「这个角色需要什么」，
/// 引擎据此装配。所有角色特例都应该落成这里的可空字段，而不是散落到工厂里的
/// <c>Id == "xxx"</c> 判断——后者每加一个角色都要回来改代码。
/// </summary>
public sealed record CharacterDefinition(
    string Id,
    string Name,
    string Title,
    string Emoji,
    string PersonaDirectory,
    string ReferenceAudio,
    string ReferenceText,
    string EmotionCatalog,
    string Greeting,
    string GreetingAudio,
    string BusyText,
    string? StoryCorpusDirectory = null,
    string? VoiceDirectory = null,
    /// <summary>
    /// 「重要事项」存档的相对路径；null 表示这个角色不挂这个能力。
    /// 原先是 <c>Id == "hutao"</c> 的硬编码判断。
    /// </summary>
    string? ImportantMemoryFile = null,
    /// <summary>
    /// 这个角色要装配哪些工具（按名字）。**null = 用引擎默认集**；
    /// 传空数组 = 明确不要任何工具。名字登记在 <c>AgentToolRegistry</c> 里。
    /// </summary>
    IReadOnlyList<string>? Tools = null,
    /// <summary>
    /// 这个角色额外要挂的 CLI 工具（白名单）。默认空 = 一个都不挂。
    /// 外部程序可能改文件/发请求，所以必须由角色包显式声明，不能由模型自行选择。
    /// </summary>
    IReadOnlyList<CliToolDefinition>? CliTools = null,
    /// <summary>
    /// **全局默认角色**。全项目只允许有一个为 true（由结构检查钉住）。
    ///
    /// 它的含义是：任何「不知道该用哪个角色」的地方（默认人设目录、缺省主题、
    /// 找不到角色时的兜底文案）都退到它，而不是各自在代码里写死一个名字。
    /// 目前是胡桃——这是产品定位，不是技术债；但必须是**显式声明**，
    /// 不能靠 `switch` 的 `_ =>` 分支隐式承担（那样每加一个角色都会静默继承胡桃的口吻）。
    /// </summary>
    bool IsGlobalDefault = false);

/// <summary>
/// 角色包里的一个 CLI 工具声明。
/// <paramref name="Executable"/> 相对仓库根或绝对路径，**必须已存在**，否则跳过。
/// </summary>
/// <param name="Arguments">审过的固定版本查询参数；不允许追加用户参数或任意子命令。</param>
public sealed record CliToolDefinition(
    string Name,
    string Description,
    string Executable,
    IReadOnlyList<string>? Arguments = null);
