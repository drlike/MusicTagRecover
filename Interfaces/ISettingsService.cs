namespace MusicTagRecover.Interfaces;

using MusicTagRecover.Models;

/// <summary>
/// 应用设置服务，管理窗口状态、歌词/图片/搜索等配置项
/// </summary>
public interface ISettingsService
{
    // === 窗口设置 ===
    string? MainFormPosSizeInfo { get; set; }
    bool Maximized { get; set; }

    // === 应用设置 ===
    string? LastVersionCode { get; set; }
    string? IgnoreCheckSpecAppVersion { get; set; }

    // === 文件设置 ===
    bool IncludeSubDir { get; set; }
    string? RestrictFileExts { get; set; }
    bool FileFilterByDuration { get; set; }
    bool FileFilterIgnoreVideoFile { get; set; }
    string? FilenameCustomPattern { get; set; }
    string? FilenameRelCondition { get; set; }
    string? FilenameRelRegexCondition { get; set; }
    int FilenameRelSelectedTab { get; set; }

    // === 列表视图设置 (ListViewFileSetting, ListviewColumnHeader) ===
    string? ListViewFileSetting { get; set; }
    string? ListviewColumnHeader { get; set; }
    string? SortSetting { get; set; }
    string? FilterListViewKeyword { get; set; }
    string? FilterListViewType { get; set; }

    // === 搜索设置 ===
    bool SearchConditionUseTitle { get; set; }
    bool SearchConditionUseAlbum { get; set; }
    bool SearchConditionUseArtist { get; set; }
    bool SearchConditionUseOnlyFilename { get; set; }
    string? SearchConditionItemList { get; set; }
    int WebSearchItemsLimit { get; set; }
    int AutoMatchTagsWebSearchThreadCount { get; set; }
    string ItunesSearchParamsCountry { get; set; }

    // === 标签设置 (ID3v2Version, SaveTagsKeepUpdateTime, etc.) ===
    string Id3v2Version { get; set; }
    bool SaveTagsKeepUpdateTime { get; set; }
    string? CommentTagWrite163Key { get; set; }
    string? AutoMatchTagsCondition { get; set; }
    string? CombTagsSearchOverwriteOptions { get; set; }
    string? ConnectorsArtists { get; set; }
    string? ConnectorsLyricAndTLyric { get; set; }

    // === 歌词设置 ===
    bool LyricDownload_DeleteHeadTag { get; set; }
    bool LyricDownload_DeleteLinesOfBlankText { get; set; }
    bool LyricDownload_ReformatTimetag { get; set; }
    bool LyricDownload_RemoveTimetag { get; set; }
    bool DontDownloadLyricWithInstrumentInTitle { get; set; }
    bool LyricDownload_DownloadTrans_Enable { get; set; }
    bool LyricDownload_DownloadTrans_DontDownloadOrigLyric { get; set; }
    string? LyricDownload_DownloadTrans_LyricFormat { get; set; }
    string? LyricDownload_DownloadTrans_ChineseConvMode { get; set; }
    string? SaveLrcDirectory { get; set; }
    string? SaveLrcFileDefaultEncoding { get; set; }
    string? SaveLrcFilenameFormat { get; set; }

    // === 图片设置 ===
    string? PictureFormatLimits { get; set; }
    int? PictureResolutionLimits { get; set; }
    int? PictureSizeLimitsKB { get; set; }
    bool OverwritePictureboxPicture { get; set; }

    // === 数据源设置 ===
    string? LyricInfo_SourceItemList { get; set; }
    string? PictureInfo_SourceItemList { get; set; }
    string? CombTagsInfo_SourceItemList { get; set; }

    // === 代理设置 ===
    string ProxyUrl { get; set; }
    string? ProxySourceSettings { get; set; }

    // === 日志设置 ===
    bool LogEnabled { get; set; }
    string LogLevel { get; set; }
    string? LogFilePath { get; set; }

    // === 缓存设置 ===
    /// <summary>下载图片缓存容量上限（MB），超限时启动按 LRU 淘汰。默认 256。</summary>
    int UrlCacheMaxSizeMb { get; set; }

    // === 持久化 ===
    void Load();
    void Save();
    void ResetToDefaults();
}
