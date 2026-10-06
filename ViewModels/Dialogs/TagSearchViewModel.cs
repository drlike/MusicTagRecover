using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;

namespace MusicTagRecover.ViewModels.Dialogs;

/// <summary>
/// 组合标签在线搜索对话框 VM：左侧结果列表（标题/艺术家/专辑/年份/风格/源/匹配度），
/// 右侧详情面板展示全部扩展字段、封面缩略图与歌词片段；确定返回 SelectedResult。
/// </summary>
public sealed partial class TagSearchViewModel : ObservableObject
{
    private readonly MusicFile _file;
    private readonly string _source;
    private readonly ICoverService _coverService;
    private readonly IImageCache _imageCache;
    private readonly ISettingsService _settings;
    private readonly bool _supportsPagination;
    private readonly HashSet<string> _identityKeys = new();
    private int _nextOffset;
    private CancellationTokenSource _cts = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDetailLoading;
    [ObservableProperty] private string _statusText = "正在准备搜索…";
    [ObservableProperty] private SearchResult? _selectedResult;

    [ObservableProperty] private bool _hasSelectedItem;
    [ObservableProperty] private BitmapSource? _detailCover;
    [ObservableProperty] private string _detailSnippet = string.Empty;
    [ObservableProperty] private bool _hasSnippet;

    /// <summary>窗口返回值。</summary>
    public SearchResult? SelectedReturn { get; private set; }

    public event Action<bool?>? RequestClose;

    public ObservableCollection<SearchResult> Results { get; } = new();
    public ObservableCollection<KeyValuePair<string, string>> DetailFields { get; } = new();

    public TagSearchViewModel(MusicFile file, string source, ICoverService coverService,
        ILyricService lyricService, IImageCache imageCache, ISettingsService settings)
    {
        _file = file;
        _source = source;
        _coverService = coverService;
        _imageCache = imageCache;
        _settings = settings;
        // 当前详情只展示 LyricSnippet 预览；保留 lyricService 参数以与主窗口调用约定一致，
        // 供后续"在标签窗内直接下载完整歌词"扩展使用
        ArgumentNullException.ThrowIfNull(lyricService);
        _supportsPagination = coverService.SupportsPagination(source);
    }

    /// <summary>关窗取消请求。</summary>
    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* 忽略竞争 */ }
    }

    public async Task InitializeAsync() => await LoadAsync(resetOffset: true);

    partial void OnIsBusyChanged(bool value)
    {
        NextPageCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    /// <summary>选中结果变化时刷新右侧详情（扩展字段/封面/歌词片段）。</summary>
    partial void OnSelectedResultChanged(SearchResult? value)
    {
        HasSelectedItem = value != null;
        ConfirmCommand.NotifyCanExecuteChanged();
        ClearDetail();
        if (value != null) _ = LoadDetailAsync(value);
    }

    private bool CanNextPage() => _supportsPagination && !IsBusy && Results.Count > 0;
    private bool CanConfirm() => SelectedResult != null && !IsBusy;
    private bool CanRetry() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private async Task NextPageAsync() => await LoadAsync(resetOffset: false);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (SelectedResult == null) return;
        SelectedReturn = SelectedResult;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void CancelClose() => RequestClose?.Invoke(false);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync() => await LoadAsync(resetOffset: true);

    /// <summary>搜索组合标签结果（首页重置 / 下一页追加去重）。</summary>
    private async Task LoadAsync(bool resetOffset)
    {
        if (IsBusy) return;

        var cts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _cts, cts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        var ct = cts.Token;

        int offset;
        if (resetOffset)
        {
            Results.Clear();
            _identityKeys.Clear();
            _nextOffset = 0;
            offset = 0;
        }
        else
        {
            offset = _nextOffset;
        }

        IsBusy = true;
        StatusText = resetOffset ? "正在搜索标签…" : "正在加载下一页…";
        try
        {
            var condition = SearchCondition.FromSettings(_settings);
            condition.WebSearchItemsLimit = Math.Max(1, _settings.WebSearchItemsLimit);
            condition.ItunesCountry = _settings.ItunesSearchParamsCountry;
            condition.WebSearchItemsOffset = offset;

            var list = await _coverService.SearchTagsFromSourceAsync(
                _file, _source, condition, ct);

            // 下一页 offset 按每页条数递增
            _nextOffset = offset + condition.WebSearchItemsLimit;

            foreach (var result in list)
            {
                if (_identityKeys.Add(result.GetIdentityKey()))
                    Results.Add(result);
            }

            StatusText = Results.Count == 0
                ? "未搜索到标签结果，可点击「重试」或更换数据源。"
                : $"共 {Results.Count} 个结果" + (_supportsPagination ? "，可继续加载下一页。" : "。");
        }
        catch (OperationCanceledException) { /* 被新搜索或关窗取消 */ }
        catch (Exception ex)
        {
            StatusText = "搜索失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>加载右侧详情：扩展字段立即填充，封面缩略图后台下载，歌词片段直接展示。</summary>
    private async Task LoadDetailAsync(SearchResult result)
    {
        IsDetailLoading = true;
        try
        {
            DetailFields.Clear();
            foreach (var pair in result.ExtraFields.OrderBy(p => p.Key, StringComparer.Ordinal))
                DetailFields.Add(pair);

            DetailSnippet = result.LyricSnippet ?? string.Empty;
            HasSnippet = !string.IsNullOrWhiteSpace(result.LyricSnippet);

            byte[]? bytes = null;
            if (!string.IsNullOrWhiteSpace(result.CoverTempPath))
            {
                try { bytes = _imageCache.ReadHistory(result.CoverTempPath!); }
                catch { /* 缓存缺失退化为网络下载 */ }
            }

            if ((bytes == null || bytes.Length == 0) && !string.IsNullOrWhiteSpace(result.CoverUrl))
            {
                try
                {
                    bytes = await _coverService.DownloadImageBytesAsync(
                        result.CoverUrl!, result.SourceName, _cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch { bytes = null; }
            }

            if (bytes is { Length: > 0 })
            {
                var bmp = await Task.Run(() => ImageHelper.ToBitmapSource(bytes, 200), _cts.Token);
                // 下载/解码期间用户已切换选择时丢弃旧封面
                if (bmp != null && SelectedResult == result)
                    Application.Current.Dispatcher.Invoke(() => DetailCover = bmp);
            }
        }
        catch (OperationCanceledException) { /* 切换选择时取消 */ }
        finally
        {
            IsDetailLoading = false;
        }
    }

    private void ClearDetail()
    {
        DetailFields.Clear();
        DetailCover = null;
        DetailSnippet = string.Empty;
        HasSnippet = false;
    }
}
