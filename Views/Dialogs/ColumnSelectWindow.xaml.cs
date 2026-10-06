using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MusicTagRecover.Models;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// 自定义显示列对话框：勾选列是否显示、调整列顺序、恢复默认设置。
/// 构造时传入的列对象会被直接修改；取消时回滚 IsShow / DisplayIndex。
/// </summary>
public partial class ColumnSelectWindow : Window
{
    private readonly List<ColumnHeaderInfo> _columns;

    /// <summary>取消时用于回滚的初始状态快照（IsShow、DisplayIndex）</summary>
    private readonly Dictionary<ColumnHeaderInfo, (bool IsShow, int DisplayIndex)> _snapshot = new();

    /// <summary>当前列表顺序的包装项</summary>
    private readonly ObservableCollection<ColumnHeaderItemViewModel> _items = new();

    /// <summary>确定后返回当前顺序的同一批列对象（DisplayIndex 已重排）</summary>
    public IReadOnlyList<ColumnHeaderInfo> Result { get; private set; }

    public ColumnSelectWindow(List<ColumnHeaderInfo> columns)
    {
        InitializeComponent();

        _columns = columns ?? throw new ArgumentNullException(nameof(columns));
        foreach (var column in _columns)
            _snapshot[column] = (column.IsShow, column.DisplayIndex);

        // 初始按 DisplayIndex 排序展示
        foreach (var column in _columns.OrderBy(c => c.DisplayIndex))
            _items.Add(new ColumnHeaderItemViewModel(column));

        ColumnsListBox.ItemsSource = _items;
        Result = _columns;

        // 未点确定关闭时回滚模型改动（勾选/顺序）
        Closed += (_, _) =>
        {
            if (DialogResult == true) return;
            foreach (var pair in _snapshot)
            {
                pair.Key.IsShow = pair.Value.IsShow;
                pair.Key.DisplayIndex = pair.Value.DisplayIndex;
            }
        };
    }

    /// <summary>上移选中列</summary>
    private void MoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        var index = ColumnsListBox.SelectedIndex;
        if (index <= 0) return;
        _items.Move(index, index - 1);
        ColumnsListBox.SelectedIndex = index - 1;
    }

    /// <summary>下移选中列</summary>
    private void MoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        var index = ColumnsListBox.SelectedIndex;
        if (index < 0 || index >= _items.Count - 1) return;
        _items.Move(index, index + 1);
        ColumnsListBox.SelectedIndex = index + 1;
    }

    /// <summary>恢复默认：按 AllColumnNames 顺序排列，并重置勾选/宽度/对齐</summary>
    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var byName = _columns.ToDictionary(c => c.Name);
        var ordered = new List<ColumnHeaderInfo>();

        // 先按系统内置列名顺序输出
        foreach (var name in ColumnHeaderInfo.AllColumnNames)
        {
            if (!byName.TryGetValue(name, out var column)) continue;
            column.IsShow = true;
            column.Width = ColumnHeaderInfo.DefaultWidths.TryGetValue(name, out var width) ? width : column.Width;
            column.TextAlign = ColumnHeaderInfo.DefaultAlignments.TryGetValue(name, out var align)
                ? align
                : ColumnTextAlign.Left;
            ordered.Add(column);
        }

        // 内置列表之外的自定义列追加在末尾
        foreach (var column in _columns.Where(c => !ordered.Contains(c)))
            ordered.Add(column);

        _items.Clear();
        foreach (var column in ordered)
            _items.Add(new ColumnHeaderItemViewModel(column));
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        // 按当前顺序重排 DisplayIndex，产出结果列表（同一批对象引用）
        var result = new List<ColumnHeaderInfo>(_items.Count);
        for (var i = 0; i < _items.Count; i++)
        {
            var column = _items[i].Column;
            column.DisplayIndex = i;
            result.Add(column);
        }

        Result = result;
        DialogResult = true;
    }
}
