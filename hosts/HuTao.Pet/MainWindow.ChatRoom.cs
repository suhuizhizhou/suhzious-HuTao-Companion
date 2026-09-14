using System.Windows;

namespace HuTao.Pet;

/// <summary>
/// 聊天室入口。单独一个 partial 文件，是因为它连接的是两个窗口，
/// 主体逻辑（选人、调度、渲染、播放）都在 <see cref="ChatRoomWindow"/> 里。
/// </summary>
public partial class MainWindow
{
    private ChatRoomWindow? _chatRoom;

    private void ChatRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeFactory is null)
        {
            ShowStatus("运行时还没准备好，稍等一下再试。");
            return;
        }

        // 单例：重复点是「把聊天室拿到前面来」，不是再开一间。
        // 多开会让同一个角色的两份 Agent 同时活着，记忆与语音都会互相打架。
        if (_chatRoom is { IsLoaded: true })
        {
            _chatRoom.Activate();
            return;
        }

        _chatRoom = new ChatRoomWindow(_runtimeFactory, _tts, CurrentCharacter.Id)
        {
            // 桌宠默认置顶；不设 Owner 的话聊天室会被压在桌宠下面，看起来像没打开。
            Owner = this,
        };
        _chatRoom.Closed += (_, _) => _chatRoom = null;
        _chatRoom.Show();
    }
}
