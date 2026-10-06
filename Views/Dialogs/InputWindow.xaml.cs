using System.Windows;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>单行文本输入对话框。</summary>
public partial class InputWindow : Window
{
    private InputWindow(string prompt, string title, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = defaultValue;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>
    /// 模态显示输入框；确定返回输入文本（可能为空串），取消返回 null。
    /// </summary>
    public static string? Ask(string title, string prompt, string defaultValue = "")
    {
        var win = new InputWindow(prompt, title, defaultValue)
        {
            Owner = Application.Current.MainWindow
        };
        return win.ShowDialog() == true ? win.InputBox.Text.Trim() : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
