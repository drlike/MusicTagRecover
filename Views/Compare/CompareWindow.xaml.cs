using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Models.Compare;
using MusicTagRecover.Services.Compare;
using MusicTagRecover.ViewModels.Compare;

namespace MusicTagRecover.Views.Compare;

/// <summary>
/// 文件比对窗口。
/// 代码隐藏仅承担：DataGrid/ListBox 多选结果的提取、双击定位资源管理器、
/// 删除历史子窗口的单例复用（非模态 Show，Owner 为本窗口）。
/// </summary>
public partial class CompareWindow : Window
{
    private readonly CompareViewModel _viewModel;
    private readonly CompareDatabaseService _dbService;

    /// <summary>删除历史窗口实例：打开期间复用，关闭后置空。</summary>
    private DeleteHistoryWindow? _deleteHistoryWindow;

    public CompareWindow(CompareScanService scanService, CompareDatabaseService dbService)
    {
        InitializeComponent();
        _dbService = dbService;
        _viewModel = new CompareViewModel(scanService, dbService);
        DataContext = _viewModel;
    }

    /// <summary>双击结果行：在资源管理器中打开并选中源文件（待删除方）。</summary>
    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not CompareMatchResult row)
            return;
        if (string.IsNullOrWhiteSpace(row.SourcePath))
            return;

        try
        {
            // explorer /select,"路径"：打开所在目录并选中该文件
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{row.SourcePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            WpfUI.Warning("无法打开源文件位置：\n" + row.SourcePath + "\n\n" + ex.Message);
        }
    }

    /// <summary>删除选中行对应的源文件（多选由 DataGrid.SelectedItems 获取；目标文件保留）。</summary>
    private async void DeleteSources_Click(object sender, RoutedEventArgs e)
    {
        var rows = ResultsGrid.SelectedItems.Cast<CompareMatchResult>().ToList();
        await _viewModel.DeleteSourcesAsync(rows);
    }

    /// <summary>删除选中的空目录。</summary>
    private async void DeleteEmptyDirs_Click(object sender, RoutedEventArgs e)
    {
        var dirs = EmptyDirsList.SelectedItems.Cast<string>().ToList();
        await _viewModel.DeleteEmptyDirsAsync(dirs);
    }

    /// <summary>
    /// 打开删除历史窗口：实例单例复用、非模态、Owner 指向本窗口；
    /// 已打开则直接激活前置，关闭后再打开会重建实例。
    /// </summary>
    private void OpenDeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        // RecycleBinService 为静态类无法作为构造参数传入，DeleteHistoryWindow 仅需 dbService
        if (_deleteHistoryWindow == null)
        {
            _deleteHistoryWindow = new DeleteHistoryWindow(_dbService)
            {
                Owner = this,
            };
            _deleteHistoryWindow.Closed += (_, _) => _deleteHistoryWindow = null;
            _deleteHistoryWindow.Show();
        }
        else
        {
            if (_deleteHistoryWindow.WindowState == WindowState.Minimized)
                _deleteHistoryWindow.WindowState = WindowState.Normal;
            _deleteHistoryWindow.Activate();
        }
    }
}
