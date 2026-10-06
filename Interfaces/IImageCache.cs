namespace MusicTagRecover.Interfaces;

/// <summary>
/// 封面图片统一缓存服务。
///
/// 两个物理隔离的子目录：
///   - cache\history\：历史封面 + 组合标签选中封面 + 外部打开封面。
///     内容寻址（sha256+扩展名），被 SQLite 长期引用，**不参与任何自动清理**。
///     生命周期由历史记录增删驱动（单条删除按引用计数清理，ClearAll 整目录清空）。
///   - cache\img\：URL 下载性能缓存。带 SQLite url_cache 索引，启动时按容量上限 + 7 天孤儿 LRU 清理。
///
/// 同一张图无论来自哪个源、用于历史/搜索/外部打开，磁盘上只存一份（内容寻址去重）。
/// </summary>
public interface IImageCache
{
    // === 历史封面目录（cache\history\，不自动清理）===

    /// <summary>历史封面目录绝对路径。</summary>
    string HistoryDir { get; }

    /// <summary>内容寻址存储到 cache\history\，返回相对文件名；内容相同只存一份。</summary>
    string? StoreHistory(byte[] data);

    /// <summary>读取历史封面字节；不存在返回 null。</summary>
    byte[]? ReadHistory(string relPath);

    /// <summary>相对路径→绝对路径。</summary>
    string GetHistoryFullPath(string relPath);

    /// <summary>历史封面文件是否存在。</summary>
    bool HistoryExists(string relPath);

    /// <summary>删除历史封面文件（调用方需先确认无引用）。</summary>
    void DeleteHistory(string relPath);

    // === URL 下载缓存目录（cache\img\，自动 LRU 清理）===

    /// <summary>URL 下载缓存目录绝对路径。</summary>
    string UrlCacheDir { get; }

    /// <summary>
    /// 按 URL 命中缓存则返回字节；未命中调 fetcher 下载、内容寻址写入 cache\img\ 并记 url_cache 索引。
    /// fetcher 由调用方提供（封装代理/Referer/超时等 HTTP 细节），IImageCache 不碰网络。
    /// </summary>
    Task<byte[]?> GetOrDownloadAsync(string url,
        Func<CancellationToken, Task<byte[]?>> fetcher, CancellationToken ct);

    // === 手动清理（设置页用）===

    /// <summary>cache\history\ 递归占用字节数。</summary>
    long GetHistorySize();

    /// <summary>cache\img\ 递归占用字节数。</summary>
    long GetUrlCacheSize();

    /// <summary>清空 cache\img\ 全部文件 + 清空 url_cache 表（性能缓存，安全）。</summary>
    void ClearUrlCache();

    /// <summary>删除 cache\history\ 中未被任何 tagshistory 记录引用的孤儿文件。</summary>
    void ClearUnreferencedHistory();

    /// <summary>清空 cache\history\ 全部文件（含被引用的）。历史记录文本保留，仅封面不可再回显。</summary>
    void ClearHistory();

    /// <summary>设置「被引用封面路径集合」提供者（启动时注入，ClearUnreferencedHistory/Sweep 据此保护）。</summary>
    void SetReferencedCoverPathsProvider(Func<IReadOnlyCollection<string>> provider);

    /// <summary>启动时执行一次：URL 缓存容量上限 LRU 淘汰 + 7 天孤儿清理。绝不触碰 cache\history\。</summary>
    void Sweep();
}
