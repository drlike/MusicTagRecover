using System.Threading;
using System.Windows;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// 批量自动匹配标签的进度对话框（纯 code-behind）。
/// SetProgress / SetFile 均通过 Dispatcher.Invoke 保证跨线程安全。
/// </summary>
public partial class AutoMatchProgressWindow : Window
{
    private readonly int _total;
    private readonly CancellationTokenSource _cts;

    public AutoMatchProgressWindow(int total, CancellationTokenSource cts)
    {
        InitializeComponent();

        _total = total;
        _cts = cts ?? throw new ArgumentNullException(nameof(cts));

        ProgressBar.Maximum = total <= 0 ? 1 : total;
        ProgressTextBlock.Text = $"正在处理 0 / {_total}";
    }

    /// <summary>更新已完成数量（可从工作线程调用）</summary>
    public void SetProgress(int done)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = Math.Clamp(done, 0, ProgressBar.Maximum);
            ProgressTextBlock.Text = $"正在处理 {done} / {_total}";
        });
    }

    /// <summary>更新当前处理的文件名（可从工作线程调用）</summary>
    public void SetFile(string name)
    {
        Dispatcher.Invoke(() => FileNameTextBlock.Text = name);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // 请求取消并禁用按钮，避免重复点击
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在取消…";
    }
}
