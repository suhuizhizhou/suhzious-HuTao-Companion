using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HuTao.Dialogue.Runtime;
using HuTao.Dialogue.Sessions;
using HuTao.Dialogue.Storage;
using HuTao.Foundation;
using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;
using HuTao.Persona;
using HuTao.Pet.ChatShell;
using HuTao.Voice;

namespace HuTao.Pet;


public partial class ChatShellWindow
{

    private void RenderSessions()
    {
        if (_viewModel is null)
            return;
        foreach (var item in _viewModel.Sessions)
            RenderSessionRow(item);
        // 虚拟化意味着**屏幕外的行还没有容器**，上面那次循环对它们无效。
        // 真正的兜底是 ItemContainerGenerator 的 StatusChanged：容器一被实体化就补画。
        // 少了这一步，只有当前可见的那几行有头像，滚动下去会看到空白圆圈。
        RenderRealizedRows();
        SidebarEmpty.Visibility = _viewModel.Sessions.Count == 0 && !string.IsNullOrWhiteSpace(_viewModel.SearchText)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RenderRealizedRows()
    {
        if (_viewModel is null || SessionList.Items.Count == 0)
            return;
        for (var i = 0; i < _viewModel.Sessions.Count; i++)
            RenderSessionRow(_viewModel.Sessions[i]);
    }

    private void SessionList_StatusChanged(object? sender, EventArgs e)
    {
        // 容器生成/回收都会走到这里；按需补画，代价是每次 O(可见行数)。
        if (e is not null)
            RenderRealizedRows();
    }

    /// <summary>
    /// 把头像、角标这些"要看会话类型决定的图"塞进行模板里的宿主。
    /// 为什么不写在 XAML 的 DataTemplate 里：容器会被虚拟化回收，
    /// 用触发器拼多角色九宫格很容易在复用后残留上一行的状态。
    /// </summary>
    private void RenderSessionRow(SessionItemViewModel item)
    {
        if (SessionList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
            return;
        container.ApplyTemplate();
        // DataTemplate 的命名作用域属于 ContentPresenter，不属于 ListBoxItem。
        // 从已实现的可视树定位，避免 FindName 抛错后被吞掉导致整列头像永远空白。
        T? Find<T>(string name) where T : class
        {
            return FindVisual(container, name) as T;
        }

        if (Find<ContentControl>("AvatarHost") is { } avatarHost)
            avatarHost.Content = SessionAvatarFactory.ForSession(item.Descriptor, 40);
        if (Find<ContentControl>("BadgeHost") is { } badgeHost)
            badgeHost.Content = SessionAvatarFactory.UnreadBadge(item.UnreadCount, item.Muted);
        if (Find<TextBlock>("SessionTitle") is { } title)
            title.Foreground = item.UnreadCount > 0
                ? (Brush)FindResource("ChatShell.TextPrimary")
                : (Brush)FindResource("ChatShell.TextSecondary");
        if (Find<TextBlock>("SessionPreview") is { } preview)
            preview.Foreground = item.Muted
                ? (Brush)FindResource("ChatShell.TextFaint")
                : (Brush)FindResource("ChatShell.TextMuted");

        // 辅助功能名称必须显式给：WPF 默认拿 DataContext 的 ToString()，
        // 于是读屏软件与 UI 自动化看到的是 "HuTao.Pet.ChatShell.SessionItemViewModel"。
        // 这是真实可访问性缺陷（也是本项目的 UI 自动化检查最先撞到的东西）。
        var accessible = item.UnreadCount > 0
            ? $"{item.Title}（{item.UnreadCount} 条未读）"
            : item.Title;
        System.Windows.Automation.AutomationProperties.SetName(container, accessible);
        System.Windows.Automation.AutomationProperties.SetHelpText(container, item.Preview);

        // 选中态的 accent 竖条取该会话的颜色。
        container.Tag = ArgbToBrushConverter.Brush(item.AccentArgb);
    }

    internal static FrameworkElement? FindVisual(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindVisual(child, name) is { } found) return found;
        }
        return null;
    }

