using System.Diagnostics;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HuTao.Agent.Core.DocumentReading;
using HuTao.Agent.Core.Diagnostics;

namespace HuTao.Pet;

/// <summary>文档朗读 UI 适配器：文件选择、进度旁白和结果操作。</summary>
public partial class MainWindow
{
    private async void ReadDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_talking || _documentReader is null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "选择要朗读的文档",
            Filter = "文本文档 (*.txt;*.docx)|*.txt;*.docx|纯文本 (*.txt)|*.txt|Word 文档 (*.docx)|*.docx",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        await StartDocumentReadingAsync(dialog.FileName);
    }

    private async Task StartDocumentReadingAsync(string path)
    {
        if (_documentReader is null || _talking)
            return;

        _talking = true;
        try
        {
            AddBubble("好哦，我来帮你念这份稿子啦~", isUser: false, audio: null, record: false);
            AddNarrationBubble($"（旁白：{CurrentCharacter.Name}正在整理稿件，马上开始有感情地念稿子~）");
            var result = await _documentReader.ReadAsync(path, _cts.Token);
            if (result.Success)
            {
                AddBubble("我完成啦，MP3 已经做好，快去听听~", isUser: false, audio: null, record: false);
                AddDocumentResultBubble(result);
            }
            else
            {
                LocalDiagnosticLog.Default.Write("document.incomplete", new InvalidOperationException(result.Error));
                AddBubble("这份稿子还没念完，等会儿再接着念给你听，好吗？", isUser: false, record: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("document.read", ex);
            AddBubble("这份稿子还没念完，等会儿再接着念给你听，好吗？", isUser: false, record: false);
        }
        finally
        {
            _scheduler?.NotifyConversationActivity();
            _talking = false;
        }
    }

    private void ReportDocumentProgress(string message)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        _ = Dispatcher.InvokeAsync(() => AddNarrationBubble(message));
    }

    private void AddNarrationBubble(string text)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(30,
                CurrentTheme.Accent.R, CurrentTheme.Accent.G, CurrentTheme.Accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(95,
                CurrentTheme.Accent.R, CurrentTheme.Accent.G, CurrentTheme.Accent.B)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(10, 5, 8, 5),
            Margin = new Thickness(12, 2, 12, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Foreground = new SolidColorBrush(CurrentTheme.ThinkingText),
            },
        };
        BubblePanel.Children.Add(border);
        BubbleScroll.ScrollToEnd();
        UpdateCompactBubble(text);
    }

    private void AddDocumentResultBubble(DocumentReadingResult result)
    {
        if (string.IsNullOrWhiteSpace(result.OutputMp3))
            return;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"共 {result.ChunkCount} 个朗读片段，已合并为 MP3。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(CurrentTheme.Text),
        });
        var path = result.OutputMp3;
        var openButton = new Button
        {
            Content = "♫ 打开 MP3",
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(CurrentTheme.Replay),
            Foreground = new SolidColorBrush(CurrentTheme.AccentDark),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        openButton.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                LocalDiagnosticLog.Default.Write("document.open_audio", ex);
                openButton.ToolTip = "暂时无法打开文件，请检查默认音频播放器。";
            }
        };
        panel.Children.Add(openButton);

        var border = new Border
        {
            Background = new SolidColorBrush(CurrentTheme.AssistantBubble),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(4, 3, 4, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 270,
            Child = panel,
        };
        BubblePanel.Children.Add(border);
        BubbleScroll.ScrollToEnd();
        UpdateCompactBubble($"朗读完成，共 {result.ChunkCount} 个片段。快去听听吧~");
    }
}
