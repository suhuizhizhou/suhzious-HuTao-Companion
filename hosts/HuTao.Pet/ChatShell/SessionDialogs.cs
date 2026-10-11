using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HuTao.Dialogue.Sessions;
using HuTao.Persona;

namespace HuTao.Pet;

/// <summary>
/// 发起群聊。校验规则全部来自 <see cref="SessionFactory"/>，这里只负责"把不合法的状态
/// 提前呈现给用户"（确认按钮置灰 + 一行原因说明），而不是让用户点了才报错。
/// </summary>
public sealed class NewGroupDialog : Window
{
    private readonly Dictionary<string, Button> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _titleBox;
    private readonly TextBlock _reason;
    private readonly Button _confirm;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

    public NewGroupDialog(IReadOnlyList<CharacterDefinition> characters)
    {
        Title = "发起群聊";
        Width = 420; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));

        _titleBox = new TextBox
        {
            FontSize = 14,
            Padding = new Thickness(8, 6, 8, 6),
            MaxLength = SessionFactory.MaxTitleLength,
            ToolTip = "给这个群起个名字",
        };
        _titleBox.TextChanged += (_, _) => Validate();

        _reason = new TextBlock
        {
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var list = new StackPanel();
        foreach (var character in characters)
        {
            var card = BuildCard(character);
            _cards[character.Id] = card;
            list.Children.Add(card);
        }

        _confirm = new Button
        {
            Content = "创建", Width = 88, Height = 32, IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x3C)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, IsEnabled = false,
        };
        _confirm.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button
        {
            Content = "取消", Width = 88, Height = 32, IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { _confirm, cancel },
        };

        Content = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        ((DockPanel)Content).Children.Add(buttons);
        ((DockPanel)Content).Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "群名称", FontSize = 12, Margin = new Thickness(0, 0, 0, 6) },
                    _titleBox,
                    new TextBlock
                    {
                        Text = $"选择成员（{SessionFactory.MinGroupMembers}–{SessionFactory.MaxGroupMembers} 位）",
                        FontSize = 12, Margin = new Thickness(0, 14, 0, 6),
                    },
                    list,
                    _reason,
                },
            },
        });

        // 默认勾上注册表里声明的默认阵容——但**不预填群名**：
        // 名字是用户的事，替他起一个反而要他先删掉。
        foreach (var id in CharacterCatalog.DefaultCastIds)
            if (_cards.ContainsKey(id))
                Toggle(id);
        Validate();
        Loaded += (_, _) => _titleBox.Focus();
    }

    public IReadOnlyList<string> SelectedIds => _selected.ToArray();

    public string SessionTitle => _titleBox.Text.Trim();

    private Button BuildCard(CharacterDefinition character)
    {
        var accent = CharacterThemeCatalog.Get(character.Id).Accent;
        var avatar = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Color.FromArgb(35, accent.R, accent.G, accent.B)),
            Child = new TextBlock
            {
                Text = character.Emoji, FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var labels = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = character.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        labels.Children.Add(new TextBlock
        {
            Text = character.Title, FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { avatar, labels } };

        var card = new Button
        {
            Tag = character.Id,
            Content = row,
            Style = (Style)FindResource("ChatShellCardButtonStyle"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        card.Click += (_, _) => { Toggle(character.Id); Validate(); };
        return card;
    }

    private void Toggle(string id)
    {
        if (!_selected.Remove(id))
            _selected.Add(id);
        if (_cards.TryGetValue(id, out var card))
        {
            var accent = CharacterThemeCatalog.Get(id).Accent;
            var on = _selected.Contains(id);
            card.BorderBrush = new SolidColorBrush(on ? accent : Color.FromArgb(34, 0, 0, 0));
            card.BorderThickness = new Thickness(on ? 1.8 : 1.2);
            card.Background = new SolidColorBrush(on
                ? Color.FromArgb(28, accent.R, accent.G, accent.B)
                : Color.FromArgb(8, 255, 255, 255));
        }
    }

    private void Validate()
    {
        var count = _selected.Count;
        var hasTitle = !string.IsNullOrWhiteSpace(_titleBox.Text);
        _confirm.IsEnabled = count is >= SessionFactory.MinGroupMembers and <= SessionFactory.MaxGroupMembers && hasTitle;
        _reason.Text = count switch
        {
            < SessionFactory.MinGroupMembers => $"还需再选 {SessionFactory.MinGroupMembers - count} 位成员。",
            > SessionFactory.MaxGroupMembers => $"最多 {SessionFactory.MaxGroupMembers} 位成员。",
            _ when !hasTitle => "给这个群起个名字。",
            _ => $"将创建 {count} 人群聊。",
        };
    }
}

/// <summary>
/// 管理群成员。移除一个成员时**不删他的在场记录**，而是写 <c>LeftAt</c>——
/// 否则视角投影算不出"他什么时候不在场"，被移除的人会继续看到后来发生的事。
/// </summary>
public sealed class GroupMembersDialog : Window
{
    private readonly Dictionary<string, Button> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

    public GroupMembersDialog(SessionDescriptor descriptor)
    {
        Title = "管理成员";
        Width = 400; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));

        foreach (var presence in descriptor.Presences.Where(p => p.LeftAt is null))
            _selected.Add(presence.CharacterId);

        var list = new StackPanel();
        foreach (var character in CharacterCatalog.All)
        {
            var card = BuildCard(character);
            _cards[character.Id] = card;
            list.Children.Add(card);
        }

        var confirm = new Button
        {
            Content = "保存", Width = 88, Height = 32, IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x3C)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        confirm.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button
        {
            Content = "取消", Width = 88, Height = 32, IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { confirm, cancel },
        };

        var panel = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = $"群「{descriptor.Title}」的成员（{SessionFactory.MinGroupMembers}–{SessionFactory.MaxGroupMembers} 位）。" +
                               "被移出的成员不会再看到之后的消息。",
                        FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                        Margin = new Thickness(0, 0, 0, 10),
                    },
                    list,
                },
            },
        });
        Content = panel;
    }

    public IReadOnlyList<string> SelectedIds => _selected.ToArray();

    private Button BuildCard(CharacterDefinition character)
    {
        var accent = CharacterThemeCatalog.Get(character.Id).Accent;
        var card = new Button
        {
            Tag = character.Id,
            Content = new TextBlock
            {
                Text = $"{character.Emoji}  {character.Name}",
                FontSize = 13.5,
            },
            Style = (Style)FindResource("ChatShellCardButtonStyle"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        card.Click += (_, _) =>
        {
            if (!_selected.Remove(character.Id))
                _selected.Add(character.Id);
            var on = _selected.Contains(character.Id);
            card.BorderBrush = new SolidColorBrush(on ? accent : Color.FromArgb(34, 0, 0, 0));
            card.BorderThickness = new Thickness(on ? 1.8 : 1.2);
            card.Background = new SolidColorBrush(on
                ? Color.FromArgb(28, accent.R, accent.G, accent.B)
                : Color.FromArgb(8, 255, 255, 255));
        };
        // 初始化外观（含一开始就被勾上的成员）。
        var initial = _selected.Contains(character.Id);
        card.BorderBrush = new SolidColorBrush(initial ? accent : Color.FromArgb(34, 0, 0, 0));
        card.BorderThickness = new Thickness(initial ? 1.8 : 1.2);
        return card;
    }
}
