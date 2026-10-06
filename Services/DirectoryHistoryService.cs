using System.Data.SQLite;
using MusicTagRecover.Models;

namespace MusicTagRecover.Services;

/// <summary>
/// 工作目录历史库（SQLite，库文件 DirectoryHistory.db 位于程序目录）。
/// 「文件 → 添加目录」把用户选中的目录持久化到这里；「管理目录」读取/切换/删除这些条目。
/// 每次操作独立开连接（与 CompareDatabaseService 同模式），天然线程安全。
/// </summary>
public class DirectoryHistoryService
{
    private readonly string _dbPath;
    private readonly object _initLock = new();
    private bool _initialized;

    public DirectoryHistoryService(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "DirectoryHistory.db");
    }

    private SQLiteConnection GetConnection()
    {
        var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
        conn.Open();
        return conn;
    }

    /// <summary>确保库表存在（幂等）。</summary>
    public void Initialize()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            using var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
create table if not exists dir_history(
    id                integer primary key autoincrement,
    path              text    not null collate nocase unique,
    include_sub_dir   integer not null default 1,
    added_ticks      integer not null,
    last_used_ticks  integer not null default 0
);";
            cmd.ExecuteNonQuery();
            _initialized = true;
        }
    }

    /// <summary>读取全部目录条目（最近使用优先，其次最近添加）。</summary>
    public List<DirectoryHistoryEntry> GetAll()
    {
        Initialize();
        var result = new List<DirectoryHistoryEntry>();
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"select id, path, include_sub_dir, added_ticks, last_used_ticks
                           from dir_history
                           order by last_used_ticks desc, added_ticks desc";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new DirectoryHistoryEntry
            {
                Id = reader.GetInt64(0),
                Path = reader.GetString(1),
                IncludeSubDir = reader.GetInt32(2) != 0,
                AddedTicks = reader.GetInt64(3),
                LastUsedTicks = reader.GetInt64(4),
            });
        }
        return result;
    }

    /// <summary>添加目录（已存在则保留原记录与添加时间，仅同步子目录勾选状态）。</summary>
    public void Add(string path, bool includeSubDir)
    {
        Initialize();
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
insert into dir_history(path, include_sub_dir, added_ticks, last_used_ticks)
values($p, $sub, $now, 0)
on conflict(path) do update set include_sub_dir = excluded.include_sub_dir;";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$sub", includeSubDir ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }

    /// <summary>记录一次切换使用时间。</summary>
    public void Touch(IEnumerable<string> paths)
    {
        Initialize();
        using var conn = GetConnection();
        foreach (var p in paths)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "update dir_history set last_used_ticks = $now where path = $p;";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("$p", p);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>删除历史条目（仅删数据库记录，绝不触碰磁盘目录）。</summary>
    public void Delete(IEnumerable<string> paths)
    {
        Initialize();
        using var conn = GetConnection();
        foreach (var p in paths)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "delete from dir_history where path = $p;";
            cmd.Parameters.AddWithValue("$p", p);
            cmd.ExecuteNonQuery();
        }
    }
}
