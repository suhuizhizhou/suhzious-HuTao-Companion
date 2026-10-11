using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HuTao.Foundation.Diagnostics;

namespace HuTao.Pet;

/// <summary>
/// 抽屉里的一项。动作是**转发给桌宠**执行的，见 MainWindow.RailTools.cs。
///
/// <see cref="ToString"/> 重写是有原因的：ListBox 项默认把数据对象的
/// <c>ToString()</c> 当可访问性名称，而 record 的默认实现会把整个对象
/// （含 <c>Invoke = System.Func`1[...]</c> 这种内部细节）吐给屏幕阅读器和
/// UIAutomation。会话行之前踩过同一个坑（名字是 <c>…SessionItemViewModel</c>）。
/// 这是实测抓到的：UIA 读到的条目名就是那串 record 转储。
/// </summary>
internal sealed record RailTool(string Icon, string Label, string Detail, Func<Task> Invoke)
{
    public override string ToString() => $"{Icon} {Label} — {Detail}";
}

/// <summary>
/// 「＋」侧边栏抽屉（QQNT 形态下桌宠悬浮窗的落点）。
///
/// 为什么放在聊天窗里：形态已经整体转向聊天室，桌宠的按钮（角色切换、状态感知、
/// 朗读文档、显示模式、清空记录）如果只留在悬浮窗里，就没人点得到了。
/// 做成"点 ＋ 才展开"的抽屉，是因为这些是**偶尔才用**的功能，
/// 常驻会和新消息抢版面。
///
/// 关键取舍：抽屉**只负责列出来**，每个动作转发给桌宠实例执行。
/// 两边各写一份实现迟早会行为不一致（比如状态感知的开关提示），
/// 所以桌宠那边把既有的 *_Click 核心逻辑抽成了无参方法（RailTools），这里只调用。
/// </summary>
public partial class ChatShellWindow
{
    private readonly ObservableCollection<RailTool> _railTools = [];
    private MainWindow? _petPanel;
    private bool _railOpen;

    private void InitializeRail()
    {
        RailToolList.ItemsSource = _railTools;
        RailToolList.SelectionChanged += RailToolList_SelectionChanged;
        RefreshRailTools();
    }

    private void RailToggle_Click(object sender, RoutedEventArgs e) => SetRailOpen(!_railOpen);

    private void RailClose_Click(object sender, RoutedEventArgs e) => SetRailOpen(false);

    private void SetRailOpen(bool open)
    {
        _railOpen = open;
        RailDrawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        RailToggleButton.Content = open ? "✕" : "＋";
        if (open)
            RefreshRailTools();
    }

