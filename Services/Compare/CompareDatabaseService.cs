using System.Data.SQLite;
using MusicTagRecover.Models.Compare;

namespace MusicTagRecover.Services.Compare;

/// <summary>
/// 文件比对模块本地数据库服务（SQLite，库文件 CompareModule.db 位于程序目录）。
///   - compare_files：扫描采集的文件全量元数据，含唯一路径索引 + MD5/名称/标签匹配索引，
///     二次扫描按「路径+大小+修改时间」命中缓存直接复用 MD5 与标签，大幅提速
///   - compare_delete_log：删除操作全链路日志（时间戳/操作人/操作路径），删除仅做状态标记不抹记录
/// 每次操作独立开连接（与 TagHistoryService 同模式），天然线程安全。
/// </summary>
public class CompareDatabaseService
{
    private readonly string _dbPath;
    private readonly object _initLock = new();
    private bool _initialized;

    public CompareDatabaseService(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "CompareModule.db");
    }

    private SQLiteConnection GetConnection()
    {
        var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
        conn.Open();
        return conn;
    }

    /// <summary>确保库表与索引存在（幂等，可多线程并发调用）。</summary>
    public void Initialize()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            using var conn = GetConnection();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
create table if not exists compare_files(
    id               integer primary key autoincrement,
    full_path        text    not null,
    root_dir         text    not null default '',
    dir              text    not null default '',
    name             text    not null default '',
    ext              text    not null default '',
    size             integer not null default 0,
    mtime_ticks      integer not null default 0,
    md5              text,
    title            text,
    artist           text,
    is_media         integer not null default 0,
    status           integer not null default 0,
    deleted_at_ticks integer,
    scanned_at_ticks integer not null default 0
);
create unique index if not exists idx_cf_fullpath
    on compare_files(full_path collate nocase);
create index if not exists idx_cf_md5 on compare_files(md5);
create index if not exists idx_cf_namesize on compare_files(name, ext, size);
create index if not exists idx_cf_tag on compare_files(title, artist);

