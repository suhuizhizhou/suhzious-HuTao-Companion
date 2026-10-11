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


/// <summary>重命名会话的一行输入框。自绘以保持与聊天窗同一套视觉。</summary>
public sealed class RenameSessionDialog : Window
{
    private readonly TextBox _box;

    public RenameSessionDialog(string current)
    {
        Title = "重命名会话";
        Width = 360; Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));

        _box = new TextBox
        {
            Text = current,
            FontSize = 14,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 14),
            MaxLength = SessionFactory.MaxTitleLength,
        };
        var confirm = new Button
        {
            Content = "确定", Width = 80, Height = 30, IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x3C)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        confirm.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button
        {
            Content = "取消", Width = 80, Height = 30, IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { confirm, cancel },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = "会话名称", FontSize = 12, Margin = new Thickness(0, 0, 0, 6) },
                _box,
                buttons,
            },
        };
        Loaded += (_, _) => { _box.SelectAll(); _box.Focus(); };
    }

    public string SessionTitle => _box.Text.Trim();
}
