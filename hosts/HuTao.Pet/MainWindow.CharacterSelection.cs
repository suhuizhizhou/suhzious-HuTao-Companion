using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HuTao.Pet;

/// <summary>角色选择器与角色切换队列。播放中的台词结束后才真正切换。</summary>
public partial class MainWindow
{
    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        RenderCharacterPicker();
        CharacterPickerPopup.IsOpen = !CharacterPickerPopup.IsOpen;
    }

    private void RenderCharacterPicker()
    {
        CharacterPickerPanel.Children.Clear();
        for (var index = 0; index < Characters.Length; index++)
        {
            var character = Characters[index];
            var selected = index == _currentIndex;
            var accent = character.Theme.Accent;

            var avatar = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                Background = new SolidColorBrush(Color.FromArgb(35, accent.R, accent.G, accent.B)),
                Child = new TextBlock
                {
                    Text = character.Emoji,
                    FontSize = 21,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var labels = new StackPanel { Margin = new Thickness(10, 0, 4, 0) };
            labels.Children.Add(new TextBlock
            {
                Text = character.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = new SolidColorBrush(character.Theme.Text),
            });
            labels.Children.Add(new TextBlock
            {
                Text = character.Title,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Color.FromArgb(175,
                    character.Theme.Text.R, character.Theme.Text.G, character.Theme.Text.B)),
            });

            var state = new Border
            {
                Padding = new Thickness(7, 3, 7, 3),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(
                    selected ? (byte)42 : (byte)15, accent.R, accent.G, accent.B)),
                Child = new TextBlock
                {
                    Text = selected ? "✓ 当前" : "选择",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(character.Theme.AccentDark),
                },
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(labels, 1);
            Grid.SetColumn(state, 2);
            row.Children.Add(avatar);
            row.Children.Add(labels);
            row.Children.Add(state);

            var button = new Button
            {
                Tag = index,
                Content = row,
                Style = (Style)CharacterPickerBorder.FindResource("CharacterOptionButtonStyle"),
                Background = new SolidColorBrush(Color.FromArgb(
                    selected ? (byte)28 : (byte)4, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(
                    selected ? (byte)125 : (byte)25, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(selected ? 1.5 : 1),
            };
            button.Click += CharacterOption_Click;
            CharacterPickerPanel.Children.Add(button);
        }
    }

    private void CharacterOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index })
            return;

        CharacterPickerPopup.IsOpen = false;
        if (index == _currentIndex && !_characterSwitchLoopRunning)
            return;

        _pendingCharacterIndex = index;
        if (!_characterSwitchLoopRunning)
            _ = RunCharacterSwitchLoopAsync();
    }

    private async Task RunCharacterSwitchLoopAsync()
    {
        _characterSwitchLoopRunning = true;
        try
        {
            while (_pendingCharacterIndex is not null && !_cts.IsCancellationRequested)
            {
                var next = _pendingCharacterIndex.Value;
                _pendingCharacterIndex = null;
                while (_talking && !_cts.IsCancellationRequested)
                    await Task.Delay(80, _cts.Token);

                if (next != _currentIndex)
                    await LoadCharacterAsync(next);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _characterSwitchLoopRunning = false;
        }
    }

    private async Task SayGreetingAsync(CharacterConfig character, CancellationToken ct)
    {
        _talking = true;
        try
        {
            var audioPath = Path.Combine(_repoRoot!, character.GreetingAudio);
            var usableAudio = File.Exists(audioPath) ? audioPath : null;
            AddBubble(character.Greeting, isUser: false, usableAudio, record: false);
            if (usableAudio is not null)
                await PlayAudioAsync(usableAudio, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _scheduler?.NotifyConversationActivity();
            _talking = false;
        }
    }
}
