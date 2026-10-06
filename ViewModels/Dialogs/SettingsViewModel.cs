using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;

namespace MusicTagRecover.ViewModels.Dialogs;

/// <summary>
/// 单个标签源分类（封面/歌词/组合标签）的可排序列表包装。
/// 列表中的 TagSourceItem 为 POCO，CheckBox/TextBox 双向绑定可直接回写对象。
/// </summary>
public sealed class TagSourceListViewModel
{
    public TagSourceCategory Category { get; }
    public ObservableCollection<TagSourceItem> Items { get; }

    public TagSourceListViewModel(string? json, TagSourceCategory category)
    {
        Category = category;
        Items = new ObservableCollection<TagSourceItem>(TagSourceCatalog.Load(json, category));
    }
}

/// <summary>
/// 设置对话框 VM：常规/搜索/歌词/图片/标签源/高级全部分项，
/// 确定时统一写回 ISettingsService 并 Save()，恢复默认后刷新控件。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IImageCache _imageCache;

    // ===== 常规 =====
    [ObservableProperty] private bool _includeSubDir;
    [ObservableProperty] private string _restrictFileExts = string.Empty;
    [ObservableProperty] private bool _fileFilterByDuration;
    [ObservableProperty] private bool _fileFilterIgnoreVideoFile;
    [ObservableProperty] private string _id3v2Version = "v2.3";
    [ObservableProperty] private bool _saveTagsKeepUpdateTime;

    // ===== 搜索 =====
    [ObservableProperty] private bool _searchConditionUseTitle;
    [ObservableProperty] private bool _searchConditionUseAlbum;
    [ObservableProperty] private bool _searchConditionUseArtist;
    [ObservableProperty] private bool _searchConditionUseOnlyFilename;
    [ObservableProperty] private int _webSearchItemsLimit;
    [ObservableProperty] private int _autoMatchTagsWebSearchThreadCount;
    [ObservableProperty] private string _itunesSearchParamsCountry = "US";

    // ===== 歌词 =====
    [ObservableProperty] private bool _lyricDeleteHeadTag;
    [ObservableProperty] private bool _lyricDeleteBlankLines;
    [ObservableProperty] private bool _lyricReformatTimetag;
    [ObservableProperty] private bool _lyricRemoveTimetag;
    [ObservableProperty] private bool _dontDownloadLyricWithInstrumentInTitle;
    [ObservableProperty] private bool _downloadTransEnable;
    [ObservableProperty] private bool _downloadTransDontDownloadOrig;
    [ObservableProperty] private string _downloadTransLyricFormat = string.Empty;
    [ObservableProperty] private string _downloadTransChineseConvMode = "none";
    [ObservableProperty] private string _saveLrcDirectory = string.Empty;
    [ObservableProperty] private string _saveLrcEncoding = "utf-8";
    [ObservableProperty] private string _saveLrcFilenameFormat = string.Empty;

    // ===== 图片 =====
    [ObservableProperty] private string _pictureFormatLimits = string.Empty;
    [ObservableProperty] private int? _pictureResolutionLimits;
    [ObservableProperty] private int? _pictureSizeLimitsKB;
    [ObservableProperty] private bool _overwritePictureboxPicture;
    [ObservableProperty] private int _urlCacheMaxSizeMb;
    [ObservableProperty] private string _urlCacheDir = string.Empty;
    [ObservableProperty] private string _cacheSizeText = string.Empty;

    // ===== 高级 =====
    [ObservableProperty] private string _commentTagWrite163Key = string.Empty;
    [ObservableProperty] private string _connectorsArtists = string.Empty;
    [ObservableProperty] private string _connectorsLyricAndTLyric = string.Empty;
    [ObservableProperty] private string _combTagsSearchOverwriteOptions = string.Empty;
    [ObservableProperty] private bool _logEnabled;
    [ObservableProperty] private string _logLevel = "info";
    [ObservableProperty] private string _proxyUrl = string.Empty;

    // ===== 标签源（启用/排序/每源数量） =====
    public TagSourceListViewModel PictureSources { get; private set; } = null!;
    public TagSourceListViewModel LyricSources { get; private set; } = null!;
    public TagSourceListViewModel CombTagSources { get; private set; } = null!;

    public event Action<bool?>? RequestClose;

    public SettingsViewModel(ISettingsService settings, IImageCache imageCache,
        ITagHistoryService history)
    {
        _settings = settings;
        _imageCache = imageCache;
        // history 暂不直接参与设置项读写；构造保留以与主窗口调用约定一致
        ArgumentNullException.ThrowIfNull(history);
        LoadFromSettings();
    }

    /// <summary>从设置服务读取全部值到控件绑定属性。</summary>
    private void LoadFromSettings()
    {
        var s = _settings;

        // 常规
        IncludeSubDir = s.IncludeSubDir;
        RestrictFileExts = s.RestrictFileExts ?? string.Empty;
        FileFilterByDuration = s.FileFilterByDuration;
        FileFilterIgnoreVideoFile = s.FileFilterIgnoreVideoFile;
        Id3v2Version = string.IsNullOrWhiteSpace(s.Id3v2Version) ? "v2.3" : s.Id3v2Version;
        SaveTagsKeepUpdateTime = s.SaveTagsKeepUpdateTime;

        // 搜索
        SearchConditionUseTitle = s.SearchConditionUseTitle;
        SearchConditionUseAlbum = s.SearchConditionUseAlbum;
        SearchConditionUseArtist = s.SearchConditionUseArtist;
        SearchConditionUseOnlyFilename = s.SearchConditionUseOnlyFilename;
        WebSearchItemsLimit = s.WebSearchItemsLimit;
        AutoMatchTagsWebSearchThreadCount = s.AutoMatchTagsWebSearchThreadCount;
        ItunesSearchParamsCountry = s.ItunesSearchParamsCountry ?? "US";

        // 歌词
        LyricDeleteHeadTag = s.LyricDownload_DeleteHeadTag;
        LyricDeleteBlankLines = s.LyricDownload_DeleteLinesOfBlankText;
        LyricReformatTimetag = s.LyricDownload_ReformatTimetag;
        LyricRemoveTimetag = s.LyricDownload_RemoveTimetag;
        DontDownloadLyricWithInstrumentInTitle = s.DontDownloadLyricWithInstrumentInTitle;
        DownloadTransEnable = s.LyricDownload_DownloadTrans_Enable;
        DownloadTransDontDownloadOrig = s.LyricDownload_DownloadTrans_DontDownloadOrigLyric;
        DownloadTransLyricFormat = s.LyricDownload_DownloadTrans_LyricFormat ?? string.Empty;
        DownloadTransChineseConvMode = s.LyricDownload_DownloadTrans_ChineseConvMode ?? "none";
        SaveLrcDirectory = s.SaveLrcDirectory ?? string.Empty;
        SaveLrcEncoding = s.SaveLrcFileDefaultEncoding ?? "utf-8";
        SaveLrcFilenameFormat = s.SaveLrcFilenameFormat ?? string.Empty;

        // 图片
        PictureFormatLimits = s.PictureFormatLimits ?? string.Empty;
        PictureResolutionLimits = s.PictureResolutionLimits;
        PictureSizeLimitsKB = s.PictureSizeLimitsKB;
        OverwritePictureboxPicture = s.OverwritePictureboxPicture;
        UrlCacheMaxSizeMb = s.UrlCacheMaxSizeMb;
        UrlCacheDir = _imageCache.UrlCacheDir ?? string.Empty;
        RefreshCacheSizeText();

        // 高级
        CommentTagWrite163Key = s.CommentTagWrite163Key ?? string.Empty;
        ConnectorsArtists = s.ConnectorsArtists ?? string.Empty;
        ConnectorsLyricAndTLyric = s.ConnectorsLyricAndTLyric ?? string.Empty;
        CombTagsSearchOverwriteOptions = s.CombTagsSearchOverwriteOptions ?? string.Empty;
        LogEnabled = s.LogEnabled;
        LogLevel = string.IsNullOrWhiteSpace(s.LogLevel) ? "info" : s.LogLevel!.ToLowerInvariant();
        ProxyUrl = s.ProxyUrl ?? string.Empty;

        // 标签源（每次重新装载以反映恢复默认）
        PictureSources = new TagSourceListViewModel(
            s.PictureInfo_SourceItemList, TagSourceCategory.Picture);
        LyricSources = new TagSourceListViewModel(
            s.LyricInfo_SourceItemList, TagSourceCategory.Lyrics);
        CombTagSources = new TagSourceListViewModel(
            s.CombTagsInfo_SourceItemList, TagSourceCategory.CombinationTags);
        OnPropertyChanged(nameof(PictureSources));
        OnPropertyChanged(nameof(LyricSources));
        OnPropertyChanged(nameof(CombTagSources));
    }

    /// <summary>选择 LRC 默认保存目录（单选文件夹）。</summary>
    [RelayCommand]
    private void BrowseSaveLrcDir()
    {
        try
        {
            var picker = WpfUI.PickFolders("选择 LRC 默认保存目录", allowMultiSelect: false);
            var dir = picker.SelectedPaths.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(dir)) SaveLrcDirectory = dir;
        }
        catch (Exception ex)
        {
            WpfUI.Warning("选择目录失败：" + ex.Message);
        }
    }

    /// <summary>立即清理 URL 性能缓存，并顺带清理无历史引用的孤儿封面文件。</summary>
    [RelayCommand]
    private void ClearCache()
    {
        try
        {
            _imageCache.ClearUrlCache();
            _imageCache.ClearUnreferencedHistory();
            RefreshCacheSizeText();
            WpfUI.Info("缓存清理完成。\n" + CacheSizeText);
        }
        catch (Exception ex)
        {
            WpfUI.Error("清理缓存失败：" + ex.Message);
        }
    }

    /// <summary>恢复默认设置（服务内部会持久化），随后刷新全部控件。</summary>
    [RelayCommand]
    private void ResetDefaults()
    {
        if (!WpfUI.Confirm("确定恢复全部设置为默认值？当前修改将被覆盖。")) return;
        _settings.ResetToDefaults();
        LoadFromSettings();
    }

    /// <summary>写回全部设置项并持久化，DialogResult=true 关窗。</summary>
    [RelayCommand]
    private void Save()
    {
        var s = _settings;

        // 常规
        s.IncludeSubDir = IncludeSubDir;
        s.RestrictFileExts = RestrictFileExts;
        s.FileFilterByDuration = FileFilterByDuration;
        s.FileFilterIgnoreVideoFile = FileFilterIgnoreVideoFile;
        s.Id3v2Version = Id3v2Version;
        s.SaveTagsKeepUpdateTime = SaveTagsKeepUpdateTime;

        // 搜索
        s.SearchConditionUseTitle = SearchConditionUseTitle;
        s.SearchConditionUseAlbum = SearchConditionUseAlbum;
        s.SearchConditionUseArtist = SearchConditionUseArtist;
        s.SearchConditionUseOnlyFilename = SearchConditionUseOnlyFilename;
        s.WebSearchItemsLimit = Math.Max(1, WebSearchItemsLimit);
        s.AutoMatchTagsWebSearchThreadCount = Math.Max(1, AutoMatchTagsWebSearchThreadCount);
        s.ItunesSearchParamsCountry = ItunesSearchParamsCountry;

        // 歌词
        s.LyricDownload_DeleteHeadTag = LyricDeleteHeadTag;
        s.LyricDownload_DeleteLinesOfBlankText = LyricDeleteBlankLines;
        s.LyricDownload_ReformatTimetag = LyricReformatTimetag;
        s.LyricDownload_RemoveTimetag = LyricRemoveTimetag;
        s.DontDownloadLyricWithInstrumentInTitle = DontDownloadLyricWithInstrumentInTitle;
        s.LyricDownload_DownloadTrans_Enable = DownloadTransEnable;
        s.LyricDownload_DownloadTrans_DontDownloadOrigLyric = DownloadTransDontDownloadOrig;
        s.LyricDownload_DownloadTrans_LyricFormat = DownloadTransLyricFormat;
        s.LyricDownload_DownloadTrans_ChineseConvMode = DownloadTransChineseConvMode;
        s.SaveLrcDirectory = SaveLrcDirectory;
        s.SaveLrcFileDefaultEncoding = SaveLrcEncoding;
        s.SaveLrcFilenameFormat = SaveLrcFilenameFormat;

        // 图片
        s.PictureFormatLimits = PictureFormatLimits;
        s.PictureResolutionLimits = PictureResolutionLimits;
        s.PictureSizeLimitsKB = PictureSizeLimitsKB;
        s.OverwritePictureboxPicture = OverwritePictureboxPicture;
        s.UrlCacheMaxSizeMb = Math.Max(1, UrlCacheMaxSizeMb);

        // 高级
        s.CommentTagWrite163Key = CommentTagWrite163Key;
        s.ConnectorsArtists = ConnectorsArtists;
        s.ConnectorsLyricAndTLyric = ConnectorsLyricAndTLyric;
        s.CombTagsSearchOverwriteOptions = CombTagsSearchOverwriteOptions;
        s.LogEnabled = LogEnabled;
        s.LogLevel = LogLevel;
        s.ProxyUrl = ProxyUrl;

        // 标签源：按当前顺序序列化
        s.PictureInfo_SourceItemList = TagSourceCatalog.Serialize(PictureSources.Items);
        s.LyricInfo_SourceItemList = TagSourceCatalog.Serialize(LyricSources.Items);
        s.CombTagsInfo_SourceItemList = TagSourceCatalog.Serialize(CombTagSources.Items);

        s.Save();
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    // ===== 标签源排序（上移/下移） =====

    [RelayCommand] private void MovePictureUp(TagSourceItem? item)
        => MoveItem(PictureSources.Items, item, -1);
    [RelayCommand] private void MovePictureDown(TagSourceItem? item)
        => MoveItem(PictureSources.Items, item, 1);
    [RelayCommand] private void MoveLyricUp(TagSourceItem? item)
        => MoveItem(LyricSources.Items, item, -1);
    [RelayCommand] private void MoveLyricDown(TagSourceItem? item)
        => MoveItem(LyricSources.Items, item, 1);
    [RelayCommand] private void MoveCombTagUp(TagSourceItem? item)
        => MoveItem(CombTagSources.Items, item, -1);
    [RelayCommand] private void MoveCombTagDown(TagSourceItem? item)
        => MoveItem(CombTagSources.Items, item, 1);

    /// <summary>在分类列表内移动标签源位置，同时重排 Sequence。</summary>
    private static void MoveItem(ObservableCollection<TagSourceItem> items,
        TagSourceItem? item, int delta)
    {
        if (item == null) return;
        var index = items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= items.Count) return;
        items.Move(index, target);
        for (var i = 0; i < items.Count; i++) items[i].Sequence = i;
    }

    private void RefreshCacheSizeText()
    {
        try
        {
            CacheSizeText = $"URL 缓存：{FileSizeConverter.FormatSize(_imageCache.GetUrlCacheSize())}"
                + $"    历史封面：{FileSizeConverter.FormatSize(_imageCache.GetHistorySize())}";
        }
        catch
        {
            CacheSizeText = "缓存大小统计不可用";
        }
    }
}
