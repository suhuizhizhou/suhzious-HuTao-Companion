namespace HuTao.Agent.Core.Configuration;

/// <summary>
/// 角色的可运行配置。路径统一使用相对于仓库根目录的形式，
/// UI 主题等展示细节留在宿主层，避免 Core 依赖 WPF。
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
    string BusyText);
