using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MusicTagRecover.ViewModels;

/// <summary>
/// 标签面板 XAML 用的静态转换器单例（x:Static 直接引用，免去每处声明资源）。
/// </summary>
public static class TagEditConverters
{
    /// <summary>bool → Visibility（已修改标记：true 显示）</summary>
    public static readonly IValueConverter ModifiedVisibility = new BoolVisible();

    /// <summary>无封面占位：HasCover=false 时显示"无封面"</summary>
    public static readonly IValueConverter NoCoverVisibility = new InverseBoolVisible();

    private sealed class BoolVisible : IValueConverter
    {
        public object Convert(object? value, Type t, object? p, CultureInfo c)
            => value is true ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
            => value is Visibility.Visible;
    }

    private sealed class InverseBoolVisible : IValueConverter
    {
        public object Convert(object? value, Type t, object? p, CultureInfo c)
            => value is true ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
            => value is not Visibility.Visible;
    }
}
