using System.Data.SQLite;
using MusicTagRecover.Interfaces;

namespace MusicTagRecover.Services;

/// <summary>
/// 封面图片统一缓存服务实现。见 <see cref="IImageCache"/>。
///
/// 历史目录 cache\history\：纯文件内容寻址存储，无索引表（引用关系由 MusicTagRecover.db 的 cover_path 持有）。
/// URL 缓存目录 cache\img\：内容寻址文件 + cache\img\index.db 的 url_cache(url PK, hash, ext, last_access) 索引。
/// </summary>
public class ImageCacheService : IImageCache
{
    private readonly ILoggerService _logger;
    private readonly ISettingsService _settings;
    private readonly string _historyDir;
    private readonly string _urlCacheDir;
    private readonly string _indexDbPath;
    private readonly object _dbLock = new();
    private volatile bool _dbReady;

    /// <summary>URL 缓存容量默认上限（设置未配置时用）：256MB。</summary>
    private const int DefaultMaxCacheMb = 256;

    /// <summary>孤儿文件清理阈值：最后写入时间早于此天数且不在 url_cache 中的文件删除。</summary>
    private const int OrphanAgeDays = 7;

    /// <summary>设置页清理历史孤儿时需要引用集合 —— 由外部（TagHistoryService）注入，避免循环依赖。</summary>
    private Func<IReadOnlyCollection<string>>? _referencedCoverPathsProvider;

    public ImageCacheService(ILoggerService logger, ISettingsService settings)
    {
        _logger = logger;
        _settings = settings;
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _historyDir = Path.Combine(baseDir, "cache", "history");
        _urlCacheDir = Path.Combine(baseDir, "cache", "img");
        _indexDbPath = Path.Combine(_urlCacheDir, "index.db");
    }

    /// <summary>当前 URL 缓存容量上限（字节），来自设置。</summary>
    private long MaxCacheBytes
    {
        get
        {
            var mb = _settings.UrlCacheMaxSizeMb;
            if (mb <= 0) mb = DefaultMaxCacheMb;
            return (long)mb * 1024 * 1024;
        }
    }

    /// <summary>设置历史封面引用集合提供者（由 TagHistoryService 在初始化后注入）。</summary>
    public void SetReferencedCoverPathsProvider(Func<IReadOnlyCollection<string>> provider)
        => _referencedCoverPathsProvider = provider;

    public string HistoryDir => _historyDir;
    public string UrlCacheDir => _urlCacheDir;

    // ============================================================
    // 历史封面目录
    // ============================================================

