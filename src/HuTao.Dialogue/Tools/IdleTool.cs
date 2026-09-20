using System.Runtime.InteropServices;
using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Tools;

/// <summary>检测用户空闲时长（键盘/鼠标无输入的时间），判断是否适合主动搭话。</summary>
public sealed class IdleTool : IAgentTool
{
    public string Name => "idle";
    public AgentToolCategory Category => AgentToolCategory.Observation;
    public ToolInputSchema InputSchema => ToolInputSchema.Empty;
    public string Description => "检测用户空闲了多久（秒），判断是否在忙";

    public Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default)
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return Task.FromResult("（无法检测空闲状态）");

        // TickCount 会溢出回绕，这里简单处理，够用即可
        var idleMs = (uint)Environment.TickCount - info.dwTime;
        var idleSec = (int)(idleMs / 1000);
        return Task.FromResult($"用户空闲约 {idleSec} 秒");
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}
