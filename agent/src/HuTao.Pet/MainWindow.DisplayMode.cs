using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace HuTao.Pet;

internal enum WindowDisplayMode
{
    Full,
    Compact,
    Hidden,
}

/// <summary>完整窗口、头像气泡和托盘隐藏三种展示状态，以及原生置顶保障。</summary>
public partial class MainWindow
{
    private const double FullWidth = 350;
    private const double FullHeight = 480;
    private const double CompactWidth = 300;
    private const double CompactHeight = 112;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private readonly string _windowPreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HuTaoCompanion",
        "ui-settings.json");
    private WindowDisplayMode _displayMode = WindowDisplayMode.Full;
    private bool _topmostEnabled = true;
    private bool _isExiting;

    private void InitializeWindowPresentation()
    {
        LoadWindowPreferences();
        SourceInitialized += (_, _) => EnsureNativeTopmost();
        Loaded += (_, _) =>
        {
            ApplyDisplayMode(_displayMode, persist: false);
            EnsureNativeTopmost();
        };
        Activated += (_, _) => EnsureNativeTopmost();
        Deactivated += (_, _) => Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            EnsureNativeTopmost);
        Closing += OnWindowClosing;
    }

    private void CompactMode_Click(object sender, RoutedEventArgs e)
        => ApplyDisplayMode(WindowDisplayMode.Compact);

    private void FullMode_Click(object sender, RoutedEventArgs e)
        => ApplyDisplayMode(WindowDisplayMode.Full);

    private void HideWindow_Click(object sender, RoutedEventArgs e)
        => ApplyDisplayMode(WindowDisplayMode.Hidden);

    private void CompactModePanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ApplyDisplayMode(WindowDisplayMode.Full);
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 点击按钮或鼠标状态已变化时无需拖动。
        }
    }

    private void ApplyDisplayMode(WindowDisplayMode mode, bool persist = true)
    {
        _displayMode = mode;
        CharacterPickerPopup.IsOpen = false;

        if (mode == WindowDisplayMode.Hidden)
        {
            FullModePanel.Visibility = Visibility.Collapsed;
            CompactModePanel.Visibility = Visibility.Collapsed;
            Hide();
            UpdateTrayMenuState();
            if (persist)
                SaveWindowPreferences();
            return;
        }

        Width = mode == WindowDisplayMode.Full ? FullWidth : CompactWidth;
        Height = mode == WindowDisplayMode.Full ? FullHeight : CompactHeight;
        FullModePanel.Visibility = mode == WindowDisplayMode.Full
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactModePanel.Visibility = mode == WindowDisplayMode.Compact
            ? Visibility.Visible
            : Visibility.Collapsed;
        ShowInTaskbar = mode == WindowDisplayMode.Full;
        WindowState = WindowState.Normal;

        if (!IsVisible)
            Show();

        KeepWindowOnScreen();
        EnsureNativeTopmost();
        UpdateTrayMenuState();
        if (persist)
            SaveWindowPreferences();
    }

    private void SetTopmostEnabled(bool enabled)
    {
        _topmostEnabled = enabled;
        Topmost = enabled;
        EnsureNativeTopmost();
        UpdateTrayMenuState();
        SaveWindowPreferences();
    }

    private void EnsureNativeTopmost()
    {
        if (!IsVisible)
            return;

        Topmost = _topmostEnabled;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        _ = SetWindowPos(
            handle,
            _topmostEnabled ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void UpdateCompactBubble(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        CompactBubbleText.Text = text.Trim();
    }

    private void KeepWindowOnScreen()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top))
            return;

        const double visibleEdge = 48;
        var minLeft = SystemParameters.VirtualScreenLeft - Width + visibleEdge;
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - visibleEdge;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - visibleEdge;
        Left = Math.Clamp(Left, minLeft, maxLeft);
        Top = Math.Clamp(Top, minTop, maxTop);
    }

    private void LoadWindowPreferences()
    {
        try
        {
            if (!File.Exists(_windowPreferencesPath))
                return;

            var settings = JsonSerializer.Deserialize<WindowPreferences>(
                File.ReadAllText(_windowPreferencesPath));
            if (settings is null)
                return;

            if (Enum.TryParse<WindowDisplayMode>(settings.DisplayMode, true, out var mode))
                _displayMode = mode;
            _topmostEnabled = settings.Topmost;
            Topmost = _topmostEnabled;

            if (double.IsFinite(settings.Left) && double.IsFinite(settings.Top))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = settings.Left;
                Top = settings.Top;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[ui] 无法读取窗口设置：{ex.Message}");
        }
    }

    private void SaveWindowPreferences()
    {
        try
        {
            var directory = Path.GetDirectoryName(_windowPreferencesPath)!;
            Directory.CreateDirectory(directory);
            var settings = new WindowPreferences(
                _displayMode.ToString(),
                _topmostEnabled,
                double.IsFinite(Left) ? Left : 0,
                double.IsFinite(Top) ? Top : 0);
            File.WriteAllText(
                _windowPreferencesPath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[ui] 无法保存窗口设置：{ex.Message}");
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
            return;

        // Alt+F4 与标题栏关闭按钮保持“退出”语义；隐藏只通过专用按钮或托盘菜单触发。
        _isExiting = true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private sealed record WindowPreferences(
        string DisplayMode,
        bool Topmost,
        double Left,
        double Top);
}
