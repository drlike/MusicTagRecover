using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MusicTagRecover.Models;

/// <summary>搜索条件的显示、启用状态和关键词顺序。</summary>
public sealed class SearchConditionItem
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int Sequence { get; set; }

    public override string ToString() => Label;
}

/// <summary>搜索条件的默认列表、旧版布尔设置兼容和排序持久化。</summary>
public static class SearchConditionCatalog
{
    public static List<SearchConditionItem> Load(string? json,
        bool useTitle, bool useArtist, bool useAlbum)
    {
        var defaults = CreateDefaults(useTitle, useArtist, useAlbum);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var items = JArray.Parse(json);
                foreach (var item in items.OfType<JObject>())
                {
                    var key = item["Key"]?.ToString() ?? string.Empty;
                    var condition = defaults.FirstOrDefault(s =>
                        string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (condition == null) continue;

                    condition.Enabled = item["Enabled"]?.Value<bool>() ?? condition.Enabled;
                    condition.Sequence = item["Seq"]?.Value<int>() ?? condition.Sequence;
                }
            }
            catch
            {
                return defaults;
            }
        }

        return defaults
            .OrderBy(s => s.Sequence)
            .ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Serialize(IEnumerable<SearchConditionItem> items)
    {
        var list = items.Select((item, index) => new
        {
            item.Key,
            item.Enabled,
            Seq = index
        });
        return JsonConvert.SerializeObject(list);
    }

    public static List<string> GetEnabledKeys(string? json,
        bool useTitle, bool useArtist, bool useAlbum)
    {
        return Load(json, useTitle, useArtist, useAlbum)
            .Where(item => item.Enabled)
            .Select(item => item.Key)
            .ToList();
    }

    private static List<SearchConditionItem> CreateDefaults(
        bool useTitle, bool useArtist, bool useAlbum) => new()
    {
        new SearchConditionItem
        {
            Key = SearchCondition.TitleKey,
            Label = "标题",
            Enabled = useTitle,
            Sequence = 0
        },
        new SearchConditionItem
        {
            Key = SearchCondition.ArtistKey,
            Label = "艺术家",
            Enabled = useArtist,
            Sequence = 1
        },
        new SearchConditionItem
        {
            Key = SearchCondition.AlbumKey,
            Label = "专辑",
            Enabled = useAlbum,
            Sequence = 2
        }
    };
}
