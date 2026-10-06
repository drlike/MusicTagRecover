using System.Windows;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// “关于”对话框：展示应用名称、版本、简介与技术栈。
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        // 版本号从程序集读取（csproj 中 Version=0.9.0）
        VersionRun.Text = AppInfo.VersionString;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
