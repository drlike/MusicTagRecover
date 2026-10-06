using System.Globalization;
using System.Windows;
using MusicTagRecover.Utils;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// COMT 原生注释编辑表单弹窗。
/// AskNew / AskEdit 返回 <see cref="DffCommtComment"/>；取消返回 null。
/// </summary>
public partial class DffCommtEditWindow : Window
{
    private DffCommtEditWindow(DffCommtComment? source)
    {
        InitializeComponent();
        var c = source ?? NowComment();
        YearBox.Text = c.Year.ToString(CultureInfo.InvariantCulture);
        MonthBox.Text = c.Month.ToString(CultureInfo.InvariantCulture);
        DayBox.Text = c.Day.ToString(CultureInfo.InvariantCulture);
        HourBox.Text = c.Hour.ToString(CultureInfo.InvariantCulture);
        MinuteBox.Text = c.Minute.ToString(CultureInfo.InvariantCulture);
        SecondBox.Text = c.Second.ToString(CultureInfo.InvariantCulture);
        TypeBox.Text = c.CommentType + " - " + DescribeType(c.CommentType);
        RefBox.Text = c.CommentReference.ToString(CultureInfo.InvariantCulture);
        TextBox.Text = c.Text ?? "";
        Loaded += (_, _) => TextBox.Focus();
    }

    /// <summary>追加注释表单（默认当前时间）。</summary>
    public static DffCommtComment? AskNew(string ownerFile)
    {
        var win = new DffCommtEditWindow(null) { Owner = Application.Current.MainWindow };
        return win.ShowDialog() == true ? win.Build() : null;
    }

    /// <summary>修改既有注释表单。</summary>
    public static DffCommtComment? AskEdit(string ownerFile, DffCommtComment source)
    {
        var win = new DffCommtEditWindow(source) { Owner = Application.Current.MainWindow };
        return win.ShowDialog() == true ? win.Build() : null;
    }

    private static DffCommtComment NowComment()
    {
        var n = DateTime.Now;
        return new DffCommtComment
        {
            Year = (ushort)n.Year, Month = (byte)n.Month, Day = (byte)n.Day,
            Hour = (byte)n.Hour, Minute = (byte)n.Minute, Second = (byte)n.Second,
        };
    }

    private void OnUseNow(object sender, RoutedEventArgs e)
    {
        var c = NowComment();
        YearBox.Text = c.Year.ToString(CultureInfo.InvariantCulture);
        MonthBox.Text = c.Month.ToString(CultureInfo.InvariantCulture);
        DayBox.Text = c.Day.ToString(CultureInfo.InvariantCulture);
        HourBox.Text = c.Hour.ToString(CultureInfo.InvariantCulture);
        MinuteBox.Text = c.Minute.ToString(CultureInfo.InvariantCulture);
        SecondBox.Text = c.Second.ToString(CultureInfo.InvariantCulture);
    }

    private DffCommtComment Build()
    {
        int type = ParseLeadingInt(TypeBox.Text, 0);
        int refVal = ParseInt(RefBox.Text, 0);
        return new DffCommtComment
        {
            Year = (ushort)ParseInt(YearBox.Text, 0),
            Month = (byte)ParseInt(MonthBox.Text, 0),
            Day = (byte)ParseInt(DayBox.Text, 0),
            Hour = (byte)ParseInt(HourBox.Text, 0),
            Minute = (byte)ParseInt(MinuteBox.Text, 0),
            Second = (byte)ParseInt(SecondBox.Text, 0),
            CommentType = (byte)Math.Clamp(type, 0, 255),
            CommentReference = (ushort)Math.Clamp(refVal, 0, 65535),
            Text = TextBox.Text ?? "",
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryValidate(out var error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (string.IsNullOrWhiteSpace(TextBox.Text) &&
            MessageBox.Show(this, "注释内容为空，是否继续写入空注释？", "内容为空",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        DialogResult = true;
    }

    private bool TryValidate(out string error)
    {
        error = "";
        if (!ushort.TryParse(YearBox.Text, out var year) || year > 9999)
            error = "年份必须为 0-9999 的整数。";
        else if (!RangeByte(MonthBox.Text, 0, 12, out _)) error = "月份必须为 0-12。";
        else if (!RangeByte(DayBox.Text, 0, 31, out _)) error = "日期必须为 0-31。";
        else if (!RangeByte(HourBox.Text, 0, 23, out _)) error = "小时必须为 0-23。";
        else if (!RangeByte(MinuteBox.Text, 0, 59, out _)) error = "分钟必须为 0-59。";
        else if (!RangeByte(SecondBox.Text, 0, 60, out _)) error = "秒必须为 0-60。";
        else if (!int.TryParse(RefBox.Text, out var refV) || refV is < 0 or > 65535)
            error = "引用编号必须为 0-65535 的整数。";
        return error == "";
    }

    private static bool RangeByte(string text, int min, int max, out byte value)
    {
        value = 0;
        if (!int.TryParse(text, out var v) || v < min || v > max) return false;
        value = (byte)v;
        return true;
    }

    private static int ParseInt(string text, int defaultValue)
        => int.TryParse(text?.Trim(), out var v) ? v : defaultValue;

    private static int ParseLeadingInt(string text, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(text)) return defaultValue;
        var digits = new string(text.Trim().TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var v) ? v : defaultValue;
    }

    private static string DescribeType(byte type) => type switch
    {
        0 => "通用注释",
        1 => "唱片信息",
        2 => "制作信息",
        3 => "抓轨/来源信息",
        _ => "自定义类型",
    };
}
