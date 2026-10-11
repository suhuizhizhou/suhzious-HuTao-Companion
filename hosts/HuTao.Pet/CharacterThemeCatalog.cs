using System.Windows.Media;

namespace HuTao.Pet;

/// <summary>WPF 专属展示主题。角色业务配置位于 Core.CharacterCatalog。</summary>
internal static class CharacterThemeCatalog
{
    public static CharacterTheme Get(string characterId)
        => characterId.ToLowerInvariant() switch
        {            "hutao" => new CharacterTheme(
                Color.FromArgb(248, 255, 255, 255),
                Color.FromRgb(224, 138, 60), Color.FromRgb(122, 63, 22),
                Color.FromArgb(64, 176, 106, 48), Color.FromArgb(224, 176, 106, 48),
                Color.FromRgb(70, 45, 20), Colors.White, Color.FromRgb(240, 224, 200),
                Color.FromRgb(240, 238, 234), Color.FromRgb(158, 138, 118)),
            "furina" => new CharacterTheme(
                Color.FromArgb(248, 247, 251, 255),
                Color.FromRgb(75, 130, 190), Color.FromRgb(31, 73, 116),
                Color.FromArgb(64, 79, 134, 198), Color.FromArgb(224, 79, 134, 198),
                Color.FromRgb(37, 63, 91), Colors.White, Color.FromRgb(221, 234, 248),
                Color.FromRgb(238, 244, 251), Color.FromRgb(109, 135, 165)),
            "klee" => new CharacterTheme(
                Color.FromArgb(248, 255, 250, 244),
                Color.FromRgb(218, 77, 62), Color.FromRgb(133, 37, 31),
                Color.FromArgb(64, 192, 72, 55), Color.FromArgb(224, 192, 72, 55),
                Color.FromRgb(88, 46, 34), Colors.White, Color.FromRgb(255, 231, 177),
                Color.FromRgb(255, 245, 220), Color.FromRgb(156, 116, 84)),
            // 异环：安魂曲。取自她「安魂曲 / 剧场」的气质——深紫配金，
            // 与前面三位原神角色的暖色系拉开区分。
            "lacrimosa" => new CharacterTheme(
                Color.FromArgb(248, 250, 247, 255),
                Color.FromRgb(140, 96, 190), Color.FromRgb(74, 42, 110),
                Color.FromArgb(64, 140, 96, 190), Color.FromArgb(224, 140, 96, 190),
                Color.FromRgb(46, 32, 62), Colors.White, Color.FromRgb(232, 220, 246),
                Color.FromRgb(244, 240, 250), Color.FromRgb(140, 124, 168)),
            _ => throw new KeyNotFoundException($"未配置角色主题: {characterId}"),
        };

    /// <summary>
    /// 取角色主题；**未登记的角色返回中性灰而不是抛异常**。
    ///
    /// 渲染路径上不能抛：一条来自未知角色的消息（旧数据、手改的会话、竞态里还没登记的角色）
    /// 不该让整个消息区渲染失败。查询路径（<see cref="Get"/>）仍然抛——
    /// 那里的未知 id 是真错误，静默兜底会把配置漏写藏起来。
    /// </summary>
    public static CharacterTheme GetOrDefault(string? characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId))
            return Neutral;
        try
        {
            return Get(characterId);
        }
        catch (KeyNotFoundException)
        {
            return Neutral;
        }
    }

    /// <summary>未登记角色的兜底主题：中性灰，不抢任何角色的视觉身份。</summary>
    private static CharacterTheme Neutral { get; } = new(
        Color.FromArgb(248, 255, 255, 255),
        Color.FromRgb(0x8C, 0x8C, 0x8C), Color.FromRgb(0x44, 0x44, 0x44),
        Color.FromArgb(64, 0x8C, 0x8C, 0x8C), Color.FromArgb(224, 0x8C, 0x8C, 0x8C),
        Color.FromRgb(0x33, 0x33, 0x33), Colors.White, Color.FromRgb(0xEE, 0xEE, 0xEE),
        Color.FromRgb(0xF2, 0xF2, 0xF2), Color.FromRgb(0x9A, 0x9A, 0x9A));

    /// <summary>
    /// 会话 accent 的字符串形态（<c>#AARRGGBB</c>）。
    /// Core 的 <c>SessionDescriptor</c> 存的是字符串——Core 不许引用 WPF——
    /// 所以颜色在写进会话目录时要在这里转一次。
    /// </summary>
    public static string AccentFor(string characterId)
    {
        var accent = GetOrDefault(characterId).Accent;
        return $"#{accent.A:X2}{accent.R:X2}{accent.G:X2}{accent.B:X2}";
    }
}
