using System.Data.SQLite;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;

namespace MusicTagRecover.Services;

/// <summary>
/// 标签历史服务 — 管理 tagshistory SQLite 数据库 + 封面文件
///   - serial 格式为 "{prefix}-{counter}"（prefix 每会话自增，counter 每记录自增）
///   - 每个文件最多保留 5 条历史（超出则删除最旧的）
///   - 全部文本字段（含歌词）存 SQLite，封面以内容寻址存 cache\history\ 目录（路径记入 cover_path）
/// </summary>
public class TagHistoryService : ITagHistoryService
{
    private readonly string _dbPath;
    private readonly IImageCache _imageCache;
    private long _serialPrefix;
    private long _serialCounter;
    private bool _initialized;

    private const int MaxRecordsPerFile = 5;

    public TagHistoryService(IImageCache imageCache, string? dbPath = null)
    {
        _imageCache = imageCache;
        _dbPath = dbPath ?? Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "MusicTagRecover.db");
    }

    public void Initialize()
    {
        if (_initialized) return;
        EnsureDatabase();

        using var conn = GetConnection();
        conn.Open();

        // 迁移旧表：补全缺失的列
        MigrateIfNeeded(conn);

        // 读取或初始化 serial 前缀
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select thserial_prefix from config";
        var result = cmd.ExecuteScalar();
        if (result != null && result != DBNull.Value)
        {
            _serialPrefix = Convert.ToInt64(result);
        }
        else
        {
            _serialPrefix = 1;
            cmd.CommandText = "insert into config(thserial_prefix) values(1)";
            cmd.ExecuteNonQuery();
        }

        // 每会话递增前缀（启动时 prefix + 1）
        _serialPrefix++;
        cmd.CommandText = "update config set thserial_prefix = ?";
        cmd.Parameters.AddWithValue("", _serialPrefix);
        cmd.ExecuteNonQuery();

        _serialCounter = 0;
        _initialized = true;

        // 一次性迁移旧 temp\img\ 封面到 cache\history\
        MigrateOldCoverDir();
    }

    public string? TryAddHistory(string filePath, MusicFile file)
    {
        if (!_initialized) Initialize();

        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            // 先查当前文件有多少条历史
            var list = GetHistoryInternal(conn, filePath, false);

            // 超出数量则删最旧的（保留 MaxRecordsPerFile 条）
            if (list.Count > MaxRecordsPerFile)
            {
                int deleteCount = list.Count - MaxRecordsPerFile;
                for (int i = 0; i < deleteCount; i++)
                {
                    using var delCmd = conn.CreateCommand();
                    delCmd.Transaction = tx;
                    delCmd.CommandText = "delete from tagshistory where serial = ?";
                    delCmd.Parameters.AddWithValue("", list[i].Serial);
                    delCmd.ExecuteNonQuery();
                }
            }

            // 生成 serial
            _serialCounter++;
            var serial = $"{_serialPrefix}-{_serialCounter}";

            // 保存封面到缓存目录（内容寻址，cache\history\）
            string? coverPath = null;
            if (file.CoverArtData != null && file.CoverArtData.Length > 0)
            {
                coverPath = _imageCache.StoreHistory(file.CoverArtData);
            }

            // 插入记录（含全部字段）
            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = @"
                insert into tagshistory(
                    serial, filepath,
                    title, artist, album, year, trackstr, discstr,
                    genre, albumartist, composer, lyricist, comment, lyrics,
                    cover_path
                ) values(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)";
            insertCmd.Parameters.AddWithValue("", serial);
            insertCmd.Parameters.AddWithValue("", filePath);
            insertCmd.Parameters.AddWithValue("", file.Title ?? "");
            insertCmd.Parameters.AddWithValue("", file.Artist ?? "");
            insertCmd.Parameters.AddWithValue("", file.Album ?? "");
            insertCmd.Parameters.AddWithValue("", file.Year > 0 ? file.Year.ToString() : "");
            insertCmd.Parameters.AddWithValue("", file.Track > 0 ? file.Track.ToString() : "");
            insertCmd.Parameters.AddWithValue("", file.Disc > 0 ? file.Disc.ToString() : "");
            insertCmd.Parameters.AddWithValue("", file.Genre ?? "");
            insertCmd.Parameters.AddWithValue("", file.AlbumArtist ?? "");
            insertCmd.Parameters.AddWithValue("", file.Composer ?? "");
            insertCmd.Parameters.AddWithValue("", file.Lyricist ?? "");
            insertCmd.Parameters.AddWithValue("", file.Comment ?? "");
            insertCmd.Parameters.AddWithValue("", file.Lyrics ?? "");
            insertCmd.Parameters.AddWithValue("", coverPath ?? (object)DBNull.Value);
            insertCmd.ExecuteNonQuery();

            tx.Commit();
            return serial;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("AddTagsHistory fail:" + ex.Message);
            return null;
        }
    }

    public List<TagHistoryRecord> GetHistory(string filePath, bool mostRecentFirst = true)
    {
        if (!_initialized) Initialize();

        try
        {
            using var conn = GetConnection();
            conn.Open();
            return GetHistoryInternal(conn, filePath, mostRecentFirst);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("GetTagsHistory fail:" + ex.Message);
            return new List<TagHistoryRecord>();
        }
    }

    /// <summary>读取历史记录的封面数据。无封面或文件不存在返回 null</summary>
    public byte[]? ReadCoverData(string serial)
    {
        try
        {
            if (!_initialized) Initialize();

            var record = FindRecordBySerial(serial);
            if (record == null || string.IsNullOrEmpty(record.CoverPath)) return null;

            return _imageCache.ReadHistory(record.CoverPath!);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("ReadTagsHistoryCover fail:" + ex.Message);
            return null;
        }
    }

    public bool CoverExists(string? coverPath)
    {
        if (string.IsNullOrEmpty(coverPath)) return false;

        try
        {
            return _imageCache.HistoryExists(coverPath!);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("CheckTagsHistoryCover fail:" + ex.Message);
            return false;
        }
    }

    public void DeleteHistory(string serial)
    {
        if (!_initialized) Initialize();

        try
        {
            string? coverPath;
            using (var conn = GetConnection())
            {
                conn.Open();
                using var qCmd = conn.CreateCommand();
                qCmd.CommandText = "select cover_path from tagshistory where serial = ?";
                qCmd.Parameters.AddWithValue("", serial);
                coverPath = qCmd.ExecuteScalar() as string;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "delete from tagshistory where serial = ?";
                cmd.Parameters.AddWithValue("", serial);
                cmd.ExecuteNonQuery();
            }

            // 删行后若该封面无其它引用则删文件
            if (!string.IsNullOrEmpty(coverPath))
                MaybeDeleteCoverFile(coverPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("DeleteTagsHistory fail:" + ex.Message);
        }
    }

    public void DeleteHistoryByFilePath(string filePath)
    {
        if (!_initialized) Initialize();

        try
        {
            var coverPaths = new List<string>();
            using (var conn = GetConnection())
            {
                conn.Open();
                using var qCmd = conn.CreateCommand();
                qCmd.CommandText = "select cover_path from tagshistory where filepath = ?";
                qCmd.Parameters.AddWithValue("", filePath);
                using var reader = qCmd.ExecuteReader();
                while (reader.Read())
                {
                    var p = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (!string.IsNullOrEmpty(p)) coverPaths.Add(p);
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "delete from tagshistory where filepath = ?";
                cmd.Parameters.AddWithValue("", filePath);
                cmd.ExecuteNonQuery();
            }

            foreach (var p in coverPaths)
                MaybeDeleteCoverFile(p);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("DeleteTagsHistoryByFilePath fail:" + ex.Message);
        }
    }

    public IReadOnlyCollection<string> GetAllReferencedCoverPaths()
    {
        if (!_initialized) Initialize();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select distinct cover_path from tagshistory where cover_path is not null and cover_path <> ''";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetString(0));
        }
        catch { }
        return result;
    }

    /// <summary>若该 cover_path 已无任何历史记录引用，则删除缓存文件。</summary>
    private void MaybeDeleteCoverFile(string coverPath)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select count(*) from tagshistory where cover_path = ?";
            cmd.Parameters.AddWithValue("", coverPath);
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            if (count == 0)
                _imageCache.DeleteHistory(coverPath);
        }
        catch { }
    }

    public void ClearAll()
    {
        if (!_initialized) Initialize();

        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "delete from tagshistory; update config set thserial_prefix = 1";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "VACUUM";
            cmd.ExecuteNonQuery();
            _serialPrefix = 1;
            _serialCounter = 0;

            // 历史已全部删除 → 清空整个历史封面目录
            try
            {
                if (Directory.Exists(_imageCache.HistoryDir))
                    Directory.Delete(_imageCache.HistoryDir, true);
                Directory.CreateDirectory(_imageCache.HistoryDir);
            }
            catch { }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("DeleteAllTagsHistory fail:" + ex.Message);
        }
    }

    // ============================================================
    // Internal Helpers
    // ============================================================

    private SQLiteConnection GetConnection()
    {
        return new SQLiteConnection($"Data Source={_dbPath}");
    }

    private void EnsureDatabase()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (dir != null) Directory.CreateDirectory(dir);

        using var conn = GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            create table if not exists tagshistory (
                serial varchar(21) primary key,
                filepath varchar(256) not null,
                createtime datetime default(strftime('%Y-%m-%d %H:%M:%f', 'now')),
                title varchar(64),
                artist varchar(64),
                album varchar(64),
                year varchar(4),
                trackstr varchar(2),
                discstr varchar(2),
                genre varchar(32),
                albumartist varchar(64),
                composer varchar(64),
                lyricist varchar(64),
                comment varchar(512),
                lyrics text,
                cover_path varchar(128)
            );
            create index if not exists th_idx01 on tagshistory(filepath);
            create table if not exists config (thserial_prefix integer);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>迁移旧表：尝试补全新列（旧数据库缺少扩展列时 ALTER TABLE）</summary>
    private void MigrateIfNeeded(SQLiteConnection conn)
    {
        var existing = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(tagshistory)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(1));
        }

        var newCols = new[] { "genre", "albumartist", "composer", "lyricist", "comment", "lyrics", "cover_path" };
        foreach (var col in newCols)
        {
            if (!existing.Contains(col, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"alter table tagshistory add column {col}";
                    alter.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Migrate add {col} fail: {ex.Message}");
                }
            }
        }
    }

    private TagHistoryRecord? FindRecordBySerial(string serial)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select *, datetime(createtime, 'localtime') ctime from tagshistory where serial = ?";
            cmd.Parameters.AddWithValue("", serial);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                return ReadRecord(reader);
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 一次性迁移：旧版本封面存在 &lt;appdir&gt;\temp\img\{md5}.{ext}，
    /// 迁移到新的 cache\history\{sha256}.{ext} 内容寻址存储，并更新 cover_path 列。
    /// best-effort：失败则跳过，旧记录封面不显示（不崩溃）。
    /// </summary>
    private void MigrateOldCoverDir()
    {
        var oldDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp", "img");
        if (!Directory.Exists(oldDir)) return;

        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            foreach (var oldFile in Directory.EnumerateFiles(oldDir))
            {
                byte[] data;
                try { data = File.ReadAllBytes(oldFile); }
                catch { continue; }

                var newRel = _imageCache.StoreHistory(data);
                if (string.IsNullOrEmpty(newRel)) continue;

                var oldRel = Path.GetFileName(oldFile);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "update tagshistory set cover_path = ? where cover_path = ?";
                cmd.Parameters.AddWithValue("", newRel);
                cmd.Parameters.AddWithValue("", oldRel);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("MigrateOldCoverDir fail:" + ex.Message);
        }

        // 迁移完成后尝试删除旧目录
        try { Directory.Delete(oldDir, true); }
        catch { }
    }

    private List<TagHistoryRecord> GetHistoryInternal(SQLiteConnection conn, string filePath, bool mostRecentFirst)
    {
        var result = new List<TagHistoryRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select *, datetime(createtime, 'localtime') ctime from tagshistory where filepath = ? order by createtime " +
                          (mostRecentFirst ? "desc" : "");
        cmd.Parameters.AddWithValue("", filePath);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(ReadRecord(reader));
        return result;
    }

    private static TagHistoryRecord ReadRecord(SQLiteDataReader reader)
    {
        string? Get(string col) => reader[col] as string ?? (reader.IsDBNull(reader.GetOrdinal(col)) ? null : reader.GetString(reader.GetOrdinal(col)));

        return new TagHistoryRecord
        {
            Serial = reader.GetString(reader.GetOrdinal("serial")),
            FilePath = reader.GetString(reader.GetOrdinal("filepath")),
            CreateTime = reader.GetDateTime(reader.GetOrdinal("ctime")),
            Title = Get("title"),
            Artist = Get("artist"),
            Album = Get("album"),
            Year = Get("year"),
            TrackStr = Get("trackstr"),
            DiscStr = Get("discstr"),
            Genre = Get("genre"),
            AlbumArtist = Get("albumartist"),
            Composer = Get("composer"),
            Lyricist = Get("lyricist"),
            Comment = Get("comment"),
            Lyrics = Get("lyrics"),
            CoverPath = Get("cover_path"),
        };
    }
}
