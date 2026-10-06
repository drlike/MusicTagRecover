using CommunityToolkit.Mvvm.ComponentModel;
using MusicTagRecover.Models;

namespace MusicTagRecover.ViewModels.Dialogs;

/// <summary>
/// 列头包装项：ColumnHeaderInfo 本身不实现 INPC，
/// 对话框用此包装对象承载勾选状态与显示名，并持有原始模型引用。
/// </summary>
public sealed class ColumnHeaderItemViewModel : ObservableObject
{
    /// <summary>被包装的原始列头对象（同一引用）</summary>
    public ColumnHeaderInfo Column { get; }

    /// <summary>列表中显示的文本：优先取中文显示名，找不到则回退为内部列名</summary>
    public string DisplayName { get; }

    public ColumnHeaderItemViewModel(ColumnHeaderInfo column)
    {
        Column = column;
        DisplayName = ColumnHeaderInfo.DisplayNames.TryGetValue(column.Name, out var text)
            ? text
            : column.Name;
    }

    /// <summary>是否显示该列（直接写回原始模型）</summary>
    public bool IsShow
    {
        get => Column.IsShow;
        set
        {
            if (Column.IsShow == value) return;
            Column.IsShow = value;
            OnPropertyChanged();
        }
    }
}
