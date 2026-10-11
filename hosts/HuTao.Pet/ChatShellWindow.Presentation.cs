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
    /// <summary>串行播放；同步播放丢到后台线程，不阻塞界面。</summary>
    private void PlayAudio(string path)
    {
        if (_cts.IsCancellationRequested || !File.Exists(path))
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _audioGate.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    using var player = new System.Media.SoundPlayer(path);
                    player.PlaySync();
                }
                finally { _audioGate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LocalDiagnosticLog.Default.Write("chat_shell.playback", ex); }
        });
    }

    // ── 状态行与自适应 ───────────────────────────────────────────────────

    /// <summary>系统提示走独立状态行；**绝不混进消息流**（角色气泡里只允许出现角色说的话）。</summary>
    private void SetStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            StatusText.Visibility = Visibility.Collapsed;
            return;
        }
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        _statusEpochText = message;
        _ = HideStatusLaterAsync(message);
    }

    private async Task HideStatusLaterAsync(string epoch)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), _cts.Token);
            if (!_cts.IsCancellationRequested && _statusEpochText == epoch)
                StatusText.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>窗口窄到放不下侧栏时自动折叠成头像列。</summary>
    private void UpdateResponsiveLayout()
        => SidebarColumn.Width = ActualWidth < 820
            ? new GridLength(64)
            : new GridLength(Math.Max(200, SidebarColumn.ActualWidth < 64 ? 280 : SidebarColumn.ActualWidth));

    // ── 窗口尺寸记忆 ─────────────────────────────────────────────────────

    private void LoadWindowPreferences()
    {
        try
        {
            if (!File.Exists(_preferencesPath))
                return;
            var settings = System.Text.Json.JsonSerializer.Deserialize<ChatShellPreferences>(
                File.ReadAllText(_preferencesPath));
            if (settings is null)
                return;
            if (settings.Width is > 720 and < 6000) Width = settings.Width.Value;
            if (settings.Height is > 480 and < 6000) Height = settings.Height.Value;

            // 坐标要先夹到虚拟屏幕范围内再用。
            // 踩过的坑：自动化检查把窗口放在 (-30000,-30000) 以"不打扰用户"，
            // 关窗时那个坐标被当成用户偏好存了下来；下一次真实启动就"打开在屏幕外"，
            // 表现为**点了 💬 什么都没发生**。所以不仅存的时候要过滤，读的时候也要夹。
            if (double.IsFinite(settings.Left) && double.IsFinite(settings.Top) && IsOnScreen(settings.Left, settings.Top))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = settings.Left;
                Top = settings.Top;
            }
            else if (double.IsFinite(settings.Left) && double.IsFinite(settings.Top))
            {
                LocalDiagnosticLog.Default.Write("chat_shell.offscreen_preference",
                    new InvalidOperationException(
                        $"Saved chat window position ({settings.Left},{settings.Top}) is off-screen; ignored."));
            }
            if (settings.Maximized)
                WindowState = WindowState.Maximized;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[chat-shell] 无法读取窗口设置：{ex.Message}");
        }
    }

    /// <summary>窗口坐标是否落在任一显示器的可见范围内（留一点余量，允许贴边）。</summary>
    private static bool IsOnScreen(double left, double top)
    {
        // 留 200px 余量：只要有一角能被抓到，用户就还能把它拖回来。
        const double margin = 200;
        var virtualLeft = SystemParameters.VirtualScreenLeft - margin;
        var virtualTop = SystemParameters.VirtualScreenTop - margin;
        var virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + margin;
        var virtualBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + margin;
        return left >= virtualLeft && left <= virtualRight && top >= virtualTop && top <= virtualBottom;
    }

    /// <summary>
    /// 保证窗口至少有一部分在屏幕上。WPF 不会替我们做这件事——
    /// 显示器拔掉、分辨率变小、或存了一个屏幕外坐标，窗口就再也点不到了。
    /// </summary>
    private void KeepWindowOnScreen()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top))
            return;
        const double visibleEdge = 64;
        var minLeft = SystemParameters.VirtualScreenLeft - Width + visibleEdge;
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - visibleEdge;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - visibleEdge;
        Left = Math.Clamp(Left, minLeft, maxLeft);
        Top = Math.Clamp(Top, minTop, maxTop);
    }

    private void SaveWindowPreferences()
    {
        try
        {
            // 屏幕外的坐标不写回：那多半不是"用户放那儿了"，而是无头检查/显示器变更的残留，
            // 存下来会让下一次启动开在看不见的地方。
            if (IsTestWindow || !IsOnScreen(Left, Top))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath)!);
            var restore = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            var settings = new ChatShellPreferences(
                restore.Width, restore.Height, restore.Left, restore.Top,
                WindowState == WindowState.Maximized);
            File.WriteAllText(_preferencesPath,
                System.Text.Json.JsonSerializer.Serialize(settings,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[chat-shell] 无法保存窗口设置：{ex.Message}");
        }
    }

    private sealed record ChatShellPreferences(
        double? Width, double? Height, double Left, double Top, bool Maximized);


}
