using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// 标签历史窗口。new TagHistoryWindow(historyService, filePath, records).ShowDialog()，
/// 返回 true 时读取 SelectedRecord / SelectedCoverData；清空全部历史后返回 false。
/// </summary>
public partial class TagHistoryWindow : Window
{
    private readonly TagHistoryViewModel _viewModel;

    /// <summary>对话框返回 true 时为要恢复的历史记录。</summary>
    public TagHistoryRecord? SelectedRecord => _viewModel.SelectedReturn;

    /// <summary>对话框返回 true 时为要恢复的封面字节（无封面为 null）。</summary>
    public byte[]? SelectedCoverData => _viewModel.SelectedCoverData;

    public TagHistoryWindow(ITagHistoryService historyService, string filePath,
        List<TagHistoryRecord> records)
    {
        InitializeComponent();
        _viewModel = new TagHistoryViewModel(historyService, filePath, records);
        _viewModel.RequestClose += OnRequestClose;
        DataContext = _viewModel;
        Owner = Application.Current?.MainWindow;
        Title = $"标签历史 - {System.IO.Path.GetFileName(filePath)}";
    }

    /// <summary>双击列表项等同于"恢复此记录"。</summary>
    private void ListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem != null
            && _viewModel.RestoreCommand.CanExecute(null))
        {
            _viewModel.RestoreCommand.Execute(null);
        }
    }

    private void OnRequestClose(bool? dialogResult)
    {
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            DialogResult = dialogResult;
        else
            Close();
    }
}
