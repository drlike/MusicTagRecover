using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// 批量自动匹配标签设置对话框：逐字段配置启用/写入模式/覆盖，
/// 以及并发线程数与伴奏歌词过滤。
/// </summary>
public partial class AutoMatchTagsWindow : Window
{
    private readonly AutoMatchTagsViewModel _viewModel;

    public AutoMatchTagsWindow(ISettingsService settings)
    {
        InitializeComponent();
        _viewModel = new AutoMatchTagsViewModel(settings);
        DataContext = _viewModel;

        // Ctrl+A：全选/取消全选所有字段（焦点在文本框时不拦截，保留文本框原生全选）
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.A || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (Keyboard.FocusedElement is TextBox) return;
        _viewModel.ToggleAllCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>当前配置的选项（确定时已持久化到设置服务）</summary>
    public AutoMatchOptions Options => _viewModel.Options;

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryCommit(out var error))
        {
            MessageBox.Show(this, error, "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