    private void SessionAvatar_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionItemViewModel item })
            RenderSessionRow(item);
    }

    // ── 消息渲染 ─────────────────────────────────────────────────────────

    private void RebuildMessages()
    {
        MessagePanel.Children.Clear();
        if (_viewModel is null)
            return;
        if (_viewModel.ActiveTurns.Count == 0)
        {
            MessagePanel.Children.Add(EmptyHint());
            return;
        }

        var isGroup = _viewModel.ActiveDescriptor?.Kind == SessionKind.Group;
        string? previousSpeaker = null;
        DateOnly? previousDate = null;

        foreach (var turn in _viewModel.ActiveTurns)
        {
            var date = DateOnly.FromDateTime(turn.At.LocalDateTime);
            if (previousDate != date)
            {
                MessagePanel.Children.Add(_messageFactory.DateSeparator(DateLabel(turn.At)));
                previousDate = date;
                previousSpeaker = null;
            }

            // 群聊里同一人连续发言只在第一条显示头像与昵称。
            var showHeader = !turn.IsUser &&
                             (isGroup ? previousSpeaker != turn.SpeakerId : true);
            MessagePanel.Children.Add(_messageFactory.For(
                turn, CharacterThemeCatalog.GetOrDefault(turn.SpeakerId), showHeader, isGroup));
            previousSpeaker = turn.IsUser ? null : turn.SpeakerId;
        }
        MessageScroll.ScrollToEnd();
        // 调度面板若开着，跟着刷新——否则它会停在打开那一刻的快照上。
        if (DebugBorder.Visibility == Visibility.Visible)
            DebugText.Text = _viewModel.DirectorTrace ?? "";
    }

    private static string DateLabel(DateTimeOffset at)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var date = DateOnly.FromDateTime(at.LocalDateTime);
        if (date == today) return "今天";
        if (date == today.AddDays(-1)) return "昨天";
        return at.Year == DateTime.Now.Year ? at.ToString("M月d日") : at.ToString("yyyy年M月d日");
    }

    private static UIElement EmptyHint() => new TextBlock
    {
        Text = "还没有消息，说点什么吧。",
        FontSize = 12.5,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 24, 0, 0),
    };

    // ── 视图模型事件 ─────────────────────────────────────────────────────

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
            return;
        switch (e.PropertyName)
        {
            case nameof(ChatShellViewModel.ActiveSessionId):
                RebuildMessages();
                RenderHeader();
                LoadContextForActiveSession();
                break;
            case nameof(ChatShellViewModel.StatusMessage):
                SetStatus(_viewModel.StatusMessage);
                break;
            case nameof(ChatShellViewModel.ActiveTurns):
                break;
            case nameof(ChatShellViewModel.IsAwaitingReply):
                // 等角色开口期间显示占位，收到回复（或失败/取消）后收掉。
                AwaitingBubble.Visibility = _viewModel.IsAwaitingReply
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (_viewModel.IsAwaitingReply)
                    MessageScroll.ScrollToEnd();
                break;
            case nameof(ChatShellViewModel.IsBusy):
                UpdateComposerState();
                break;
            case nameof(ChatShellViewModel.IsAutoTalking):
                AutoTalkButton.Content = _viewModel.IsAutoTalking ? "⏸" : "▶";
                break;
            case nameof(ChatShellViewModel.TotalSessions):
                RenderSessions();
                break;
        }
    }

    private void RenderHeader()
    {
        var descriptor = _viewModel?.ActiveDescriptor;
        if (descriptor is null)
        {
            TitleText.Text = "聊天";
            SubtitleText.Text = "选择左侧会话说点什么";
            MemberSummary.Text = "";
            MemberStrip.Children.Clear();
            AutoTalkButton.Visibility = Visibility.Collapsed;
            StepButton.Visibility = Visibility.Collapsed;
            DebugButton.Visibility = Visibility.Collapsed;
            return;
        }

        TitleText.Text = descriptor.Title;
        Title = descriptor.Title + " · 聊天";
        var names = descriptor.ActivePresences
            .Select(p => CharacterCatalog.Find(p.CharacterId)?.Name ?? p.CharacterId)
            .ToArray();
        SubtitleText.Text = descriptor.Kind == SessionKind.Group
            ? $"群聊 · {names.Length} 位成员"
            : "单聊";
        MemberSummary.Text = names.Length == 0 ? "" : $"在场：{string.Join('、', names)}";

        MemberStrip.Children.Clear();
        foreach (var presence in descriptor.ActivePresences.Take(6))
        {
            var definition = CharacterCatalog.Find(presence.CharacterId);
            var chip = SessionAvatarFactory.ForCharacter(definition, 26);
            chip.SetValue(MarginProperty, new Thickness(4, 0, 0, 0));
            MemberStrip.Children.Add(chip);
        }

        var isGroup = descriptor.Kind == SessionKind.Group;
        AutoTalkButton.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        StepButton.Visibility = Visibility.Visible;
        DebugButton.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// 输入框上的占位文字按会话类型换。用会话的 accent 描边，
    /// 这样一眼看出"现在在跟谁说话"。
    /// </summary>
    private void LoadContextForActiveSession()
    {
        var descriptor = _viewModel?.ActiveDescriptor;
        _contextSessionId = descriptor?.Id;
        InputBox.Text = _contextSessionId is { } id && _drafts.TryGetValue(id, out var draft) ? draft : "";
        InputBox.CaretIndex = InputBox.Text.Length;
        if (descriptor is null)
        {
            InputBox.IsEnabled = false;
            SendButton.IsEnabled = false;
            return;
        }
        InputBox.IsEnabled = true;
        UpdateComposerState();
        InputBox.ToolTip = descriptor.Kind == SessionKind.Group
            ? $"和 {descriptor.Title} 的成员说点什么…\nEnter 发送，Shift+Enter 换行"
            : $"和 {descriptor.Title} 说点什么…\nEnter 发送，Shift+Enter 换行";
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_contextSessionId is { } id)
        {
            if (InputBox.Text.Length == 0) _drafts.Remove(id);
            else _drafts[id] = InputBox.Text;
        }
        UpdateComposerState();
    }

    private void UpdateComposerState()
    {
        // XAML 初始化期间 TextChanged 可能先于其他控件创建。
        if (SendButton is null || StepButton is null || InputBox is null) return;
        var ready = _viewModel?.CanSend == true && !_isSwitching && !_cts.IsCancellationRequested;
        SendButton.IsEnabled = ready && !string.IsNullOrWhiteSpace(InputBox.Text);
        StepButton.IsEnabled = ready;
    }

}
