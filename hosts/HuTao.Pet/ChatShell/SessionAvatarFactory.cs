using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HuTao.Dialogue.Sessions;
using HuTao.Persona;

namespace HuTao.Pet.ChatShell;

/// <summary>
/// 会话头像与未读角标的构造。抽出来是因为它们都要按"会话类型"分派，
/// 写在 XAML 里会变成一堆 DataTrigger，写在行模板事件里又会和虚拟化打架
/// （容器复用时触发器状态容易残留）。
/// </summary>
public static class SessionAvatarFactory
{
    /// <summary>单聊 = 角色 Emoji；群聊 = 成员的 2×2 九宫格（2 人时 1×2 并排）。</summary>
    public static UIElement ForSession(SessionDescriptor session, double size)
        => session.Kind == SessionKind.Single || session.ActivePresences.Count <= 1
            ? ForCharacter(ResolveFirst(session, session.Emoji), size)
            : ForGroup(session, size);

    public static UIElement ForCharacter(CharacterDefinition? definition, double size)
    {
        var emoji = definition?.Emoji ?? "💬";
        return Circle(size, accent: null, content: new TextBlock
        {
            Text = emoji,
            FontSize = size * 0.52,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    /// <summary>按 accent 字符串直接画（会话描述符里存的就是它，不必回查角色表）。</summary>
    public static UIElement ForSessionColor(string emoji, string? accentArgb, double size)
        => Circle(size, accentArgb, new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(emoji) ? "💬" : emoji,
            FontSize = size * 0.52,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

    /// <summary>
    /// 群聊九宫格。最多取 4 个成员：再多只会让每个格子小到看不清，
    /// 而侧栏头像是用来"一眼认出是哪个群"的，不是成员名册。
    /// </summary>
    public static UIElement ForGroup(SessionDescriptor session, double size)
    {
        var members = session.ActivePresences
            .Select(presence => CharacterCatalog.Find(presence.CharacterId))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .Take(4)
            .ToArray();

        if (members.Length == 0)
            return ForSessionColor("💬", session.AccentArgb, size);

        var grid = new UniformGrid
        {
            Rows = members.Length <= 2 ? 1 : 2,
            Columns = members.Length <= 2 ? members.Length : 2,
            Width = size,
            Height = size,
        };
        foreach (var member in members)
        {
            grid.Children.Add(new TextBlock
            {
                Text = member.Emoji,
                FontSize = size * (members.Length <= 2 ? 0.36 : 0.26),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var host = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
            Child = grid,
        };
        host.ToolTip = string.Join('、', session.ActivePresences
            .Select(presence => CharacterCatalog.Find(presence.CharacterId)?.Name ?? presence.CharacterId));
        return host;
    }

    /// <summary>
    /// 未读角标。<paramref name="muted"/> 时退化成一个小圆点——免打扰的会话
    /// 不该继续用红色数字催你（那是"免打扰"这个词的意思）。
    ///
    /// 颜色**故意写在这里而不是查资源字典**：工厂要能在没有
    /// <c>Application.Current</c> 的离线检查里跑（UI 冒烟测试就是这么跑的）。
    /// </summary>
    public static UIElement UnreadBadge(int count, bool muted)
    {
        if (count <= 0)
            return new Border { Visibility = Visibility.Collapsed };

        if (muted)
            return new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };

        var text = count >= 100 ? "99+" : count.ToString();
        return new Border
        {
            MinWidth = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(5, 0, 5, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static CharacterDefinition? ResolveFirst(SessionDescriptor session, string fallbackEmoji)
    {
        _ = fallbackEmoji;
        var first = session.ActivePresences.FirstOrDefault();
        return first is null ? null : CharacterCatalog.Find(first.CharacterId);
    }

    private static Border Circle(double size, string? accent, UIElement content)
    {
        var color = ArgbToBrushConverter.Parse(accent);
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = accent is null
                ? new SolidColorBrush(Color.FromArgb(20, 0, 0, 0))
                : new SolidColorBrush(Color.FromArgb(36, color.R, color.G, color.B)),
            Child = content,
        };
    }
}