create table if not exists compare_delete_log(
    id       integer primary key autoincrement,
    op_time  text not null,
    operator text not null,
    op_type  text not null,
    path     text not null,
    detail   text not null default ''
);
create index if not exists idx_dl_time on compare_delete_log(op_time);
create index if not exists idx_dl_path on compare_delete_log(path);
create index if not exists idx_dl_type on compare_delete_log(op_type);";
                cmd.ExecuteNonQuery();
            }
            _initialized = true;
        }
    }

    // ============================================================
    // 文件元数据
    // ============================================================

    /// <summary>
    /// 加载全部「正常状态」的文件记录到内存字典（键=完整路径，忽略大小写），
    /// 供扫描服务做缓存命中判定。
    /// </summary>
    public Dictionary<string, CompareFileRecord> LoadAllNormal()
    {
        Initialize();
        var dict = new Dictionary<string, CompareFileRecord>(StringComparer.OrdinalIgnoreCase);
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select id, full_path, root_dir, dir, name, ext, size, mtime_ticks, " +
                          "md5, title, artist, is_media, status, deleted_at_ticks, scanned_at_ticks " +
                          "from compare_files where status = 0";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rec = new CompareFileRecord
            {
                Id = reader.GetInt64(0),
                FullPath = reader.GetString(1),
                RootDir = reader.GetString(2),
                Dir = reader.GetString(3),
                Name = reader.GetString(4),
                Ext = reader.GetString(5),
                Size = reader.GetInt64(6),
                MtimeTicks = reader.GetInt64(7),
                Md5 = reader.IsDBNull(8) ? null : reader.GetString(8),
                Title = reader.IsDBNull(9) ? null : reader.GetString(9),
                Artist = reader.IsDBNull(10) ? null : reader.GetString(10),
                IsMedia = reader.GetInt64(11) != 0,
                Status = (int)reader.GetInt64(12),
                DeletedAtTicks = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                ScannedAtTicks = reader.GetInt64(14),
            };
            dict[rec.FullPath] = rec;
        }
        return dict;
    }

    /// <summary>
    /// 批量写入/更新文件记录（按完整路径 upsert；分批事务避免超大事务锁库过长）。
    /// </summary>
    public void UpsertFiles(IEnumerable<CompareFileRecord> files)
    {
        Initialize();
        var batch = new List<CompareFileRecord>(1000);
        foreach (var f in files)
        {
            batch.Add(f);
            if (batch.Count >= 1000)
            {
                UpsertBatch(batch);
                batch.Clear();
            }
        }
        if (batch.Count > 0) UpsertBatch(batch);
    }

    private void UpsertBatch(List<CompareFileRecord> batch)
    {
        using var conn = GetConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
insert into compare_files(
    full_path, root_dir, dir, name, ext, size, mtime_ticks,
    md5, title, artist, is_media, status, deleted_at_ticks, scanned_at_ticks)
values(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
on conflict(full_path collate nocase) do update set
    root_dir=excluded.root_dir, dir=excluded.dir, name=excluded.name, ext=excluded.ext,
    size=excluded.size, mtime_ticks=excluded.mtime_ticks, md5=excluded.md5,
    title=excluded.title, artist=excluded.artist, is_media=excluded.is_media,
    status=excluded.status, deleted_at_ticks=excluded.deleted_at_ticks,
    scanned_at_ticks=excluded.scanned_at_ticks";
        // 预建 14 个参数，逐行赋值复用同一命令（批量插入性能关键）
        for (var i = 0; i < 14; i++)
            cmd.Parameters.AddWithValue("@p" + i, DBNull.Value);
        foreach (var f in batch)
        {
            cmd.Parameters["@p0"].Value = f.FullPath;
            cmd.Parameters["@p1"].Value = f.RootDir;
            cmd.Parameters["@p2"].Value = f.Dir;
            cmd.Parameters["@p3"].Value = f.Name;
            cmd.Parameters["@p4"].Value = f.Ext;
            cmd.Parameters["@p5"].Value = f.Size;
            cmd.Parameters["@p6"].Value = f.MtimeTicks;
            cmd.Parameters["@p7"].Value = (object?)f.Md5 ?? DBNull.Value;
            cmd.Parameters["@p8"].Value = (object?)f.Title ?? DBNull.Value;
            cmd.Parameters["@p9"].Value = (object?)f.Artist ?? DBNull.Value;
            cmd.Parameters["@p10"].Value = f.IsMedia ? 1 : 0;
            cmd.Parameters["@p11"].Value = f.Status;
            cmd.Parameters["@p12"].Value = (object?)f.DeletedAtTicks ?? DBNull.Value;
            cmd.Parameters["@p13"].Value = f.ScannedAtTicks;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 按路径更新记录状态（删除置 1 / 恢复归 0）。记录永不物理删除。
    /// </summary>
    public void SetStatusByPath(string fullPath, int status, long? deletedAtTicks)
    {
        Initialize();
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "update compare_files set status = ?, deleted_at_ticks = ? " +
                          "where full_path = ? collate nocase";
        cmd.Parameters.AddWithValue("", status);
        cmd.Parameters.AddWithValue("", (object?)deletedAtTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("", fullPath);
        cmd.ExecuteNonQuery();
    }

    // ============================================================
    // 删除操作日志
    // ============================================================

    /// <summary>写入一条删除/恢复操作日志。</summary>
    public void LogDelete(string opType, string path, string detail)
    {
        Initialize();
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "insert into compare_delete_log(op_time, operator, op_type, path, detail) " +
                          "values(?, ?, ?, ?, ?)";
        cmd.Parameters.AddWithValue("", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("", Environment.UserDomainName + "\\" + Environment.UserName);
        cmd.Parameters.AddWithValue("", opType);
        cmd.Parameters.AddWithValue("", path);
        cmd.Parameters.AddWithValue("", detail ?? "");
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 查询删除历史：支持时间范围、路径关键字（包含匹配）、操作类型联合筛选，按时间倒序。
    /// </summary>
    public List<CompareDeleteLogRecord> QueryLogs(
        DateTime? from, DateTime? to, string? pathKeyword, string? opType)
    {
        Initialize();
        var conditions = new List<string>();
        if (from.HasValue) conditions.Add("op_time >= ?");
        if (to.HasValue) conditions.Add("op_time <= ?");
        if (!string.IsNullOrWhiteSpace(pathKeyword)) conditions.Add("path like ?");
        if (!string.IsNullOrWhiteSpace(opType)) conditions.Add("op_type = ?");

        var sql = "select id, op_time, operator, op_type, path, detail from compare_delete_log";
        if (conditions.Count > 0) sql += " where " + string.Join(" and ", conditions);
        sql += " order by op_time desc, id desc";

        var result = new List<CompareDeleteLogRecord>();
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (from.HasValue)
            cmd.Parameters.AddWithValue("", from.Value.ToString("yyyy-MM-dd 00:00:00"));
        if (to.HasValue)
            cmd.Parameters.AddWithValue("", to.Value.ToString("yyyy-MM-dd 23:59:59"));
        if (!string.IsNullOrWhiteSpace(pathKeyword))
            cmd.Parameters.AddWithValue("", "%" + pathKeyword.Trim() + "%");
        if (!string.IsNullOrWhiteSpace(opType))
            cmd.Parameters.AddWithValue("", opType);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rec = new CompareDeleteLogRecord
            {
                Id = reader.GetInt64(0),
                OpTime = DateTime.TryParse(reader.GetString(1), out var t) ? t : DateTime.MinValue,
                Operator = reader.GetString(2),
                OpType = reader.GetString(3),
                Path = reader.GetString(4),
                Detail = reader.GetString(5),
            };
            result.Add(rec);
        }
        return result;
    }
}
