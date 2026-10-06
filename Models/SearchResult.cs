using MusicTagRecover.Interfaces;

namespace MusicTagRecover.Models;

/// <summary>
/// 网络搜索结果，包含标签/歌词/封面的在线匹配信息
/// </summary>
public class SearchResult
{
    public string SourceName { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Year { get; set; }
    public string? Genre { get; set; }
    public string? CoverUrl { get; set; }
    /// <summary>封面已存入 cache\history\ 的相对文件名（内容寻址去重，与标签历史共用同一目录）</summary>
    public string? CoverTempPath { get; set; }
    public string? LyricSnippet { get; set; }
    public double MatchScore { get; set; }
    public Dictionary<string, string> ExtraFields { get; set; } = new();

    /// <summary>用于分页合并时识别同一结果，不包含运行时缓存路径。</summary>
    public string GetIdentityKey()
    {
        var extras = string.Join("|", ExtraFields
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value));
        return string.Join("|", SourceName, SourceUrl, Title, Artist, Album, Year,
            CoverUrl, LyricSnippet, extras);
    }

    public override string ToString() => $"[{SourceName}] {Artist} - {Title} ({MatchScore:P0})";
}

/// <summary>
/// 搜索条件配置
/// </summary>
public class SearchCondition
{
    public const string TitleKey = "title";
    public const string ArtistKey = "artist";
    public const string AlbumKey = "album";

    public bool UseTitle { get; set; } = true;
    public bool UseArtist { get; set; } = true;
    public bool UseAlbum { get; set; }
    public List<string> FieldOrder { get; set; } = new()
    {
        TitleKey, ArtistKey, AlbumKey
    };
    // Compatibility with the previous single "only filename" option.
    public bool UseOnlyFilename { get; set; }
    public string? CustomQuery { get; set; }

    /// <summary>
    /// iTunes 搜索地区参数
    /// </summary>
    public string ItunesCountry { get; set; } = "US";
    public int WebSearchItemsLimit { get; set; } = 10;
    public int WebSearchItemsOffset { get; set; }
    public int WebSearchThreadCount { get; set; } = 4;

    /// <summary>根据应用设置创建搜索条件，包含启用状态和关键词顺序。</summary>
    public static SearchCondition FromSettings(ISettingsService settings)
    {
        var legacyOnlyFilename = settings.SearchConditionUseOnlyFilename;
        var condition = new SearchCondition
        {
            UseTitle = settings.SearchConditionUseTitle && !legacyOnlyFilename,
            UseArtist = settings.SearchConditionUseArtist && !legacyOnlyFilename,
            UseAlbum = settings.SearchConditionUseAlbum && !legacyOnlyFilename,
            UseOnlyFilename = legacyOnlyFilename
        };
        condition.FieldOrder = SearchConditionCatalog.GetEnabledKeys(
            settings.SearchConditionItemList,
            condition.UseTitle,
            condition.UseArtist,
            condition.UseAlbum);
        return condition;
    }

    /// <summary>
    /// 根据音乐文件生成搜索关键词
    /// </summary>
    public string BuildSearchQuery(MusicFile file)
    {
        if (!string.IsNullOrEmpty(CustomQuery))
            return CustomQuery;

        // Keep the legacy option's artist + filename ordering for old settings.
        if (UseOnlyFilename)
        {
            var legacyParts = new List<string>();
            if (UseArtist && !string.IsNullOrEmpty(file.Artist))
                legacyParts.Add(file.Artist);

            var filename = Path.GetFileNameWithoutExtension(file.FilePath);
            if (!string.IsNullOrWhiteSpace(filename))
                legacyParts.Add(filename);
            if (UseAlbum && !string.IsNullOrEmpty(file.Album))
                legacyParts.Add(file.Album);
            return string.Join(" ", legacyParts);
        }

        var parts = new List<string>();
        foreach (var field in FieldOrder)
        {
            if (field == TitleKey && UseTitle && !string.IsNullOrWhiteSpace(file.Title))
                parts.Add(file.Title);
            else if (field == ArtistKey && UseArtist && !string.IsNullOrEmpty(file.Artist))
                parts.Add(file.Artist);
            else if (field == AlbumKey && UseAlbum && !string.IsNullOrEmpty(file.Album))
                parts.Add(file.Album);
        }

        if (parts.Count == 0)
        {
            var filename = Path.GetFileNameWithoutExtension(file.FilePath);
            if (!string.IsNullOrWhiteSpace(filename))
                parts.Add(filename);
        }

        return string.Join(" ", parts);
    }
}
