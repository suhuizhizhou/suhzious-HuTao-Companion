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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private async void Close_Click(object sender, RoutedEventArgs e) => await RequestCloseAsync();

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        // Shift+Enter 交给多行 TextBox；普通 Enter 在 Preview 阶段发送，避免被控件吞掉。
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return;
        e.Handled = true;
        await SendAsync();
    }

    private async Task SendAsync()
    {
        if (_viewModel is null || _cts.IsCancellationRequested || !_viewModel.CanSend || _isSwitching)
            return;
        var text = InputBox.Text.Trim();
        if (text.Length == 0)
            return;

        var sessionId = _viewModel.ActiveSessionId;
        InputBox.Clear();
        InputBox.Focus();
        _activeTurn = _viewModel.SendAsync(text, _cts.Token);
        try { await _activeTurn; }
        catch (Exception ex) { LocalDiagnosticLog.Default.Write("chat_shell.send", ex); }
        if (string.Equals(sessionId, _viewModel.ActiveSessionId, StringComparison.Ordinal))
            RebuildMessages();
        RenderSessions();
    }

    private async void Step_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _cts.IsCancellationRequested)
            return;
        _activeTurn = _viewModel.StepAsync();
        try { await _activeTurn; }
        catch (Exception ex) { LocalDiagnosticLog.Default.Write("chat_shell.step", ex); }
        RebuildMessages();
        RenderSessions();
    }

    private async void AutoTalk_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _cts.IsCancellationRequested)
            return;
        _activeTurn = _viewModel.ToggleAutoTalkAsync(_cts.Token);
        try { await _activeTurn; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LocalDiagnosticLog.Default.Write("chat_shell.auto_talk", ex); }
        RebuildMessages();
    }

    private void Debug_Click(object sender, RoutedEventArgs e)
    {
        DebugBorder.Visibility = DebugBorder.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        DebugText.Text = _viewModel?.DirectorTrace ?? "";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_viewModel is null)
            return;
        _viewModel.SearchText = SearchBox.Text;
        _viewModel.RefreshSessions();
        RenderSessions();
    }

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || SessionList.SelectedItem is not SessionItemViewModel item)
            return;
        await SwitchAsync(item.Id);
    }

    private async Task SwitchAsync(string sessionId)
    {
        if (_viewModel is null || _cts.IsCancellationRequested || _isSwitching)
            return;
        if (_viewModel.ActiveSessionId == sessionId)
            return;

        _isSwitching = true;
        SessionList.IsEnabled = false;
        UpdateComposerState();

        // 先显示骨架屏，150ms 内没激活完才真正露面，避免快速切换时闪一下灰条。
        var showSkeleton = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        showSkeleton.Tick += (_, _) =>
        {
            showSkeleton.Stop();
            Skeleton.Visibility = Visibility.Visible;
        };
        showSkeleton.Start();
        try
        {
            _activeTurn = _viewModel.SwitchSessionAsync(sessionId, _cts.Token);
            await _activeTurn;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LocalDiagnosticLog.Default.Write("chat_shell.switch", ex); }
        finally
        {
            showSkeleton.Stop();
            Skeleton.Visibility = Visibility.Collapsed;
            _isSwitching = false;
            SessionList.IsEnabled = true;
            UpdateComposerState();
        }
        RebuildMessages();
        RenderHeader();
        RenderSessions();
    }

    private void SessionList_KeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null || _viewModel.Sessions.Count == 0)
            return;
        var index = SessionList.SelectedIndex;
        if (e.Key == Key.Down && index < _viewModel.Sessions.Count - 1)
        {
            SessionList.SelectedIndex = index + 1;
            e.Handled = true;
        }
        else if (e.Key == Key.Up && index > 0)
        {
            SessionList.SelectedIndex = index - 1;
            e.Handled = true;
        }
    }

    // ── 右键菜单 ─────────────────────────────────────────────────────────

    private SessionItemViewModel? _menuTarget;

    private void SessionList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null)
            return;
        var item = (e.OriginalSource as DependencyObject) is { } source
            ? FindAncestor<ListBoxItem>(source)
            : null;
        if (item?.DataContext is not SessionItemViewModel target)
            return;

        _menuTarget = target;
        if (FindResource("SessionMenuResource") is not ContextMenu menu)
            return;

        // 菜单项文案与可用性按目标会话定制：置顶/免打扰是切换式，
        // 重命名与成员管理只有群聊才有意义。
        foreach (var entry in menu.Items.OfType<MenuItem>())
        {
            switch (entry.Tag as string)
            {
                case "Pin":
                    entry.Header = target.Pinned ? "取消置顶" : "置顶会话";
                    break;
                case "Mute":
                    entry.Header = target.Muted ? "取消免打扰" : "消息免打扰";
                    break;
                case "Rename":
                case "Members":
                    entry.IsEnabled = target.IsGroup;
                    break;
            }
        }

        menu.PlacementTarget = item;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match)
                return match;
        return null;
    }

    /// <summary>右键菜单的唯一入口。用一个 Tag 分派，避免每个动作一个 x:Name 字段
    /// （资源字典里的命名元素不会变成窗口字段）。</summary>
    private async void SessionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _menuTarget is null || sender is not MenuItem { Tag: string action })
            return;
        var target = _menuTarget;

        switch (action)
        {
            case "Pin":
                await _viewModel.SetPinnedAsync(target.Id, !target.Pinned);
                RenderSessions();
                break;
            case "Mute":
                await _viewModel.SetMutedAsync(target.Id, !target.Muted);
                RenderSessions();
                break;
            case "MarkRead":
                await _viewModel.MarkReadAsync(target.Id);
                RenderSessions();
                break;
            case "Rename":
                await RenameAsync(target);
                break;
            case "Members":
                await EditMembersAsync(target);
                break;
            case "Clear":
                await ClearHistoryAsync(target);
                break;
            case "Delete":
                await DeleteSessionAsync(target);
                break;
        }
    }

    private async Task RenameAsync(SessionItemViewModel target)
    {
        var dialog = new RenameSessionDialog(target.Title) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SessionTitle))
            return;
        await _viewModel!.RenameAsync(target.Id, dialog.SessionTitle);
        RenderHeader();
        RenderSessions();
    }

    private async Task EditMembersAsync(SessionItemViewModel target)
    {
        var descriptor = target.Descriptor;
        var dialog = new GroupMembersDialog(descriptor) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var now = DateTimeOffset.Now;
        var presences = new List<MemberPresence>();
        foreach (var presence in descriptor.Presences)
        {
            // 已离开的保持原样；被移出的写 LeftAt = now（**不要删掉这一条**，
            // 否则投影就算不出"他什么时候不在场"的边界）。
            if (presence.LeftAt is not null)
            {
                presences.Add(presence);
                continue;
            }
            presences.Add(dialog.SelectedIds.Contains(presence.CharacterId)
                ? presence
                : presence with { LeftAt = now });
        }
        foreach (var id in dialog.SelectedIds)
        {
            if (presences.Any(p => p.CharacterId == id))
                continue;
            presences.Add(new MemberPresence(id, now));
        }

        await _viewModel!.ApplyPresencesAsync(descriptor.Id, presences);
        // 成员变了，视角缓存必须重建，否则被移除的人还会看到自己不在场时的消息。
        var updated = _viewModel.Catalog.Find(descriptor.Id);
        if (updated is not null)
        {
            await _viewModel.SessionStoreForHost.SaveSessionAsync(updated);
            // 被迫让会话重算视角：结束再激活会按新在场区间重建缓存。
            if (_viewModel.ActiveSessionId == descriptor.Id)
                await _viewModel.SwitchSessionAsync(descriptor.Id);
        }
        RenderHeader();
        RenderSessions();
    }

    private async Task ClearHistoryAsync(SessionItemViewModel target)
    {
        if (MessageBox.Show(this, "清空这个会话的聊天记录？会话和记忆会保留。", "清空聊天记录",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        if (_viewModel!.ActiveSessionId != target.Id)
        {
            await _viewModel.SwitchSessionAsync(target.Id);
        }
        await _viewModel.ClearHistoryAsync();
        RebuildMessages();
        RenderSessions();
    }

    private async Task DeleteSessionAsync(SessionItemViewModel target)
    {
        if (MessageBox.Show(this, $"删除会话「{target.Title}」？本地记录会移到回收站。", "删除会话",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        await _viewModel!.DeleteAsync(target.Id);
        _drafts.Remove(target.Id);
        RebuildMessages();
        RenderHeader();
        RenderSessions();
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        var dialog = new NewGroupDialog(CharacterCatalog.All) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedIds.Count < SessionFactory.MinGroupMembers)
            return;
        _activeTurn = CreateGroupAsync(dialog.SelectedIds, dialog.SessionTitle);
    }

    private async Task CreateGroupAsync(IReadOnlyList<string> memberIds, string title)
    {
        try
        {
            await _viewModel!.CreateGroupAsync(memberIds, title, CharacterThemeCatalog.AccentFor, _cts.Token);
            RenderSessions();
            RenderHeader();
            RebuildMessages();
        }
        catch (ArgumentException ex)
        {
            SetStatus($"建群失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("chat_shell.create_group", ex);
            SetStatus($"建群失败：{ex.GetType().Name}");
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.ActiveSession is not { } active)
            return;
        _ = ClearHistoryAsync(active);
    }


}
