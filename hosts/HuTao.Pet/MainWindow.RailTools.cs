using System.Windows;
using Microsoft.Win32;

namespace HuTao.Pet;

/// <summary>
/// 桌宠暴露给**聊天客户端侧边栏**的动作。
///
/// 为什么要这样：形态转向 QQNT 之后，聊天窗是主界面，桌宠悬浮窗变成它的一个面板。
/// 于是桌宠原本靠按钮触发的那些能力（角色切换、状态感知、朗读文档、显示模式）
/// 需要能从聊天窗那一侧调用。
///
/// 刻意**只抽取、不复制**：下面每个方法都只是把既有 <c>*_Click</c> 处理器里的
/// 核心逻辑提出来，处理器再转调它。这样"从托盘点"和"从聊天窗点"走的是
/// **同一条代码路径**——复制实现迟早会两边行为不一致。
/// </summary>
public partial class MainWindow
{
    /// <summary>当前角色显示名，给侧边栏显示"正在和谁说话"。</summary>
    internal string RailCharacterName => CurrentCharacter.Name;

    /// <summary>当前角色 emoji，给侧边栏做头像。</summary>
    internal string RailCharacterEmoji => CurrentCharacter.Emoji;

    /// <summary>应用状态感知是否开启（读前台进程名与窗口标题）。</summary>
    internal bool RailAwarenessEnabled => _allowAppAwareness;

    /// <summary>切换应用状态感知。与 <c>Awareness_Click</c> 共用同一条路径。</summary>
    internal void RailToggleAwareness() => ToggleAwareness();

    /// <summary>切换显示模式（完整 / 头像气泡 / 隐藏到托盘）。</summary>
    internal void RailSetDisplayMode(WindowDisplayMode mode) => ApplyDisplayMode(mode);

    /// <summary>朗读文档（弹文件选择框）。与 <c>ReadDocument_Click</c> 共用同一条路径。</summary>
    internal Task RailReadDocumentAsync() => PickAndReadDocumentAsync();

    /// <summary>清空当前角色的聊天记录。</summary>
    internal void RailClearConversation() => ClearConversation();

    /// <summary>当前显示模式，供侧边栏做"循环三态"的下一步判断。</summary>
    internal WindowDisplayMode RailDisplayMode => _displayMode;

    /// <summary>把角色切换面板打开（聊天窗不方便内嵌那个 Popup，所以唤起桌宠自己切）。</summary>
    internal void RailOpenCharacterPicker() => ShowCharacterPicker();

    /// <summary>朗读是否可用（运行时是否已就绪）。</summary>
    internal bool RailDocumentReadingReady => _documentReader is not null && !_cts.IsCancellationRequested;

    /// <summary>运行时是否已就绪。</summary>
    internal bool RailRuntimeReady => _runtimeFactory is not null && !_cts.IsCancellationRequested;

    /// <summary>
    /// 作为**聊天窗的侧边面板**运行时调用：把桌宠自己的入口按钮收起来。
    ///
    /// 为什么：形态已转向 QQNT，聊天是主界面。这个面板由聊天窗开出来，
    /// 它的 💬 按钮还能再开一个聊天窗——那正是用户要求暂时关掉的东西，
    /// 两个聊天窗并存也会让同一个角色起两份 Agent。
    /// 而 —（隐藏）/ ◫（缩成气泡）在面板语境下也不需要——面板的显隐由侧边栏管。
    /// 独立的 <c>--pet</c> 形态不受影响，那些按钮照旧。
    /// </summary>
    internal void EnterPanelMode()
    {
        ChatRoomButton.Visibility = Visibility.Collapsed;
        ChatRoomButton.IsEnabled = false;
        ChatRoomButton.ToolTip = "聊天请用聊天客户端（本面板是它的侧边栏）";
        HideButton.Visibility = Visibility.Collapsed;
        CompactButton.Visibility = Visibility.Collapsed;
    }
}
