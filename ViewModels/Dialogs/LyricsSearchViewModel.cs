using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;

namespace MusicTagRecover.ViewModels.Dialogs;

/// <summary>歌词搜索列表条目：标题/艺术家/来源 + 片段预览。</summary>
public sealed partial class LyricResultItem : ObservableObject
{
    public SearchResult Result { get; }

    public LyricResultItem(SearchResult result) => Result = result;

    public string? Title => Result.Title;
    public string? Artist => Result.Artist;
    public string SourceName => Result.SourceName;
    public string MatchText => Result.MatchScore > 0 ? $"{Result.MatchScore:P0}" : string.Empty;

    /// <summary>列表项中的歌词片段预览（最多显示前若干行）。</summary>
    public string Snippet
    {
        get
        {
            var snippet = Result.LyricSnippet;
            if (string.IsNullOrWhiteSpace(snippet)) return "（无预览）";
            var lines = snippet.Split('\n');
            var preview = string.Join('\n', lines.Take(4)).Trim();
            return lines.Length > 4 ? preview + "\n…" : preview;
        }
    }
}

/// <summary>
/// 歌词在线搜索对话框 VM：左侧结果列表，右侧选中后下载完整歌词（原文/翻译分页），
/// 支持分页；确定时返回 SelectedLyric 与 SelectedResult。
/// </summary>
public sealed partial class LyricsSearchViewModel : ObservableObject
{
    private readonly MusicFile _file;
    private readonly string _source;
    private readonly ILyricService _lyricService;
    private readonly ITagService _tagService;
    private readonly ISettingsService _settings;
    private readonly LyricInfo.DownloadConfig _downloadConfig;
    private readonly bool _supportsPagination;
    private readonly HashSet<string> _identityKeys = new();
    private int _nextOffset;
    private CancellationTokenSource _cts = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDetailLoading;
    [ObservableProperty] private string _statusText = "正在准备搜索…";
    [ObservableProperty] private LyricResultItem? _selectedItem;

    /// <summary>当前选中条目已下载的完整歌词。</summary>
    [ObservableProperty] private LyricInfo? _currentLyric;

    /// <summary>原文歌词文本（只读展示）。</summary>
    [ObservableProperty] private string _originalLyric = string.Empty;

    /// <summary>翻译歌词文本（只读展示），无翻译为空。</summary>
    [ObservableProperty] private string _translatedLyric = string.Empty;

    /// <summary>右侧详情区是否显示翻译页签。</summary>
    [ObservableProperty] private bool _hasTranslation;

    /// <summary>是否存在选中的搜索结果（控制右侧占位提示与详情区切换）。</summary>
    [ObservableProperty] private bool _hasSelectedItem;

    /// <summary>窗口返回值。</summary>
    public LyricInfo? SelectedLyric { get; private set; }
    public SearchResult? SelectedResult { get; private set; }

    /// <summary>确认时是否已把歌词成功写入音频文件标签。</summary>
    public bool Saved { get; private set; }

    public event Action<bool?>? RequestClose;

    public ObservableCollection<LyricResultItem> Results { get; } = new();

    public LyricsSearchViewModel(MusicFile file, string source, ILyricService lyricService,
        ITagService tagService, ISettingsService settings)
    {
        _file = file;
        _source = source;
        _lyricService = lyricService;
        _tagService = tagService;
        _settings = settings;
        _supportsPagination = lyricService.SupportsPagination(source);
        _downloadConfig = new LyricInfo.DownloadConfig
        {
            DownloadTranslation = settings.LyricDownload_DownloadTrans_Enable,
            DontDownloadOriginal = settings.LyricDownload_DownloadTrans_DontDownloadOrigLyric,
            LyricFormat = string.IsNullOrWhiteSpace(settings.LyricDownload_DownloadTrans_LyricFormat)
                ? "{artist} - {title}.lrc"
                : settings.LyricDownload_DownloadTrans_LyricFormat!,
            ReformatTimetag = settings.LyricDownload_ReformatTimetag,
            RemoveTimetag = settings.LyricDownload_RemoveTimetag,
            DeleteHeadTag = settings.LyricDownload_DeleteHeadTag,
            DeleteBlankLines = settings.LyricDownload_DeleteLinesOfBlankText,
            ChineseConvMode = string.IsNullOrWhiteSpace(settings.LyricDownload_DownloadTrans_ChineseConvMode)
                ? "none"
                : settings.LyricDownload_DownloadTrans_ChineseConvMode!,
        };
    }

    /// <summary>关窗时取消正在进行的网络请求。</summary>
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

