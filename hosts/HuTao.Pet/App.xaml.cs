using System.Windows;
using HuTao.Dialogue.Runtime;
using HuTao.Foundation;
using HuTao.Voice;

namespace HuTao.Pet;

/// <summary>
/// 宿主入口。**没有 StartupUri**：窗口在这里按命令行选择，因为本项目有两个互不相同的入口——
/// 桌宠（陪伴）与 QQNT 聊天客户端（会话列表）。
///
/// `--chat-shell` 只开聊天客户端：既方便用户"我今天只想聊天"，
/// 也是 UI 自动化检查能确定性驱动它的前提（两个窗口同时活着时，
/// "输入框"和"消息区"各有两个，按 AutomationId 找会选错）。
/// </summary>
public partial class App : Application
{
    /// <summary>显式只开聊天客户端（等价于默认行为，保留是为了向后兼容与脚本可读性）。</summary>
    public const string ChatShellSwitch = "--chat-shell";

    /// <summary>显式只开桌宠悬浮窗（旧形态）。</summary>
    public const string PetSwitch = "--pet";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 形态已转向 QQNT：**默认启动聊天客户端**。
        // 桌宠悬浮窗保留为显式入口（--pet），因为它承载的"陪伴"是另一种用法；
        // 但它已经不再默认启动，也不再置顶（见 MainWindow.DisplayMode.cs）。
        var wantsPet = e.Args.Any(arg => string.Equals(arg, PetSwitch, StringComparison.OrdinalIgnoreCase));
        if (wantsPet)
        {
            new MainWindow().Show();
            return;
        }

        StartChatShell();
    }

    private void StartChatShell()
    {
        ProjectEnvironment.LoadDotEnv(log: Console.WriteLine);
        var repoRoot = ProjectEnvironment.FindRepositoryRoot(
            Environment.CurrentDirectory, AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

        // 常驻 TTS：整个进程只应有一个引擎实例（它内部有参考音频缓存，多开会互相踩）。
        var tts = TtsRuntimeFactory.CreateGptSovits(repoRoot, preferResident: true, log: Console.WriteLine);
        var factory = new AgentRuntimeFactory(
            repoRoot,
            _ => tts,
            Console.WriteLine,
            approveTool: (_, _) => Task.FromResult(true));

        var window = new ChatShellWindow(factory, tts, repoRoot);
        MainWindow = window;
        window.Show();
    }
}
