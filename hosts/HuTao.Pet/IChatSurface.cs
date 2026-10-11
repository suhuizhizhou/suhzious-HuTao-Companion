namespace HuTao.Pet;

/// <summary>
/// 「桌宠能托管的那种子窗口」。让 <see cref="MainWindow"/> 不必知道它托的是
/// 旧聊天室窗口还是新的 QQNT 聊天客户端——这样把聊天室换成会话列表时，
/// 桌宠的关闭排空链路（等子窗真正结束再关自己）一行都不用改。
///
/// 只暴露桌宠真正用得到的两件事：它是不是加载好了、以及请它关闭并等结果。
/// </summary>
public interface IChatSurface
{
    bool IsLoaded { get; }

    /// <summary>
    /// 把窗口带到前台（已打开时复用，而不是再开一间）。
    /// 名字不叫 Activate 是为了避开 <c>Window.Activate()</c> 的签名冲突。
    /// </summary>
    void BringToFront();

    void Show();

    /// <summary>
    /// 请求关闭并等它真正结束。返回 <c>false</c> 表示这次关闭**没成功**
    /// （例如记忆尚未落盘），此时父窗必须放弃关闭——否则会丢掉用户的数据。
    /// </summary>
    Task<bool> RequestCloseAsync();

    event EventHandler? Closed;
}
