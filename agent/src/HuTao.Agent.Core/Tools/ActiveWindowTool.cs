using System.Diagnostics;
using System.Runtime.InteropServices;
using HuTao.Agent.Core.Abstractions;

namespace HuTao.Agent.Core.Tools;

/// <summary>
/// 在用户显式授权后读取当前前台应用的进程名。
/// 不读取窗口标题、窗口内容、文件路径，也不枚举后台进程。
/// </summary>
public sealed class ActiveWindowTool : IAgentTool
{
    private readonly Func<bool> _hasPermission;

    public ActiveWindowTool(Func<bool>? hasPermission = null)
    {
        _hasPermission = hasPermission ?? (() => false);
    }

    public string Name => "active_window";
    public string Description => "经用户授权后，仅获取当前前台应用的进程名和桌宠前后台状态";
    public AgentToolPolicy Policy => new(
        Sensitivity: AgentToolSensitivity.ActivityMetadata,
        RequiresExplicitIntent: true);

    public Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default)
    {
        if (!_hasPermission())
            return Task.FromResult("未授权应用状态感知；没有读取任何窗口或进程信息");

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return Task.FromResult("已授权，但当前无法获取前台应用");

        GetWindowThreadProcessId(hwnd, out var pid);

        var procName = "未知进程";
        try { procName = Process.GetProcessById((int)pid).ProcessName; }
        catch { /* 进程可能已退出 */ }

        var isPetForeground = pid == Environment.ProcessId;
        var petState = isPetForeground ? "前台" : "后台";
        var activity = ClassifyActivity(procName, isPetForeground);
        return Task.FromResult(
            $"桌宠状态: {petState}；当前前台应用进程: {procName}；粗略活动: {activity}；未读取窗口标题或内容");
    }

    private static string ClassifyActivity(string processName, bool isPetForeground)
    {
        if (isPetForeground)
            return "正在使用桌宠";

        var name = processName.ToLowerInvariant();
        if (name is "devenv" or "code" or "idea64" or "pycharm64" or "rider64" or "androidstudio")
            return "可能在编程或查看工程（具体内容未知）";
        if (name is "powershell" or "pwsh" or "cmd" or "windowsterminal" or "wt")
            return "可能在使用命令行（具体内容未知）";
        if (name is "chrome" or "msedge" or "firefox" or "opera" or "brave")
            return "可能在浏览网页（页面内容未知）";
        if (name is "winword" or "excel" or "powerpnt" or "wps" or "et" or "wpp")
            return "可能在处理文档（文档内容未知）";
        if (name is "wechat" or "weixin" or "qq" or "teams" or "discord" or "slack")
            return "可能在沟通交流（对话内容未知）";
        if (name is "vlc" or "potplayermini64" or "spotify" or "cloudmusic")
            return "可能在播放媒体（媒体内容未知）";
        return "正在使用某个应用（具体活动未知）";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
