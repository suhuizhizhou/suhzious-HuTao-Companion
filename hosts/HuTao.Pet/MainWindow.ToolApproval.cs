using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HuTao.Dialogue.Tools;

namespace HuTao.Pet;

public partial class MainWindow
{
    private async Task<bool> ConfirmToolAsync(ToolApproval action, CancellationToken ct)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            var approved = false;
            var dialog = new Window
            {
                Title = "确认操作", Owner = this, Width = 620, Height = 540,
                MinWidth = 420, MinHeight = 340, MaxWidth = SystemParameters.WorkArea.Width,
                MaxHeight = SystemParameters.WorkArea.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            var panel = new DockPanel { Margin = new Thickness(18) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 88, Padding = new Thickness(12, 6, 12, 6) };
            var confirm = new Button { Content = "允许此次操作", MinWidth = 120, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
            cancel.Click += (_, _) => dialog.Close();
            confirm.Click += (_, _) => { approved = true; dialog.Close(); };
            buttons.Children.Add(cancel); buttons.Children.Add(confirm);
            DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
            var title = new TextBlock { Text = action.Description, TextWrapping = TextWrapping.Wrap,
                FontSize = 15, Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
            using var document = JsonDocument.Parse(action.ArgumentsJson);
            panel.Children.Add(new TextBox
            {
                Text = "用户请求：\n" + action.UserRequest + "\n\n待执行内容：\n" +
                    JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
                    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10), FontSize = 13
            });
            dialog.Content = panel;
            using var cancellation = ct.Register(() => Dispatcher.BeginInvoke(new Action(() => dialog.Close())));
            dialog.ShowDialog();
            return approved && !ct.IsCancellationRequested;
        });
    }
}
