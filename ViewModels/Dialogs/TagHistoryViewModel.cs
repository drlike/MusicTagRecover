using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;

namespace MusicTagRecover.ViewModels.Dialogs;

/// <summary>历史记录列表条目：时间 + 标题摘要。</summary>
public sealed class HistoryRecordItem
{
    public TagHistoryRecord Record { get; }

    public HistoryRecordItem(TagHistoryRecord record) => Record = record;

    /// <summary>列表显示文本：MM-dd HH:mm + 艺术家 - 标题摘要。</summary>
    public string Display
    {
        get
        {
            var r = Record;
            var summary = string.Join(" - ",
                new[] { r.Artist, r.Title }.Where(t => !string.IsNullOrWhiteSpace(t)));
            if (string.IsNullOrWhiteSpace(summary)) summary = Path.GetFileName(r.FilePath);
            return $"{r.CreateTime:MM-dd HH:mm}  {summary}";
        }
    }
}

/// <summary>
/// 标签历史对话框 VM：左侧历史记录列表，右侧全部文本字段与封面，
/// 支持恢复选中记录、删除单条、清空全部历史。
/// </summary>
public sealed partial class TagHistoryViewModel : ObservableObject
{
    private readonly ITagHistoryService _historyService;

    [ObservableProperty] private HistoryRecordItem? _selectedItem;
    [ObservableProperty] private TagHistoryRecord? _selectedRecord;
    [ObservableProperty] private BitmapSource? _coverImage;
    [ObservableProperty] private bool _hasCover;
    [ObservableProperty] private bool _isDetailLoading;

    private byte[]? _currentCoverData;

    /// <summary>窗口返回值：要恢复的历史记录。</summary>
    public TagHistoryRecord? SelectedReturn { get; private set; }

    /// <summary>窗口返回值：要恢复的封面字节。</summary>
    public byte[]? SelectedCoverData { get; private set; }

    public event Action<bool?>? RequestClose;

    public ObservableCollection<HistoryRecordItem> Records { get; } = new();

    public TagHistoryViewModel(ITagHistoryService historyService, string filePath,
        List<TagHistoryRecord> records)
    {
        _historyService = historyService;
        FilePath = filePath;
        foreach (var record in records)
            Records.Add(new HistoryRecordItem(record));
    }

    /// <summary>当前文件路径（供窗口标题展示）。</summary>
    public string FilePath { get; }

    partial void OnSelectedItemChanged(HistoryRecordItem? value)
    {
        RestoreCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        if (value != null) _ = LoadDetailAsync(value.Record);
        else ClearDetail();
    }

    private bool HasSelection => SelectedItem != null;

    /// <summary>恢复此记录：设置返回值并 DialogResult=true。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Restore()
    {
        var record = SelectedRecord;
        if (record == null) return;
        SelectedReturn = record;
        SelectedCoverData = _currentCoverData;
        RequestClose?.Invoke(true);
    }

    /// <summary>删除当前选中记录并从列表移除。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        var item = SelectedItem;
        if (item == null) return;
        if (!WpfUI.Confirm($"确定删除该条历史记录？\n{item.Display}")) return;

        try
        {
            _historyService.DeleteHistory(item.Record.Serial);
        }
        catch (Exception ex)
        {
            WpfUI.Error("删除历史记录失败：" + ex.Message);
            return;
        }

        var index = Records.IndexOf(item);
        Records.Remove(item);
        if (Records.Count > 0)
            SelectedItem = Records[Math.Clamp(index, 0, Records.Count - 1)];
        else
            SelectedItem = null;
    }

    /// <summary>清空全部历史（所有文件），确认后关窗并返回 false。</summary>
    [RelayCommand]
    private void ClearAll()
    {
        if (!WpfUI.Confirm("确定清空全部标签历史吗？该操作会删除所有文件的历史记录与历史封面，且不可恢复。"))
            return;

        try
        {
            _historyService.ClearAll();
        }
        catch (Exception ex)
        {
            WpfUI.Error("清空历史失败：" + ex.Message);
            return;
        }

        Records.Clear();
        ClearDetail();
        // 按约定清空全部后关窗并返回 false
        RequestClose?.Invoke(false);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    /// <summary>加载选中记录详情：封面通过历史服务读取后降采样展示。</summary>
    private async Task LoadDetailAsync(TagHistoryRecord record)
    {
        IsDetailLoading = true;
        ClearDetail();
        SelectedRecord = record;
        try
        {
            // 封面读取涉及磁盘 IO 与图像解码，放到后台执行
            var data = await Task.Run(() =>
            {
                try { return _historyService.ReadCoverData(record.Serial); }
                catch { return null; }
            });

            _currentCoverData = data;
            if (data is { Length: > 0 })
            {
                var bmp = await Task.Run(() => ImageHelper.ToBitmapSource(data, 200));
                if (bmp != null)
                {
                    CoverImage = bmp;
                    HasCover = true;
                }
            }
        }
        catch
        {
            // 单条详情加载失败不影响窗口使用
        }
        finally
        {
            IsDetailLoading = false;
        }
    }

    private void ClearDetail()
    {
        SelectedRecord = null;
        _currentCoverData = null;
        CoverImage = null;
        HasCover = false;
    }
}
