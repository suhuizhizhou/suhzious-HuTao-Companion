using System.Windows;

namespace HuTao.Pet;

/// <summary>
/// 聊天入口。这里连的是两个窗口：桌宠主窗与聊天客户端窗。
///
/// 字段类型是 <see cref="IChatSurface"/> 而不是某个具体窗口——
/// 换实现（旧聊天室 → QQNT 会话列表）时，桌宠这一侧不需要跟着改。
/// </summary>
public partial class MainWindow
{
    private IChatSurface? _chatRoom;

    private void ChatRoom_Click(object sender, RoutedEventArgs e)
    {
        if(_cts.IsCancellationRequested)return;
        if (_runtimeFactory is null)
        {
            ShowStatus("运行时还没准备好，稍等一下再试。");
            return;
        }

        // 单例：重复点是「把聊天拿到前面来」，不是再开一间。
        // 多开会让同一个角色的两份 Agent 同时活着，记忆与语音都会互相打架。
        if (_chatRoom is { IsLoaded: true })
        {
            _chatRoom.BringToFront();
            return;
        }

        _chatRoom = new ChatShellWindow(_runtimeFactory, _tts, preferredCharacterId: CurrentCharacter.Id)
        {
            // 桌宠默认置顶；不设 Owner 的话聊天窗会被压在桌宠下面，看起来像没打开。
            Owner = this,
        };
        _chatRoom.Closed += (_, _) => _chatRoom = null;
        _chatRoom.Show();
    }
}