    public string? StoreHistory(byte[] data)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            Directory.CreateDirectory(_historyDir);
            var fileName = BuildContentFileName(data);
            var fullPath = Path.Combine(_historyDir, fileName);
            if (!File.Exists(fullPath))
                AtomicWrite(fullPath, data);
            return fileName;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ImageCache] StoreHistory fail");
            return null;
        }
    }

    public byte[]? ReadHistory(string relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return null;
        try
        {
            var fullPath = GetHistoryFullPath(relPath);
            return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
        }
        catch { return null; }
    }

    public string GetHistoryFullPath(string relPath)
        => Path.Combine(_historyDir, Path.GetFileName(relPath));

    public bool HistoryExists(string relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return false;
        try { return File.Exists(GetHistoryFullPath(relPath)); }
        catch { return false; }
    }

    public void DeleteHistory(string relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return;
        try
        {
            var fullPath = GetHistoryFullPath(relPath);
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch (Exception ex) { _logger.Error(ex, "[ImageCache] DeleteHistory fail"); }
    }

    // ============================================================
    // URL 下载缓存目录
    // ============================================================

    public async Task<byte[]?> GetOrDownloadAsync(string url,
        Func<CancellationToken, Task<byte[]?>> fetcher, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url) || fetcher == null) return null;

        EnsureDbReady();

        // 命中检查
        var hit = TryReadUrlCache(url);
        if (hit != null)
        {
            _logger.Debug("[ImageCache] URL 缓存命中: {0}...",
                url.Length > 80 ? url.Substring(0, 80) : url);
            return hit;
        }

        // 未命中 → 下载
        var data = await fetcher(ct).ConfigureAwait(false);
        if (data == null || data.Length == 0) return null;

        // 内容寻址写入 + 记索引
        try
        {
            Directory.CreateDirectory(_urlCacheDir);
            var fileName = BuildContentFileName(data);
            var fullPath = Path.Combine(_urlCacheDir, fileName);
            if (!File.Exists(fullPath))
                AtomicWrite(fullPath, data);
            InsertUrlCache(url, fileName);
            _logger.Debug("[ImageCache] URL 已缓存: {0} ({1} bytes)", fileName, data.Length);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ImageCache] 写入 URL 缓存失败");
        }
        return data;
    }

    /// <summary>命中则读文件并更新 last_access；未命中返回 null。</summary>
    private byte[]? TryReadUrlCache(string url)
    {
        try
        {
            string? fileName = null;
            lock (_dbLock)
            using (var conn = OpenIndexConn())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "select hash from url_cache where url = ?";
                cmd.Parameters.AddWithValue("", url);
                var r = cmd.ExecuteScalar();
                if (r == null || r == DBNull.Value) return null;
                fileName = (string)r;

                // 更新 last_access
                cmd.CommandText = "update url_cache set last_access = ? where url = ?";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("", url);
                cmd.ExecuteNonQuery();
            }
            var fullPath = Path.Combine(_urlCacheDir, fileName);
            return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
        }
        catch { return null; }
    }

    private void InsertUrlCache(string url, string fileName)
    {
        try
        {
            lock (_dbLock)
            using (var conn = OpenIndexConn())
            using (var tx = conn.BeginTransaction())
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"insert or replace into url_cache(url, hash, last_access)
                                    values(?, ?, ?)";
                cmd.Parameters.AddWithValue("", url);
                cmd.Parameters.AddWithValue("", fileName);
                cmd.Parameters.AddWithValue("", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
        }
        catch (Exception ex) { _logger.Error(ex, "[ImageCache] InsertUrlCache fail"); }
    }

    // ============================================================
    // 手动清理
    // ============================================================

    public long GetHistorySize() => GetDirSize(_historyDir);
    public long GetUrlCacheSize() => GetDirSize(_urlCacheDir);

    public void ClearUrlCache()
    {
        try
        {
            lock (_dbLock)
            using (var conn = OpenIndexConn())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "delete from url_cache";
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { _logger.Error(ex, "[ImageCache] 清空 url_cache 表失败"); }

        DeleteFilesInDir(_urlCacheDir, keepIndexDb: true);
        _logger.Info("[ImageCache] 已清空 URL 下载缓存");
    }

    public void ClearUnreferencedHistory()
    {
        var referenced = _referencedCoverPathsProvider?.Invoke()
            ?? Array.Empty<string>();
        var refSet = new HashSet<string>(referenced, StringComparer.OrdinalIgnoreCase);

        int deleted = 0;
        try
        {
            if (!Directory.Exists(_historyDir)) return;
            foreach (var file in Directory.EnumerateFiles(_historyDir))
            {
                var name = Path.GetFileName(file);
                if (refSet.Contains(name)) continue;
                try { File.Delete(file); deleted++; }
                catch { }
            }
        }
        catch (Exception ex) { _logger.Error(ex, "[ImageCache] ClearUnreferencedHistory fail"); }
        _logger.Info("[ImageCache] 清理未引用历史封面: 删除 {0} 个", deleted);
    }

    public void ClearHistory()
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(_historyDir)) return;
            foreach (var file in Directory.EnumerateFiles(_historyDir))
            {
                try { File.Delete(file); deleted++; }
                catch { }
            }
        }
        catch (Exception ex) { _logger.Error(ex, "[ImageCache] ClearHistory fail"); }
        _logger.Info("[ImageCache] 清空历史封面目录: 删除 {0} 个", deleted);
    }

    public void Sweep()
    {
        try
        {
            EnsureDbReady();
            SweepBySize();
            SweepOrphans();
        }
        catch (Exception ex) { _logger.Error(ex, "[ImageCache] Sweep fail"); }
    }

    /// <summary>容量超 MaxCacheBytes 时按 last_access 最旧优先淘汰。</summary>
    private void SweepBySize()
    {
        long total = GetDirSize(_urlCacheDir);
        if (total <= MaxCacheBytes) return;

        // 取全部条目按 last_access 升序
        var entries = new List<(string url, string hash, long lastAccess)>();
        lock (_dbLock)
        using (var conn = OpenIndexConn())
        using (var cmd = conn.CreateCommand())
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                entries.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));

        entries.Sort((a, b) => a.lastAccess.CompareTo(b.lastAccess));

        foreach (var e in entries)
        {
            if (total <= MaxCacheBytes) break;
            var fullPath = Path.Combine(_urlCacheDir, e.hash);
            try
            {
                if (File.Exists(fullPath))
                {
                    var len = new FileInfo(fullPath).Length;
                    File.Delete(fullPath);
                    total -= len;
                }
            }
            catch { }
            // 删索引
            lock (_dbLock)
            using (var conn = OpenIndexConn())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "delete from url_cache where url = ?";
                cmd.Parameters.AddWithValue("", e.url);
                cmd.ExecuteNonQuery();
            }
        }
        _logger.Info("[ImageCache] LRU 淘汰后 URL 缓存大小: {0} bytes", total);
    }

    /// <summary>删除 cache\img\ 中不在 url_cache、最后写入早于 OrphanAgeDays 的孤儿。</summary>
    private void SweepOrphans()
    {
        if (!Directory.Exists(_urlCacheDir)) return;

        var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_dbLock)
        using (var conn = OpenIndexConn())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select hash from url_cache";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                indexed.Add(reader.GetString(0));
        }

        var cutoff = DateTime.UtcNow.AddDays(-OrphanAgeDays);
        int deleted = 0;
        foreach (var file in Directory.EnumerateFiles(_urlCacheDir))
        {
            var name = Path.GetFileName(file);
            if (name == "index.db") continue;
            if (indexed.Contains(name)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch { }
        }
        if (deleted > 0)
            _logger.Info("[ImageCache] 清理孤儿缓存文件: {0} 个", deleted);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private void EnsureDbReady()
    {
        if (_dbReady) return;
        lock (_dbLock)
        {
            if (_dbReady) return;
            try
            {
                Directory.CreateDirectory(_urlCacheDir);
                using (var conn = OpenIndexConn())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"create table if not exists url_cache(
                        url text primary key,
                        hash text not null,
                        last_access integer not null)";
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = "create index if not exists uc_idx_access on url_cache(last_access)";
                    cmd.ExecuteNonQuery();
                }
                _dbReady = true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[ImageCache] 初始化 index.db 失败");
            }
        }
    }

    private SQLiteConnection OpenIndexConn()
    {
        var conn = new SQLiteConnection($"Data Source={_indexDbPath}");
        conn.Open();
        return conn;
    }

    /// <summary>内容寻址文件名：sha256(content) + 嗅探扩展名。</summary>
    private static string BuildContentFileName(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        return hash + SniffImageExtension(data);
    }

    /// <summary>按魔术字嗅探图片扩展名，无法识别默认 .jpg。</summary>
    private static string SniffImageExtension(byte[] data)
    {
        if (data == null || data.Length < 12) return ".jpg";
        // PNG: 89 50 4E 47
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return ".png";
        // JPEG: FF D8 FF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ".jpg";
        // GIF: 47 49 46 38
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38) return ".gif";
        // BMP: 42 4D
        if (data[0] == 0x42 && data[1] == 0x4D) return ".bmp";
        // WEBP: RIFF....WEBP
        if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return ".webp";
        return ".jpg";
    }

    /// <summary>原子写：先写 .tmp 再 File.Replace。</summary>
    private static void AtomicWrite(string fullPath, byte[] data)
    {
        var tmp = fullPath + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (File.Exists(fullPath))
        {
            File.Replace(tmp, fullPath, null);
        }
        else
        {
            File.Move(tmp, fullPath);
        }
    }

    private static long GetDirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long sum = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { sum += new FileInfo(f).Length; } catch { }
        }
        return sum;
    }

    /// <summary>删除目录下所有文件（保留 index.db 时跳过它）。</summary>
    private void DeleteFilesInDir(string dir, bool keepIndexDb)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            if (keepIndexDb && Path.GetFileName(f) == "index.db") continue;
            try { File.Delete(f); } catch { }
        }
    }
}
