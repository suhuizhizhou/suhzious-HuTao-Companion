using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Tools;

/// <summary>
/// 读取当前前台应用，回答「用户此刻在做什么」。
/// 默认开启（用户可在桌宠面板一键关闭）；关闭时不读取任何窗口或进程信息。
///
/// 隐私边界：只读前台那一个窗口的进程名与标题，不截图、不读文件、不枚举后台进程。
/// 标题会做长度截断与控制字符清理，避免把超长路径或异常字符灌进提示词。
/// </summary>
public sealed class ActiveWindowTool : IAgentTool
{
    private const int MaxTitleLength = 120;
    private const int MaxDwellReportSeconds = 4 * 60 * 60;

    private readonly Func<bool> _hasPermission;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private string? _lastProcess;
    private DateTimeOffset _lastProcessSince = DateTimeOffset.MinValue;

    public ActiveWindowTool(Func<bool>? hasPermission = null, Func<DateTimeOffset>? clock = null)
    {
        _hasPermission = hasPermission ?? (() => true);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string Name => "active_window";

    public string Description =>
        "获取用户当前前台应用的进程名、窗口标题和已停留时长，用于判断用户此刻在做什么、是否适合打扰";

    public AgentToolPolicy Policy => new(
        Sensitivity: AgentToolSensitivity.ActivityMetadata,
        RequiresExplicitIntent: false);

    public Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default)
    {
        if (!_hasPermission())
            return Task.FromResult("用户已关闭应用状态感知；没有读取任何窗口或进程信息。不要猜测用户在做什么。");

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return Task.FromResult("感知已开启，但当前无法获取前台窗口。");

        GetWindowThreadProcessId(hwnd, out var pid);
        var processName = ResolveProcessName(pid);
        var isPetForeground = pid == Environment.ProcessId;
        var title = isPetForeground ? "" : Sanitize(ReadWindowTitle(hwnd));
        var (activity, detail) = ClassifyActivity(processName, title, isPetForeground);
        var dwell = TrackDwell(isPetForeground ? "hu_tao_pet" : processName.ToLowerInvariant());

        var builder = new StringBuilder();
        builder.Append($"用户当前在做什么: {activity}");
        if (detail.Length > 0)
            builder.Append($"（{detail}）");
        builder.Append($"\n前台应用进程: {processName}");
        if (title.Length > 0)
            builder.Append($"\n窗口标题: {title}");
        builder.Append($"\n桌宠状态: {(isPetForeground ? "前台（用户正在看桌宠）" : "后台")}");
        builder.Append($"\n已连续停留: {DescribeDuration(dwell)}");

        return Task.FromResult(builder.ToString());
    }

    /// <summary>同一前台应用连续停留多久；切走再回来会重新计时。</summary>
    private TimeSpan TrackDwell(string key)
    {
        var now = _clock();
        lock (_gate)
        {
            if (!string.Equals(_lastProcess, key, StringComparison.Ordinal))
            {
                _lastProcess = key;
                _lastProcessSince = now;
                return TimeSpan.Zero;
            }

            var dwell = now - _lastProcessSince;
            return dwell < TimeSpan.Zero || dwell > TimeSpan.FromSeconds(MaxDwellReportSeconds)
                ? TimeSpan.Zero
                : dwell;
        }
    }

    private static string DescribeDuration(TimeSpan dwell)
    {
        if (dwell.TotalSeconds < 30)
            return "刚切换过来";
        if (dwell.TotalMinutes < 1)
            return $"{(int)dwell.TotalSeconds} 秒";
        if (dwell.TotalHours < 1)
            return $"{(int)dwell.TotalMinutes} 分钟";
        return $"{(int)dwell.TotalHours} 小时 {(int)(dwell.TotalMinutes % 60)} 分钟";
    }

    private static string ResolveProcessName(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return "未知进程"; }
    }

    /// <summary>标题里常见「文件 - 应用名」结构，只取去掉应用后缀的主体，减少噪音。</summary>
    private static string Sanitize(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        var cleaned = new StringBuilder(title.Length);
        foreach (var ch in title)
        {
            if (char.IsControl(ch))
                continue;
            cleaned.Append(ch);
        }

        var value = cleaned.ToString().Trim();
        return value.Length <= MaxTitleLength ? value : value[..MaxTitleLength] + "…";
    }

    private static (string Activity, string Detail) ClassifyActivity(
        string processName,
        string title,
        bool isPetForeground)
    {
        if (isPetForeground)
            return ("正在和桌宠聊天", "");

        var name = processName.ToLowerInvariant();
        var subject = ExtractSubject(title);

        if (name is "devenv" or "code" or "idea64" or "pycharm64" or "rider64" or "androidstudio"
            or "webstorm64" or "goland64" or "clion64" or "sublime_text" or "notepad++")
            return ("在写代码或看工程", subject);
        if (name is "powershell" or "pwsh" or "cmd" or "windowsterminal" or "wt" or "conhost"
            or "mintty" or "ubuntu")
            return ("在用命令行", subject);
        if (name is "chrome" or "msedge" or "firefox" or "opera" or "brave" or "vivaldi" or "360se" or "qqbrowser")
            return ("在浏览网页", subject);
        if (name is "winword" or "excel" or "powerpnt" or "wps" or "et" or "wpp" or "onenote" or "acrobat"
            or "sumatrapdf" or "typora" or "obsidian")
            return ("在处理文档", subject);
        if (name is "wechat" or "weixin" or "qq" or "teams" or "discord" or "slack" or "dingtalk" or "feishu" or "lark")
            return ("在跟人聊天沟通", subject);
        if (name is "vlc" or "potplayermini64" or "potplayer" or "mpv" or "spotify" or "cloudmusic" or "bilibili")
            return ("在看视频或听歌", subject);
        if (name is "photoshop" or "illustrator" or "blender" or "krita" or "sai2" or "clipstudiopaint")
            return ("在做图或做模型", subject);
        if (name is "explorer")
            return ("在翻文件", subject);
        if (name.StartsWith("steam", StringComparison.Ordinal) || name is "yuanshen" or "genshinimpact" or "starrail")
            return ("在玩游戏", subject);
        return ("在使用某个应用", subject);
    }

    /// <summary>从窗口标题里抽出「在做什么事」的主体，去掉应用名尾缀。</summary>
    private static string ExtractSubject(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        var value = title.Trim();
        foreach (var separator in new[] { " - ", " – ", " — ", " | " })
        {
            var index = value.LastIndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                value = value[..index].Trim();
                break;
            }
        }

        return value.Length <= 60 ? value : value[..60] + "…";
    }

    private static string ReadWindowTitle(IntPtr hwnd)
    {
        try
        {
            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
                return "";
            var buffer = new StringBuilder(length + 1);
            _ = GetWindowText(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }
        catch
        {
            return "";
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}
