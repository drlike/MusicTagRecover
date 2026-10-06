using System.Data.SQLite;
using Newtonsoft.Json;
using MusicTagRecover.Interfaces;

namespace MusicTagRecover.Services;

/// <summary>
/// 设置服务 — 使用 SQLite 持久化应用配置
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly string _dbPath;
    private readonly Dictionary<string, object?> _cache = new();
    private bool _loaded;

    // === 默认值 ===
    private static readonly Dictionary<string, object?> Defaults = new()
    {
        ["MainFormPosSizeInfo"] = null,
        ["Maximized"] = false,
        ["LastVersionCode"] = null,
        ["IgnoreCheckSpecAppVersion"] = null,
        ["IncludeSubDir"] = true,
        ["RestrictFileExts"] = ".mp3;.flac;.m4a;.ogg;.wma;.wav;.ape",
        ["FileFilterByDuration"] = false,
        ["FileFilterIgnoreVideoFile"] = true,
        ["FilenameCustomPattern"] = null,
        ["FilenameRelCondition"] = null,
        ["FilenameRelRegexCondition"] = null,
        ["FilenameRelSelectedTab"] = 0,
        ["ListViewFileSetting"] = null,
        ["ListviewColumnHeader"] = null,
        ["SortSetting"] = null,
        ["FilterListViewKeyword"] = null,
        ["FilterListViewType"] = null,
        ["SearchConditionUseTitle"] = true,
        ["SearchConditionUseAlbum"] = false,
        ["SearchConditionUseArtist"] = true,
        ["SearchConditionUseOnlyFilename"] = false,
        ["SearchConditionItemList"] = null,
        ["WebSearchItemsLimit"] = 10,
        ["AutoMatchTagsWebSearchThreadCount"] = 4,
        ["ItunesSearchParamsCountry"] = "US",
        ["Id3v2Version"] = "v2.3",
        ["SaveTagsKeepUpdateTime"] = false,
        ["CommentTagWrite163Key"] = null,
        ["AutoMatchTagsCondition"] = null,
        ["CombTagsSearchOverwriteOptions"] = null,
        ["ConnectorsArtists"] = "; ",
        ["ConnectorsLyricAndTLyric"] = " / ",
        ["LyricDownload_DeleteHeadTag"] = true,
        ["LyricDownload_DeleteLinesOfBlankText"] = true,
        ["LyricDownload_ReformatTimetag"] = true,
        ["LyricDownload_RemoveTimetag"] = false,
        ["DontDownloadLyricWithInstrumentInTitle"] = true,
        ["LyricDownload_DownloadTrans_Enable"] = true,
        ["LyricDownload_DownloadTrans_DontDownloadOrigLyric"] = false,
        ["LyricDownload_DownloadTrans_LyricFormat"] = "{artist} - {title}.lrc",
        ["LyricDownload_DownloadTrans_ChineseConvMode"] = "none",
        ["SaveLrcDirectory"] = null,
        ["SaveLrcFileDefaultEncoding"] = "utf-8",
        ["SaveLrcFilenameFormat"] = "{artist} - {title}.lrc",
        ["PictureFormatLimits"] = "jpg,jpeg,png,bmp,gif",
        ["PictureResolutionLimits"] = null,
        ["PictureSizeLimitsKB"] = null,
        ["OverwritePictureboxPicture"] = true,
        ["LyricInfo_SourceItemList"] = null,
        ["PictureInfo_SourceItemList"] = null,
        ["CombTagsInfo_SourceItemList"] = null,
        ["ProxyUrl"] = "http://127.0.0.1:7890",
        ["ProxySourceSettings"] = null,
        ["LogEnabled"] = true,
        ["LogLevel"] = "Info",
        ["LogFilePath"] = null,
        ["UrlCacheMaxSizeMb"] = 256
    };

    public SettingsService(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "MusicTagRecover.db");
    }

    public void Load()
    {
        if (_loaded) return;
        EnsureDatabase();

        using var conn = new SQLiteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM Settings";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var value = reader.IsDBNull(1) ? null : reader.GetString(1);
            _cache[key] = value;
        }
        _loaded = true;
    }

    public void Save()
    {
        EnsureDatabase();
        using var conn = new SQLiteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var tx = conn.BeginTransaction();

        foreach (var kvp in _cache)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@key, @value)";
            cmd.Parameters.AddWithValue("@key", kvp.Key);
            cmd.Parameters.AddWithValue("@value", (object?)kvp.Value?.ToString() ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void ResetToDefaults()
    {
        _cache.Clear();
        foreach (var kvp in Defaults)
        {
            _cache[kvp.Key] = kvp.Value;
        }
        Save();
    }

    #region Property Accessors

    public string? MainFormPosSizeInfo { get => Get<string?>(); set => Set(value); }
    public bool Maximized { get => Get<bool>(); set => Set(value); }
    public string? LastVersionCode { get => Get<string?>(); set => Set(value); }
    public string? IgnoreCheckSpecAppVersion { get => Get<string?>(); set => Set(value); }
    public bool IncludeSubDir { get => Get<bool>(); set => Set(value); }
    public string? RestrictFileExts { get => Get<string?>(); set => Set(value); }
    public bool FileFilterByDuration { get => Get<bool>(); set => Set(value); }
    public bool FileFilterIgnoreVideoFile { get => Get<bool>(); set => Set(value); }
    public string? FilenameCustomPattern { get => Get<string?>(); set => Set(value); }
    public string? FilenameRelCondition { get => Get<string?>(); set => Set(value); }
    public string? FilenameRelRegexCondition { get => Get<string?>(); set => Set(value); }
    public int FilenameRelSelectedTab { get => Get<int>(); set => Set(value); }
    public string? ListViewFileSetting { get => Get<string?>(); set => Set(value); }
    public string? ListviewColumnHeader { get => Get<string?>(); set => Set(value); }
    public string? SortSetting { get => Get<string?>(); set => Set(value); }
    public string? FilterListViewKeyword { get => Get<string?>(); set => Set(value); }
    public string? FilterListViewType { get => Get<string?>(); set => Set(value); }
    public bool SearchConditionUseTitle { get => Get<bool>(); set => Set(value); }
    public bool SearchConditionUseAlbum { get => Get<bool>(); set => Set(value); }
    public bool SearchConditionUseArtist { get => Get<bool>(); set => Set(value); }
    public bool SearchConditionUseOnlyFilename { get => Get<bool>(); set => Set(value); }
    public string? SearchConditionItemList { get => Get<string?>(); set => Set(value); }
    public int WebSearchItemsLimit { get => Get<int>(); set => Set(value); }
    public int AutoMatchTagsWebSearchThreadCount { get => Get<int>(); set => Set(value); }
    public string ItunesSearchParamsCountry { get => Get<string>()!; set => Set(value); }
    public string Id3v2Version { get => Get<string>()!; set => Set(value); }
    public bool SaveTagsKeepUpdateTime { get => Get<bool>(); set => Set(value); }
    public string? CommentTagWrite163Key { get => Get<string?>(); set => Set(value); }
    public string? AutoMatchTagsCondition { get => Get<string?>(); set => Set(value); }
    public string? CombTagsSearchOverwriteOptions { get => Get<string?>(); set => Set(value); }
    public string? ConnectorsArtists { get => Get<string?>(); set => Set(value); }
    public string? ConnectorsLyricAndTLyric { get => Get<string?>(); set => Set(value); }
    public bool LyricDownload_DeleteHeadTag { get => Get<bool>(); set => Set(value); }
    public bool LyricDownload_DeleteLinesOfBlankText { get => Get<bool>(); set => Set(value); }
    public bool LyricDownload_ReformatTimetag { get => Get<bool>(); set => Set(value); }
    public bool LyricDownload_RemoveTimetag { get => Get<bool>(); set => Set(value); }
    public bool DontDownloadLyricWithInstrumentInTitle { get => Get<bool>(); set => Set(value); }
    public bool LyricDownload_DownloadTrans_Enable { get => Get<bool>(); set => Set(value); }
    public bool LyricDownload_DownloadTrans_DontDownloadOrigLyric { get => Get<bool>(); set => Set(value); }
    public string? LyricDownload_DownloadTrans_LyricFormat { get => Get<string?>(); set => Set(value); }
    public string? LyricDownload_DownloadTrans_ChineseConvMode { get => Get<string?>(); set => Set(value); }
    public string? SaveLrcDirectory { get => Get<string?>(); set => Set(value); }
    public string? SaveLrcFileDefaultEncoding { get => Get<string?>(); set => Set(value); }
    public string? SaveLrcFilenameFormat { get => Get<string?>(); set => Set(value); }
    public string? PictureFormatLimits { get => Get<string?>(); set => Set(value); }
    public int? PictureResolutionLimits { get => Get<int?>(); set => Set(value); }
    public int? PictureSizeLimitsKB { get => Get<int?>(); set => Set(value); }
    public bool OverwritePictureboxPicture { get => Get<bool>(); set => Set(value); }
    public string? LyricInfo_SourceItemList { get => Get<string?>(); set => Set(value); }
    public string? PictureInfo_SourceItemList { get => Get<string?>(); set => Set(value); }
    public string? CombTagsInfo_SourceItemList { get => Get<string?>(); set => Set(value); }
    public string ProxyUrl { get => Get<string>()!; set => Set(value); }
    public string? ProxySourceSettings { get => Get<string?>(); set => Set(value); }
    public bool LogEnabled { get => Get<bool>(); set => Set(value); }
    public string LogLevel { get => Get<string>()!; set => Set(value); }
    public string? LogFilePath { get => Get<string?>(); set => Set(value); }
    public int UrlCacheMaxSizeMb { get => Get<int>(); set => Set(value); }

    #endregion

    #region Internal Helpers

    private T Get<T>([System.Runtime.CompilerServices.CallerMemberName] string? key = null)
    {
        Load();
        var actualKey = key ?? throw new ArgumentNullException(nameof(key));
        if (_cache.TryGetValue(actualKey, out var value) && value != null)
        {
            var t = typeof(T);
            if (t == typeof(bool) && value is string s)
                return (T)(object)(s == "True");
            if (t == typeof(int) && value is string si)
                return (T)(object)int.Parse(si);
            if (t == typeof(int?))
            {
                var inner = Nullable.GetUnderlyingType(t)!;
                var raw = value is string sv ? int.Parse(sv) : Convert.ChangeType(value, inner);
                return (T)raw!;
            }
            return (T)Convert.ChangeType(value, typeof(T))!;
        }

        if (Defaults.TryGetValue(actualKey, out var def))
            return (T)def!;

        return default!;
    }

    private void Set<T>(T value, [System.Runtime.CompilerServices.CallerMemberName] string? key = null)
    {
        var actualKey = key ?? throw new ArgumentNullException(nameof(key));
        _cache[actualKey] = value;
    }

    private void EnsureDatabase()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (dir != null) Directory.CreateDirectory(dir);

        using var conn = new SQLiteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT
            )
            """;
        cmd.ExecuteNonQuery();
    }

    #endregion
}
