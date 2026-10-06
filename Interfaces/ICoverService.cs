using MusicTagRecover.Models;

namespace MusicTagRecover.Interfaces;

/// <summary>
/// 封面图片服务，支持封面搜索、下载、压缩和格式验证
/// </summary>
public interface ICoverService
{
    /// <summary>搜索封面图片（所有源聚合）</summary>
    Task<IReadOnlyList<SearchResult>> SearchCoversAsync(MusicFile file, SearchCondition condition,
        CancellationToken ct = default);

    /// <summary>从指定数据源搜索封面图片</summary>
    /// <param name="source">源名称：default/netease/qq/itunes/kuwo/lastfm/musicbrainz/discogs</param>
    Task<IReadOnlyList<SearchResult>> SearchCoversFromSourceAsync(
        MusicFile file, string source, SearchCondition condition,
        CancellationToken ct = default);

    /// <summary>从指定源搜索组合标签结果。</summary>
    Task<IReadOnlyList<SearchResult>> SearchTagsFromSourceAsync(
        MusicFile file, string source, SearchCondition condition,
        CancellationToken ct = default);

    /// <summary>返回该源是否支持通过 offset/page 获取下一页结果。</summary>
    bool SupportsPagination(string source);

    /// <summary>下载封面图片；解析失败或下载内容为空时返回 null。</summary>
    Task<CoverDownloadResult?> DownloadCoverAsync(SearchResult result, CoverArt.LimitsConfig limits,
        CancellationToken ct = default);

    /// <summary>压缩封面图片，缩放并降低JPEG质量</summary>
    CoverArt? CompressCover(CoverArt cover, int maxWidth = 500, int maxHeight = 500, int quality = 85);

    /// <summary>验证封面图片是否符合格式、分辨率和大小限制</summary>
    bool ValidateCover(CoverArt cover, CoverArt.LimitsConfig limits, out string errorMessage);

    /// <summary>加载本地图片文件</summary>
    CoverArt? LoadImageFromFile(string filePath);

    /// <summary>为指定源创建带代理配置的 HttpClient（用于缩略图等场景）</summary>
    /// <param name="sourceDisplayName">源显示名（如 "Last.fm"、"酷我音乐"）</param>
    HttpClient CreateHttpClientForSource(string sourceDisplayName);

    /// <summary>下载图片字节（带 URL→文件缓存，同 URL 只下载一次）</summary>
    /// <param name="imageUrl">图片 URL</param>
    /// <param name="sourceDisplayName">源显示名（用于代理配置）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>图片字节，失败返回 null</returns>
    Task<byte[]?> DownloadImageBytesAsync(string imageUrl, string sourceDisplayName,
        CancellationToken ct = default);
}
