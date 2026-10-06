using MusicTagRecover.Models;

namespace MusicTagRecover.Interfaces;

/// <summary>
/// 歌词搜索/下载服务，支持多源歌词在线搜索和LRC格式处理
/// </summary>
public interface ILyricService
{
    /// <summary>搜索歌词</summary>
    Task<IReadOnlyList<SearchResult>> SearchLyricsAsync(MusicFile file, SearchCondition condition,
        LyricInfo.DownloadConfig config, CancellationToken ct = default);

    /// <summary>从指定数据源搜索歌词</summary>
    Task<IReadOnlyList<SearchResult>> SearchLyricsFromSourceAsync(
        MusicFile file, string source, SearchCondition condition,
        LyricInfo.DownloadConfig config, CancellationToken ct = default);

    /// <summary>返回该源是否支持通过 offset/page 获取下一页结果。</summary>
    bool SupportsPagination(string source);

    /// <summary>下载歌词内容</summary>
    Task<LyricInfo?> DownloadLyricAsync(SearchResult result, LyricInfo.DownloadConfig config,
        CancellationToken ct = default);

    /// <summary>重新格式化 LRC 时间标签为标准 [mm:ss.xx] 格式</summary>
    string ReformatTimetag(string lrcContent);

    /// <summary>移除 LRC 时间标签，保留纯歌词文本</summary>
    string RemoveTimetag(string lrcContent);

    /// <summary>将歌词保存为 LRC 文件，成功时返回保存的完整路径，失败返回 null</summary>
    Task<string?> SaveLrcFileAsync(string directory, MusicFile file, LyricInfo lyric,
        LyricInfo.SaveConfig config);

    /// <summary>解析 LRC 文件内容</summary>
    LyricInfo? ParseLrcContent(string lrcContent);
}
