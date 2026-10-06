using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MusicTagRecover.Models;

namespace MusicTagRecover.Infrastructure;

/// <summary>bool → Visibility（false 折叠）</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>取反 bool → Visibility</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>非 null → Visible</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value != null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>列对齐枚举 → WPF TextAlignment</summary>
public sealed class ColumnTextAlignConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            ColumnTextAlign.Center => TextAlignment.Center,
            ColumnTextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字节数 → 人类可读大小</summary>
public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        long bytes = value switch
        {
            long l => l,
            int i => i,
            _ => 0,
        };
        return FormatSize(bytes);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{size:0.##} {units[u]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>毫秒时长 → mm:ss / h:mm:ss</summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        long ms = value switch
        {
            long l => l,
            double d => (long)d,
            int i => i,
            _ => 0,
        };
        return FormatDuration(ms);
    }

    public static string FormatDuration(long ms)
    {
        if (ms <= 0) return "";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>枚举值与 RadioButton.IsChecked 互转（ConverterParameter=枚举名）</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value != null && parameter != null && value.ToString() == parameter.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter != null ? Enum.Parse(targetType, parameter.ToString()!)
           : Binding.DoNothing;
}

/// <summary>非空字符串 → true（用于"有歌词/有封面"标记列）</summary>
public sealed class HasTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrEmpty(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
