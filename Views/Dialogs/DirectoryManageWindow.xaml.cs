using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Models;
using MusicTagRecover.Services;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// 「管理目录」对话框：列出数据库中的历史工作目录，支持多选；
/// 「切换工作目录」按勾选项返回（Result），「删除选中记录」仅清库记录。
/// </summary>
public partial class DirectoryManageWindow : Window
{
    private readonly DirectoryHistoryService _history;
    private readonly ObservableCollection<DirectoryEntryItemViewModel> _items = new();

    /// <summary>用户确认切换的目录条目（保留各自的子目录勾选状态）。</summary>
    public IReadOnlyList<DirectoryHistoryEntry> Result { get; private set; }
        = Array.Empty<DirectoryHistoryEntry>();

    public DirectoryManageWindow(DirectoryHistoryService history)
    {
        InitializeComponent();
        _history = history;
        Reload();
        DirsList.ItemsSource = _items;
    }

    private void Reload()
    {
        _items.Clear();
        foreach (var entry in _history.GetAll())
            _items.Add(new DirectoryEntryItemViewModel(entry));
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.IsChecked = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.IsChecked = false;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var checkedItems = _items.Where(i => i.IsChecked).ToList();
        if (checkedItems.Count == 0)
        {
            WpfUI.Info("请先勾选要删除的目录记录。");
            return;
        }

        var names = string.Join("\n", checkedItems.Select(i => i.Path));
        if (WpfUI.YesNo($"确定删除以下 {checkedItems.Count} 条历史目录记录吗？\n\n{names}\n\n"
                          + "仅删除软件内的历史记录，不会删除磁盘上的文件夹。")
            != MessageBoxResult.Yes) return;

        _history.Delete(checkedItems.Select(i => i.Path));
        Reload();
    }

    private void OnSwitch(object sender, RoutedEventArgs e)
    {
        var checkedItems = _items.Where(i => i.IsChecked).ToList();
        if (checkedItems.Count == 0)
        {
            WpfUI.Info("请先勾选要切换的目录。");
            return;
        }

        // 已不存在的目录不允许切换，避免加载空列表
        var missing = checkedItems.Where(i => !i.Exists).ToList();
        if (missing.Count > 0)
        {
            WpfUI.Warning("以下勾选目录已不存在，请取消勾选或删除记录：\n\n"
                          + string.Join("\n", missing.Select(i => i.Path)));
            return;
        }

        Result = checkedItems.Select(i => i.Entry).ToList();
        DialogResult = true;
        Close();
    }
}
