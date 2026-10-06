using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicTagRecover.Interfaces;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// DFF 原生标签（DIIN/COMT）管理窗口：单文件逐条编辑 + 多文件批量执行。
/// 关闭后返回本次发生过成功变更的文件路径集合，供主窗口刷新底层数据模型。
/// </summary>
public partial class DffNativeTagsWindow : Window
{
    private readonly DffNativeTagsViewModel _vm;

    private DffNativeTagsWindow(DffNativeTagsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>
    /// 模态打开管理窗口。
    /// </summary>
    /// <returns>发生过成功变更的文件路径集合（无变更返回空集合）。</returns>
    public static HashSet<string> Show(IReadOnlyList<string> dffFilePaths, ILoggerService logger)
    {
        var vm = new DffNativeTagsViewModel(dffFilePaths, logger);
        var win = new DffNativeTagsWindow(vm) { Owner = Application.Current.MainWindow };
        win.ShowDialog();
        return vm.ChangedFiles;
    }

    private void OnDiinDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DiinGrid.SelectedItem is DiinFieldRowViewModel row)
            _vm.EditDiinCommand.Execute(row);
    }

    private void OnCommtDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CommtGrid.SelectedItem is CommtCommentRowViewModel row)
            _vm.EditCommtCommand.Execute(row);
    }
}
