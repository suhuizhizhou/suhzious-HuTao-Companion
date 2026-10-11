using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using HuTao.Pet;

/// <summary>真实 WPF 控件回归；配合 Computer Use 键鼠验收，而非替代它。</summary>
internal static class UiInteractionChecks
{
    public static async Task RunAsync(ChatShellWindow window)
    {
        var count = 0;
        void Check(string name, bool value)
        {
            Console.WriteLine($"{(value ? "PASS" : "FAIL")} {name}");
            if (!value) throw new InvalidOperationException(name);
            count++;
        }
        Task Call(string name, params object[] args) => (Task)typeof(ChatShellWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;
        await Call("SwitchAsync", "single_hutao");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check("ui_sidebar_avatar_has_content", ChatShellWindow.FindVisual(window.SessionList, "AvatarHost")
            is System.Windows.Controls.ContentControl { Content: not null });
        Check("ui_empty_send_disabled", !window.SendButton.IsEnabled);
        window.InputBox.Text = "   ";
        Check("ui_whitespace_send_disabled", !window.SendButton.IsEnabled);
        window.InputBox.Text = "胡桃专属草稿\n第二行";
        Check("ui_multiline_enabled", window.InputBox.AcceptsReturn);
        Check("ui_nonempty_send_enabled", window.SendButton.IsEnabled);
        await Call("SwitchAsync", "single_furina");
        Check("ui_draft_does_not_cross_session", window.InputBox.Text == "");
        window.InputBox.Text = "芙宁娜专属草稿";
        await Call("SwitchAsync", "single_hutao");
        Check("ui_draft_restored", window.InputBox.Text == "胡桃专属草稿\n第二行");
        var pending = Call("SendAsync");
        Check("ui_accepted_send_clears_composer", window.InputBox.Text == "");
        Check("ui_busy_send_disabled", !window.SendButton.IsEnabled);
        window.InputBox.Text = "下一条还没发送";
        await Call("SendAsync");
        Check("ui_busy_enter_keeps_draft", window.InputBox.Text == "下一条还没发送");
        await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Check("ui_completion_preserves_new_draft", window.InputBox.Text == "下一条还没发送");
        Check("ui_completion_reenables_send", window.SendButton.IsEnabled);
        await Call("SwitchAsync", "single_furina");
        Check("ui_other_draft_restored", window.InputBox.Text == "芙宁娜专属草稿");
        window.WindowState = WindowState.Minimized;
        window.BringToFront();
        Check("ui_restore_from_minimized", window.WindowState == WindowState.Normal);
        window.Width = 760;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check("ui_compact_sidebar", window.SidebarWidthForCheck <= 80);
        window.Width = 960;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check("ui_expanded_sidebar", window.SidebarWidthForCheck >= 200);
        Console.WriteLine($"UI interaction checks: {count}/{count}");
    }
}
