using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HuTao.Dialogue.Sessions;
using HuTao.Persona;

namespace HuTao.Pet.ChatShell;

/// <summary>
/// 一条记录 → 一个气泡。逻辑从旧聊天室窗口的 <c>AppendTurn</c> 迁过来并参数化。
///
/// 为什么要有这个工厂：QQNT 形态下同一份"消息长什么样"的规则要在
/// 消息区、搜索预览、以及将来的转发卡片里用；复制三份必然会漂移。
/// 它只造 UIElement，不做播放、不碰数据。
/// </summary>
public sealed class MessageVisualFactory
{
    private readonly Action<string> _onReplay;

    /// <param name="onReplay">点 🔊 时回调被播放的音频路径。播放本身由宿主负责（它才有串行门与取消）。</param>
    public MessageVisualFactory(Action<string> onReplay) => _onReplay = onReplay;

    /// <summary>
    /// 造一条消息的可视化。
    /// </summary>
    /// <param name="showHeader">
    /// 是否显示头像与昵称。群聊里同一人连续发言时，只有第一条显示——
    /// 这是 QQ 的既有做法，也避免了每行都占一个头像位。
    /// </param>
    /// <param name="isGroup">群聊里昵称必显（否则分不清谁在说话）；单聊里只有角色一侧显示。</param>
    /// <param name="theme">角色主题（气泡底色等）。为 null 时用中性灰。</param>
    public UIElement For(
        ConversationTurn turn,
        CharacterTheme? theme,
        bool showHeader,
        bool isGroup)
    {
        return turn.Kind switch
        {
            ConversationTurnKind.System => SystemLine(turn.Text),
            ConversationTurnKind.Hint => HintLine(turn.Text),
            _ => MessageBubble(turn, theme, showHeader, isGroup),
        };
    }

    /// <summary>系统提示：居中、灰、无气泡。它是"环境事实"，不是谁说的话。</summary>
    public UIElement SystemLine(string text) => Centered(text, italic: false);

    /// <summary>场景旁白：居中、灰、斜体。</summary>
    public UIElement HintLine(string text) => Centered(text, italic: true);

    /// <summary>日期分隔：居中胶囊。跨天时插一条。</summary>
    public UIElement DateSeparator(string label) => new Border
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 10, 0, 10),
        Padding = new Thickness(10, 3, 10, 3),
        CornerRadius = new CornerRadius(9),
        Background = new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xDA)),
        Child = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Brushes.White,
        },
    };

    private UIElement Centered(string text, bool italic) => new TextBlock
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
        FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(24, 4, 24, 8),
        MaxWidth = 520,
    };

    private UIElement MessageBubble(
        ConversationTurn turn, CharacterTheme? theme, bool showHeader, bool isGroup)
    {
        var isUser = turn.IsUser;
        var accent = theme?.Accent ?? Color.FromRgb(0xE0, 0x8A, 0x3C);
        var accentDark = theme?.AccentDark ?? Color.FromRgb(0x7A, 0x3F, 0x16);

        var bubble = new Border
        {
            Padding = new Thickness(11, 8, 11, 8),
            CornerRadius = new CornerRadius(10),
            MaxWidth = 460,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = isUser
                ? new SolidColorBrush(accent)
                : new SolidColorBrush(theme?.AssistantBubble ?? Colors.White),
            BorderThickness = isUser ? new Thickness(0) : new Thickness(1),
            BorderBrush = isUser
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = turn.Text,
                        FontSize = 13.5,
                        LineHeight = 20,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = isUser ? Brushes.White : new SolidColorBrush(theme?.Text ?? Colors.Black),
                    },
                },
            },
        };

        // 有语音才给按钮：没有声线的角色不该出现一个点了没反应的图标。
        // 这一条是从已退休的旧聊天室窗口继承下来的教训。
        if (!isUser && !string.IsNullOrWhiteSpace(turn.AudioPath))
        {
            var path = turn.AudioPath!;
            var play = new Button
            {
                Content = "🔊",
                Width = 24,
                Height = 20,
                FontSize = 11,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "重播这一句",
            };
            play.Click += (_, _) => _onReplay(path);
            ((StackPanel)bubble.Child).Children.Add(play);
        }

        if (isUser)
        {
            // 自己发的消息：QQ 里不重复显示自己的头像与昵称。
            return Wrap(bubble, isUser: true);
        }

        // 角色一侧：头像列 + 昵称 + 气泡。
        var column = new StackPanel { MaxWidth = 500 };
        if (showHeader || isGroup)
        {
            column.Children.Add(new TextBlock
            {
                Text = turn.SpeakerName,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accentDark),
                Margin = new Thickness(4, 0, 4, 4),
            });
        }
        column.Children.Add(bubble);

        var avatar = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(36, accent.R, accent.G, accent.B)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = CharacterCatalog.Find(turn.SpeakerId)?.Emoji ?? "🎭",
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        // 连续发言且不显示表头时，头像位留空但保留缩进，让气泡左边缘对齐。
        avatar.Visibility = showHeader ? Visibility.Visible : Visibility.Hidden;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(avatar, 0);
        Grid.SetColumn(column, 1);
        row.Children.Add(avatar);
        row.Children.Add(column);

        return Wrap(row, isUser: false, tightTop: !showHeader);
    }

    private static UIElement Wrap(UIElement content, bool isUser, bool tightTop = false)
        => new Grid
        {
            Margin = new Thickness(0, tightTop ? 2 : 6, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { content },
        };
}