    /// <summary>Esc 关抽屉。键盘可达性：不能只靠鼠标。</summary>
    private void RailRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _railOpen)
        {
            SetRailOpen(false);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 重建抽屉条目。**每次展开都重建**：状态感知的开关状态、桌宠是否已开
    /// 都会变，缓存下来就会显示旧值。
    /// </summary>
    private void RefreshRailTools()
    {
        _railTools.Clear();

        if (_petPanel is { IsLoaded: true } pet)
        {
            _railTools.Add(new RailTool("🙂", $"桌宠面板（{pet.RailCharacterName}）",
                pet.IsVisible ? "已在桌面显示，点一下收起" : "悬浮窗已隐藏，点一下唤出",
                () =>
                {
                    if (pet.IsVisible)
                        pet.RailSetDisplayMode(WindowDisplayMode.Hidden);
                    else
                        pet.RailSetDisplayMode(WindowDisplayMode.Full);
                    pet.Activate();
                    return Task.CompletedTask;
                }));

            _railTools.Add(new RailTool("🎭", "切换角色", $"当前：{pet.RailCharacterName}",
                () =>
                {
                    // 角色面板是桌宠自己的 Popup，嵌不过来，所以唤起它来切。
                    if (!pet.IsVisible)
                        pet.RailSetDisplayMode(WindowDisplayMode.Full);
                    pet.Activate();
                    pet.RailOpenCharacterPicker();
                    return Task.CompletedTask;
                }));

            _railTools.Add(new RailTool("👁", "应用状态感知",
                pet.RailAwarenessEnabled ? "已开启：读取前台进程与窗口标题" : "已关闭：不读取任何窗口信息",
                () =>
                {
                    pet.RailToggleAwareness();
                    RefreshRailTools();
                    return Task.CompletedTask;
                }));

            _railTools.Add(new RailTool("📖", "朗读文档",
                pet.RailDocumentReadingReady ? "选择 txt / docx，让她读给你听" : "运行时还没就绪",
                async () =>
                {
                    await pet.RailReadDocumentAsync();
                    RefreshRailTools();
                }));

            _railTools.Add(new RailTool("🗑", "清空该角色记录", "只清桌宠窗口那边的对话",
                () =>
                {
                    pet.RailClearConversation();
                    RefreshRailTools();
                    return Task.CompletedTask;
                }));

            _railTools.Add(new RailTool("◫", "桌宠显示模式", "完整 / 头像气泡 / 隐藏到托盘",
                () =>
                {
                    // 完整 → 头像气泡 → 隐藏 → 完整，循环三态。
                    var next = pet.RailDisplayMode switch
                    {
                        WindowDisplayMode.Full => WindowDisplayMode.Compact,
                        WindowDisplayMode.Compact => WindowDisplayMode.Hidden,
                        _ => WindowDisplayMode.Full,
                    };
                    pet.RailSetDisplayMode(next);
                    RefreshRailTools();
                    return Task.CompletedTask;
                }));
        }
        else
        {
            _railTools.Add(new RailTool("🙂", "打开桌宠面板",
                "在旁边起一个桌宠悬浮窗（共用同一套运行时）",
                () =>
                {
                    OpenPetPanel();
                    RefreshRailTools();
                    return Task.CompletedTask;
                }));
        }

        _railTools.Add(new RailTool("💬", "新建群聊", "选 2–8 位角色开一间群",
            () =>
            {
                SetRailOpen(false);
                NewGroup_Click(this, new RoutedEventArgs());
                return Task.CompletedTask;
            }));

        RailHint.Text = _petPanel is { IsLoaded: true }
            ? "桌宠与聊天窗共用同一套运行时：同一个角色不会起两份 Agent。"
            : "桌宠面板是为「陪伴」而存在的窗口，和这里的会话列表互不干扰。";
    }

    /// <summary>
    /// 起桌宠悬浮窗。**共用本窗口的 factory 与 TTS**——进程里只应有一个 TTS 引擎
    /// （它内部有参考音频缓存，开两份会互相踩），AgentPool 同理。
    /// </summary>
    private void OpenPetPanel()
    {
        if (_petPanel is { IsLoaded: true })
        {
            _petPanel.Activate();
            return;
        }

        var pet = new MainWindow(null, null, _factory, _sharedTts)
        {
            // 不设 Owner：设为 Owner 会被强制叠在本窗口之上，聊天窗就没法盖住它了；
            // 用户要的是两个平级窗口。相对位置在下面按聊天窗左边缘摆。
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        _petPanel = pet;
        pet.Closed += (_, _) =>
        {
            if (ReferenceEquals(_petPanel, pet))
                _petPanel = null;
            RefreshRailTools();
        };
        pet.Show();
        // 面板形态：收起它自己的聊天入口与隐藏/缩小按钮（显隐由侧边栏管）。
        pet.EnterPanelMode();
        PositionPetPanelBeside(pet);
        pet.Activate();
        RefreshRailTools();
    }

    /// <summary>把桌宠摆到聊天窗左侧；左边放不下就放右侧（多显示器/贴边时仍要看得见）。</summary>
    private void PositionPetPanelBeside(Window pet)
    {
        const double gap = 8;
        var work = SystemParameters.WorkArea;
        var petWidth = pet.ActualWidth > 0 ? pet.ActualWidth : pet.Width;
        if (double.IsNaN(petWidth) || petWidth <= 0)
            petWidth = 360;

        var left = Left - petWidth - gap;
        if (left < work.Left)
            left = Left + Width + gap;                    // 左边放不下，改放右边
        if (left + petWidth > work.Right)
            left = Math.Max(work.Left, work.Right - petWidth);  // 右边也放不下，就贴右边缘

        pet.Left = left;
        pet.Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - 200));
    }

    private void RailToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RailToolList.SelectedItem is not RailTool tool)
            return;
        // 立刻取消选中：这是个动作列表，不是选择列表，留着高亮会让人以为"选中了某项"。
        RailToolList.SelectedItem = null;

        _ = RunRailToolAsync(tool);
    }

    private async Task RunRailToolAsync(RailTool tool)
    {
        try
        {
            await tool.Invoke();
        }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("chat_shell.rail_tool", ex);
            SetStatus($"「{tool.Label}」没能完成：{ex.Message}");
        }
    }
}
