using CommunityToolkit.Mvvm.ComponentModel;
using MusicTagRecover.Models;

namespace MusicTagRecover.ViewModels.Dialogs;

/// <summary>「管理目录」列表项：勾选 + 目录信息 + 当前是否仍存在。</summary>
public partial class DirectoryEntryItemViewModel : ObservableObject
{
    public DirectoryHistoryEntry Entry { get; }

    [ObservableProperty] private bool _isChecked;

    public DirectoryEntryItemViewModel(DirectoryHistoryEntry entry)
    {
        Entry = entry;
    }

    public string Path => Entry.Path;

    /// <summary>目录当前是否仍存在于磁盘上。</summary>
    public bool Exists => Directory.Exists(Entry.Path);

    /// <summary>添加时间（本地时区显示）。</summary>
    public string AddedText
        => new DateTime(Entry.AddedTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>最近使用时间；从未使用显示 "—"。</summary>
    public string LastUsedText => Entry.LastUsedTicks > 0
        ? new DateTime(Entry.LastUsedTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "—";

    /// <summary>子行说明文字：子目录设置 + 添加/使用时间 + 丢失标记。</summary>
    public string DetailText
    {
        get
        {
            var sub = Entry.IncludeSubDir ? "含子目录" : "仅当前目录";
            var missing = Exists ? "" : "（目录已不存在）";
            return $"{sub}    添加：{AddedText}    最近切换：{LastUsedText}{missing}";
        }
    }
}
