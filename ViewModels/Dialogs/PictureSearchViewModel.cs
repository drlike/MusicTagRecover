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
/// 封面搜索条目：携带搜索结果与异步加载的缩略图。
/// </summary>
public sealed partial class PictureResultItem : ObservableObject
{
    public SearchResult Result { get; }

    [ObservableProperty] private BitmapSource? _thumbnail;

    public PictureResultItem(SearchResult result) => Result = result;

    public string? Title => Result.Title;
    public string? Artist => Result.Artist;
    public string SourceName => Result.SourceName;
    public string MatchText => Result.MatchScore > 0 ? $"{Result.MatchScore:P0}" : string.Empty;
}

/// <summary>
/// 封面在线搜索对话框 VM：构造后异步加载，网格展示缩略图，支持分页；
/// 确定时按设置中的图片限制下载完整封面并返回。
/// </summary>
public sealed partial class PictureSearchViewModel : ObservableObject
{
    private readonly MusicFile _file;
    private readonly string _source;
    private readonly ICoverService _coverService;
    private readonly ISettingsService _settings;
    private readonly IImageCache _imageCache;
    private readonly CoverArt.LimitsConfig _limits;
    private readonly bool _supportsPagination;
    private readonly HashSet<string> _identityKeys = new();
    private int _nextOffset;
    private CancellationTokenSource _cts = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "正在准备搜索…";
    [ObservableProperty] private PictureResultItem? _selectedItem;

    /// <summary>窗口返回值：选中并下载完成的封面。</summary>
    public CoverArt? SelectedCover { get; private set; }

    /// <summary>窗口返回值：选中的搜索结果。</summary>
    public SearchResult? SelectedResult { get; private set; }

    /// <summary>请求关闭窗口，参数为 DialogResult。</summary>
    public event Action<bool?>? RequestClose;

    public ObservableCollection<PictureResultItem> Results { get; } = new();

    public PictureSearchViewModel(MusicFile file, string source, ICoverService coverService,
        ISettingsService settings, IImageCache imageCache)
    {
        _file = file;
        _source = source;
        _coverService = coverService;
        _settings = settings;
        _imageCache = imageCache;
        _supportsPagination = coverService.SupportsPagination(source);
        _limits = new CoverArt.LimitsConfig
        {
            FormatLimits = string.IsNullOrWhiteSpace(settings.PictureFormatLimits)
                ? "jpg,jpeg,png,bmp,gif"
                : settings.PictureFormatLimits!,
            MaxResolution = settings.PictureResolutionLimits,
            MaxSizeKB = settings.PictureSizeLimitsKB,
            OverwriteExisting = settings.OverwritePictureboxPicture,
        };
    }

    /// <summary>窗口关闭时取消所有网络请求与缩略图加载。</summary>
    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* 窗口关闭过程中竞争，忽略 */ }
    }

    /// <summary>构造后调用：加载第一页结果。</summary>
    public async Task InitializeAsync() => await LoadAsync(resetOffset: true);

    /// <summary>当选中项变化时刷新"确定/下载"按钮可用性。</summary>
    partial void OnSelectedItemChanged(PictureResultItem? value)
        => ConfirmCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        NextPageCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private bool CanNextPage() => _supportsPagination && !IsBusy && Results.Count > 0;
    private bool CanConfirm() => SelectedItem != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private async Task NextPageAsync() => await LoadAsync(resetOffset: false);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        var item = SelectedItem;
        if (item == null) return;

        IsBusy = true;
        StatusText = "正在下载选中的封面…";
        try
        {
            // 注意：服务在部分失败分支可能返回 null 实例，需双重判空
            var download = await _coverService.DownloadCoverAsync(
                item.Result, _limits, _cts.Token);
            if (download?.Cover == null)
            {
                WpfUI.Warning(string.IsNullOrEmpty(download?.ValidationError)
                    ? "封面下载失败，请更换一个结果后重试。"
                    : download!.ValidationError!);
                return;
            }

            SelectedCover = download.Cover;
            SelectedResult = item.Result;
            RequestClose?.Invoke(true);
        }
        catch (OperationCanceledException) { /* 用户取消，不提示 */ }
        catch (Exception ex)
        {
            WpfUI.Error("下载封面时发生异常：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            if (SelectedCover == null) StatusText = BuildIdleStatus();
        }
    }

    [RelayCommand]
    private void CancelClose() => RequestClose?.Invoke(false);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync() => await LoadAsync(resetOffset: true);

    private bool CanRetry() => !IsBusy;

    /// <summary>
    /// 执行一次搜索；首页清空旧结果，下一页追加并按身份键去重。
    /// </summary>
    private async Task LoadAsync(bool resetOffset)
    {
        if (IsBusy) return;

        // 翻页使用新令牌，取消旧请求不影响已展示结果
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
        StatusText = resetOffset ? "正在搜索封面…" : "正在加载下一页…";
        try
        {
            var condition = SearchCondition.FromSettings(_settings);
            condition.WebSearchItemsLimit = Math.Max(1, _settings.WebSearchItemsLimit);
            condition.ItunesCountry = _settings.ItunesSearchParamsCountry;
            condition.WebSearchItemsOffset = offset;

            var list = await _coverService.SearchCoversFromSourceAsync(
                _file, _source, condition, ct);

            // 下一页 offset 按每页条数递增
            _nextOffset = offset + condition.WebSearchItemsLimit;

            var added = new List<PictureResultItem>();
            foreach (var result in list)
            {
                if (!_identityKeys.Add(result.GetIdentityKey())) continue;
                added.Add(new PictureResultItem(result));
            }

            foreach (var item in added) Results.Add(item);
            StatusText = BuildIdleStatus();

            if (added.Count > 0)
            {
                // 缩略图后台下载/解码，不阻塞结果列表交互
                _ = LoadThumbnailsAsync(added, ct);
            }
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

    /// <summary>并发（限流）加载缩略图：优先 cache\history 本地文件，否则按 URL 下载后降采样。</summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<PictureResultItem> items, CancellationToken ct)
    {
        using var throttle = new SemaphoreSlim(6, 6);
        var tasks = items.Select(async item =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var result = item.Result;
                byte[]? bytes = null;

                // CoverTempPath 是 cache\history 相对文件名时直接读本地，不走网络
                if (!string.IsNullOrWhiteSpace(result.CoverTempPath))
                {
                    try { bytes = _imageCache.ReadHistory(result.CoverTempPath!); }
                    catch { /* 缓存读取失败退化为网络下载 */ }
                }

                if ((bytes == null || bytes.Length == 0) && !string.IsNullOrWhiteSpace(result.CoverUrl))
                {
                    bytes = await _coverService.DownloadImageBytesAsync(
                        result.CoverUrl!, result.SourceName, ct);
                }

                if (bytes == null || bytes.Length == 0) return;

                // 解码与降采样放到线程池，避免大图片卡 UI
                var bmp = await Task.Run(() => ImageHelper.ToBitmapSource(bytes, 160), ct);
                if (bmp != null)
                {
                    Application.Current.Dispatcher.Invoke(() => item.Thumbnail = bmp);
                }
            }
            catch (OperationCanceledException) { /* 翻页/关窗取消 */ }
            catch
            {
                // 单张缩略图失败不影响其他结果
            }
            finally
            {
                throttle.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private string BuildIdleStatus()
        => Results.Count == 0
            ? "未搜索到封面，可点击「重试」或更换数据源。"
            : $"共 {Results.Count} 个结果" + (_supportsPagination ? "，可继续加载下一页。" : "。");
}
