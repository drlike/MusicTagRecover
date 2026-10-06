using System.Windows;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// “文件名 ⇄ 标签”联动对话框。
/// forcedMode 非空时锁定对应页（隐藏 Tab 头）；确定时校验模式/正则并持久化。
/// </summary>
public partial class FilenameRelationWindow : Window
{
    private readonly FilenameRelationViewModel _viewModel;

    public FilenameRelationWindow(ISettingsService settings, FilenameRelationMode? forcedMode)
    {
        InitializeComponent();
        _viewModel = new FilenameRelationViewModel(settings, forcedMode);
        DataContext = _viewModel;

        // 锁定模式下窗口标题明确当前功能
        Title = forcedMode switch
        {
            FilenameRelationMode.ChangeTags => "文件名 → 标签",
            FilenameRelationMode.RenameFiles => "标签 → 文件名",
            _ => "文件名与标签联动",
        };
    }

    /// <summary>确定后填充的选项（取消时为 null）</summary>
    public FilenameRelationOptions? Options => _viewModel.Options;

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryConfirm(out var error))
        {
            MessageBox.Show(this, error, "模式无效",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
