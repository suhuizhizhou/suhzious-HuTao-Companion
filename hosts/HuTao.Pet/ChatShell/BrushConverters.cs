using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HuTao.Pet.ChatShell;

/// <summary>
/// ARGB 字符串（如 <c>#FFE08A3C</c>）→ <see cref="SolidColorBrush"/>。
///
/// 会话的 accent 在 Core 里是字符串：Core 不许引用 WPF（会被
/// <c>dependency_direction_acyclic</c> 判红），所以颜色只能在宿主这一侧还原。
/// 解析失败返回中性灰而不是抛——一个坏色值不该让整个侧栏渲染不出来。
/// </summary>
public sealed class ArgbToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Fallback = new(Color.FromRgb(0x9A, 0x9A, 0x9A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new SolidColorBrush(Parse(value as string));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>把 <c>#AARRGGBB</c> / <c>#RRGGBB</c> 解析成颜色；失败给中性灰。</summary>
    public static Color Parse(string? argb)
    {
        if (string.IsNullOrWhiteSpace(argb))
            return Fallback.Color;
        try
        {
            return (Color)ColorConverter.ConvertFromString(argb.Trim());
        }
        catch (FormatException)
        {
            return Fallback.Color;
        }
    }

    public static SolidColorBrush Brush(string? argb) => new(Parse(argb));
}

/// <summary>把一个颜色按给定透明度做成画刷，用于"浅色底 + accent 文字"这类组合。</summary>
public sealed class AccentTintConverter : IValueConverter
{
    public double Alpha { get; set; } = 0.14;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = ArgbToBrushConverter.Parse(value as string);
        var alpha = parameter is string text && double.TryParse(text, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : Alpha;
        return new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255),
            color.R, color.G, color.B));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
