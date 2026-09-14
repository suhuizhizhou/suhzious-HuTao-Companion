using System.Windows.Media;

namespace HuTao.Pet;

/// <summary>WPF 专属展示主题。角色业务配置位于 Core.CharacterCatalog。</summary>
internal static class CharacterThemeCatalog
{
    public static CharacterTheme Get(string characterId)
        => characterId.ToLowerInvariant() switch
        {
            "hutao" => new CharacterTheme(
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
            // 异环：塔吉多。标志性爱好是番茄酱，所以直接给番茄红。
            "tajiduo" => new CharacterTheme(
                Color.FromArgb(248, 255, 250, 247),
                Color.FromRgb(206, 62, 48), Color.FromRgb(120, 30, 22),
                Color.FromArgb(64, 206, 62, 48), Color.FromArgb(224, 206, 62, 48),
                Color.FromRgb(72, 32, 26), Colors.White, Color.FromRgb(255, 227, 214),
                Color.FromRgb(252, 243, 238), Color.FromRgb(168, 122, 108)),
            _ => throw new KeyNotFoundException($"未配置角色主题: {characterId}"),
        };
}