    /// <summary>选中结果变化时自动下载完整歌词到右侧详情区。</summary>
    partial void OnSelectedItemChanged(LyricResultItem? value)
    {
        HasSelectedItem = value != null;
        ConfirmCommand.NotifyCanExecuteChanged();
        if (value != null) _ = LoadDetailAsync(value);
        else ClearDetail();
    }

    private bool CanNextPage() => _supportsPagination && !IsBusy && Results.Count > 0;
    private bool CanConfirm() => SelectedItem != null && !IsBusy && !IsDetailLoading;

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private async Task NextPageAsync() => await LoadAsync(resetOffset: false);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        var item = SelectedItem;
        if (item == null) return;

        // 详情尚未下载完成时先补下载
        var lyric = CurrentLyric;
        if (lyric == null || !ReferenceEquals(_lastDetailResult, item.Result))
        {
            lyric = await DownloadDetailAsync(item.Result, _cts.Token);
        }

        if (lyric == null)
        {
            WpfUI.Warning("歌词下载失败，请选择其他结果后重试。");
            return;
        }

        // “保存到标签”：直接把歌词写入音频文件标签，而不是仅填入编辑面板
        var text = lyric.LrcFormatted ?? lyric.OriginalLyric ?? string.Empty;
        IsBusy = true;
        try
        {
            var ok = await _tagService.WriteLyricsAsync(_file.FilePath, text);
            if (!ok)
            {
                WpfUI.Error("歌词写入标签失败：文件可能正被其他程序占用或没有写入权限，请关闭占用程序后重试。");
                return;
            }
        }
        catch (Exception ex)
        {
            WpfUI.Error("歌词写入标签失败：" + ex.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        _file.Lyrics = text;
        _file.HasLyrics = !string.IsNullOrWhiteSpace(text);

        SelectedLyric = lyric;
        SelectedResult = item.Result;
        Saved = true;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void CancelClose() => RequestClose?.Invoke(false);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync() => await LoadAsync(resetOffset: true);

    private bool CanRetry() => !IsBusy;

    /// <summary>搜索结果加载（首页重置 / 下一页追加去重）。</summary>
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
        StatusText = resetOffset ? "正在搜索歌词…" : "正在加载下一页…";
        try
        {
            var condition = SearchCondition.FromSettings(_settings);
            condition.WebSearchItemsLimit = Math.Max(1, _settings.WebSearchItemsLimit);
            condition.ItunesCountry = _settings.ItunesSearchParamsCountry;
            condition.WebSearchItemsOffset = offset;

            var list = await _lyricService.SearchLyricsFromSourceAsync(
                _file, _source, condition, _downloadConfig, ct);

            // 下一页 offset 按每页条数递增
            _nextOffset = offset + condition.WebSearchItemsLimit;

            int added = 0;
            foreach (var result in list)
            {
                if (!_identityKeys.Add(result.GetIdentityKey())) continue;
                Results.Add(new LyricResultItem(result));
                added++;
            }

            StatusText = Results.Count == 0
                ? "未搜索到歌词，可点击「重试」或更换数据源。"
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

    private SearchResult? _lastDetailResult;

    /// <summary>下载选中结果的完整歌词并刷新右侧文本区。</summary>
    private async Task LoadDetailAsync(LyricResultItem item)
    {
        ClearDetail();
        IsDetailLoading = true;
        try
        {
            var lyric = await DownloadDetailAsync(item.Result, _cts.Token);
            // 下载期间用户已切换到其他条目时丢弃本次结果
            if (SelectedItem != item) return;
            if (lyric == null)
            {
                OriginalLyric = "（歌词下载失败）";
                return;
            }

            CurrentLyric = lyric;
            _lastDetailResult = item.Result;
            OriginalLyric = lyric.LrcFormatted ?? lyric.OriginalLyric ?? string.Empty;
            TranslatedLyric = lyric.TranslatedLyric ?? string.Empty;
            HasTranslation = lyric.HasTranslation;
        }
        catch (OperationCanceledException) { /* 切换选择时取消旧请求 */ }
        finally
        {
            IsDetailLoading = false;
            ConfirmCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task<LyricInfo?> DownloadDetailAsync(SearchResult result, CancellationToken ct)
    {
        try
        {
            return await _lyricService.DownloadLyricAsync(result, _downloadConfig, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void ClearDetail()
    {
        CurrentLyric = null;
        _lastDetailResult = null;
        OriginalLyric = string.Empty;
        TranslatedLyric = string.Empty;
        HasTranslation = false;
    }
}
