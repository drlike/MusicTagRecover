using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MusicTagRecover.Models;

public enum TagSourceCategory
{
    Picture,
    Lyrics,
    CombinationTags
}

public sealed class TagSourceItem
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int Sequence { get; set; }
    public int WebSearchItemsLimit { get; set; } = 100;

    public override string ToString() => Label;
}

/// <summary>标签源的默认列表、旧版 JSON 兼容和排序持久化。</summary>
public static class TagSourceCatalog
{
    public static List<TagSourceItem> Load(string? json, TagSourceCategory category,
        int defaultLimit = 10)
    {
        var defaults = CreateDefaults(category, defaultLimit);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var items = JArray.Parse(json);
                foreach (var item in items.OfType<JObject>())
                {
                    var source = defaults.FirstOrDefault(s =>
                        string.Equals(s.Key, MapSource(item["Src"]), StringComparison.OrdinalIgnoreCase));
                    if (source == null) continue;

                    source.Enabled = item["Enabled"]?.Value<bool>() ?? source.Enabled;
                    source.Sequence = item["Seq"]?.Value<int>() ?? source.Sequence;
                    source.WebSearchItemsLimit = item["WebSearchItemsLimit"]?.Value<int>() ?? source.WebSearchItemsLimit;
                }
            }
            catch
            {
                // 损坏或不兼容的配置使用默认列表。
                return defaults;
            }
        }

        return defaults
            .OrderBy(s => s.Sequence)
            .ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Serialize(IEnumerable<TagSourceItem> items)
    {
        var list = items.Select((item, index) => new
        {
            Src = SerializeSource(item.Key),
            item.Enabled,
            Seq = index,
            item.WebSearchItemsLimit
        });
        return JsonConvert.SerializeObject(list);
    }

    public static string Describe(IEnumerable<TagSourceItem> items)
    {
        return string.Join(" | ", items.Select((item, index) =>
            string.Format("{0}:{1}({2}) enabled={3} limit={4}", index + 1,
                item.Key, item.Label, item.Enabled, item.WebSearchItemsLimit)));
    }

    private static object SerializeSource(string key) => key.ToLowerInvariant() switch
    {
        "netease" => 0,
        "qq" => 1,
        "kugou" => 3,
        "itunes" => 5,
        "lastfm" => 6,
        "musicbrainz" => 7,
        "kuwo" => 9,
        _ => key
    };

    public static string MapSource(JToken? token)
    {
        var value = token?.ToString() ?? string.Empty;
        if (int.TryParse(value, out var number))
        {
            return number switch
            {
                0 => "netease",
                1 => "qq",
                3 => "kugou",
                5 => "itunes",
                6 => "lastfm",
                7 => "musicbrainz",
                9 => "kuwo",
                _ => string.Empty
            };
        }

        return value.ToLowerInvariant() switch
        {
            "music163" or "163" or "netease" => "netease",
            "qq" => "qq",
            "kugou" => "kugou",
            "itunes" => "itunes",
            "lastfm" or "last.fm" => "lastfm",
            "brainz" or "musicbrainz" => "musicbrainz",
            "kuwo" => "kuwo",
            "discogs" => "discogs",
            _ => string.Empty
        };
    }

    private static List<TagSourceItem> CreateDefaults(TagSourceCategory category, int limit)
    {
        var enabled = category == TagSourceCategory.Lyrics
            ? new HashSet<string>(new[] { "netease", "qq", "kugou" }, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(new[] { "netease", "qq" }, StringComparer.OrdinalIgnoreCase);

        var keys = category == TagSourceCategory.Lyrics
            ? new[] { "netease", "qq", "kugou", "kuwo" }
            : new[] { "netease", "qq", "itunes", "kuwo", "lastfm", "musicbrainz", "discogs" };

        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["netease"] = "网易云音乐",
            ["qq"] = "QQ音乐",
            ["kugou"] = "酷狗音乐",
            ["kuwo"] = "酷我音乐",
            ["itunes"] = "iTunes",
            ["lastfm"] = "Last.fm",
            ["musicbrainz"] = "MusicBrainz",
            ["discogs"] = "Discogs"
        };

        return keys.Select((key, index) => new TagSourceItem
        {
            Key = key,
            Label = labels[key],
            Enabled = enabled.Contains(key),
            Sequence = index,
            WebSearchItemsLimit = Math.Max(1, limit)
        }).ToList();
    }
}
