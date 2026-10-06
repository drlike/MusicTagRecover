using System.Windows;
using MusicTagRecover.Models.Compare;
using MusicTagRecover.Services.Compare;
using MusicTagRecover.ViewModels.Compare;

namespace MusicTagRecover.Views.Compare;

/// <summary>
/// 删除历史窗口（由 CompareWindow 非模态打开，Owner 为 CompareWindow）。
/// 代码隐藏负责：首次加载自动查询、多选记录提取与还原、关闭。
/// </summary>
public partial class DeleteHistoryWindow : Window
{
    private readonly DeleteHistoryViewModel _viewModel;

    // 注：RecycleBinService 是静态类，无法作为构造参数注入；还原逻辑在 ViewModel 中直接调用其静态方法。
    public DeleteHistoryWindow(CompareDatabaseService dbService)
    {
        InitializeComponent();
        _viewModel = new DeleteHistoryViewModel(dbService);
        DataContext = _viewModel;
    }

    /// <summary>窗口首次加载时拉取一次历史记录。</summary>
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.QueryLogsAsync();
    }

    /// <summary>从回收站还原选中记录（多选由 DataGrid.SelectedItems 获取）。</summary>
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var rows = LogsGrid.SelectedItems.Cast<CompareDeleteLogRecord>().ToList();
        await _viewModel.RestoreSelectedAsync(rows);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
