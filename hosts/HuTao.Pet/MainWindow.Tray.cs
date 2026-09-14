using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace HuTao.Pet;

/// <summary>完全隐藏后的恢复入口，以及不抢焦点的置顶开关。</summary>
public partial class MainWindow
{
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _fullModeTrayItem;
    private Forms.ToolStripMenuItem? _compactModeTrayItem;
    private Forms.ToolStripMenuItem? _hiddenModeTrayItem;
    private Forms.ToolStripMenuItem? _topmostTrayItem;

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        _fullModeTrayItem = new Forms.ToolStripMenuItem("打开聊天窗口");
        _compactModeTrayItem = new Forms.ToolStripMenuItem("桌宠头像模式");
        _hiddenModeTrayItem = new Forms.ToolStripMenuItem("完全隐藏");
        _topmostTrayItem = new Forms.ToolStripMenuItem("始终置顶");
        var exitItem = new Forms.ToolStripMenuItem("退出");

        _fullModeTrayItem.Click += (_, _) => Dispatcher.Invoke(
            () => ApplyDisplayMode(WindowDisplayMode.Full));
        _compactModeTrayItem.Click += (_, _) => Dispatcher.Invoke(
            () => ApplyDisplayMode(WindowDisplayMode.Compact));
        _hiddenModeTrayItem.Click += (_, _) => Dispatcher.Invoke(
            () => ApplyDisplayMode(WindowDisplayMode.Hidden));
        _topmostTrayItem.Click += (_, _) => Dispatcher.Invoke(
            () => SetTopmostEnabled(!_topmostEnabled));
        exitItem.Click += (_, _) => Dispatcher.Invoke(ExitFromTray);

        menu.Items.AddRange(
        [
            _fullModeTrayItem,
            _compactModeTrayItem,
            _hiddenModeTrayItem,
            new Forms.ToolStripSeparator(),
            _topmostTrayItem,
            new Forms.ToolStripSeparator(),
            exitItem,
        ]);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "胡桃 AI 桌宠",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(
            () => ApplyDisplayMode(WindowDisplayMode.Full));
        UpdateTrayMenuState();
    }

    private void UpdateTrayMenuState()
    {
        if (_fullModeTrayItem is null)
            return;

        _fullModeTrayItem.Checked = _displayMode == WindowDisplayMode.Full;
        _compactModeTrayItem!.Checked = _displayMode == WindowDisplayMode.Compact;
        _hiddenModeTrayItem!.Checked = _displayMode == WindowDisplayMode.Hidden;
        _topmostTrayItem!.Checked = _topmostEnabled;
    }

    private void ExitFromTray()
    {
        _isExiting = true;
        Close();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null)
            return;

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }
}
