using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MusicTagRecover.Interfaces;

namespace MusicTagRecover.Models;

public enum AutoMatchWriteMode
{
    SaveToTag,
    SaveToFile,
    SaveToTagAndFile
}

public sealed class AutoMatchFieldOption
{
    public bool Enabled { get; set; }
    public AutoMatchWriteMode WriteMode { get; set; } = AutoMatchWriteMode.SaveToTag;
    public bool Overwrite { get; set; }
}

/// <summary>批量自动匹配标签的持久化选项。</summary>
public sealed class AutoMatchOptions
{
    public const int MaxThreadCount = 16;

    public const string Cover = "cover";
    public const string Lyrics = "lyrics";
    public const string Title = "title";
    public const string Artist = "artist";
    public const string Album = "album";
    public const string Year = "year";
    public const string Track = "trackstr";
    public const string Disc = "discstr";
    public const string Genre = "genre";
    public const string Comment = "comment";

    private static readonly string[] OrderedNames =
        { Cover, Lyrics, Title, Artist, Album, Year, Track, Disc, Genre, Comment };

    public Dictionary<string, AutoMatchFieldOption> Fields { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public int ThreadCount { get; set; } = 4;
    public bool DontDownloadLyricWithInstrumentInTitle { get; set; } = true;

    public AutoMatchOptions()
    {
        foreach (var name in OrderedNames)
            Fields[name] = new AutoMatchFieldOption();
        Fields[Cover].Enabled = true;
        Fields[Lyrics].Enabled = true;
        Fields[Cover].WriteMode = AutoMatchWriteMode.SaveToTag;
        Fields[Lyrics].WriteMode = AutoMatchWriteMode.SaveToTag;
    }

    public AutoMatchFieldOption Get(string name) =>
        Fields.TryGetValue(name, out var option) ? option : new AutoMatchFieldOption();

    public bool HasAnyEnabled => Fields.Values.Any(f => f.Enabled);
    public bool HasTagFields => Fields.Values.Any(p => p.Enabled &&
        p.WriteMode != AutoMatchWriteMode.SaveToFile);

    public static AutoMatchOptions Load(ISettingsService settings)
    {
        var options = new AutoMatchOptions
        {
            ThreadCount = Math.Max(1, Math.Min(MaxThreadCount, settings.AutoMatchTagsWebSearchThreadCount)),
            DontDownloadLyricWithInstrumentInTitle = settings.DontDownloadLyricWithInstrumentInTitle
        };
        if (!string.IsNullOrWhiteSpace(settings.AutoMatchTagsCondition))
        {
            try
            {
                var obj = JObject.Parse(settings.AutoMatchTagsCondition);
                foreach (var option in options.Fields.Values) option.Enabled = false;
                foreach (var name in OrderedNames)
                {
                    var token = obj[name];
                    if (token == null) continue;
                    var option = options.Fields[name];
                    if (token.Type == JTokenType.Array)
                    {
                        option.WriteMode = ParseMode(token[0]?.ToString());
                        option.Overwrite = token[1]?.Value<bool>() ?? false;
                        option.Enabled = true;
                    }
                    else if (token.Type == JTokenType.Object)
                    {
                        option.Enabled = token["Enabled"]?.Value<bool>() ?? true;
                        option.Overwrite = token["Overwrite"]?.Value<bool>() ?? false;
                        option.WriteMode = ParseMode(token["WriteMode"]?.ToString() ?? token["Mode"]?.ToString());
                        if (token["Item1"] != null) option.WriteMode = ParseMode(token["Item1"]?.ToString());
                        if (token["Item2"] != null) option.Overwrite = token["Item2"]?.Value<bool>() ?? false;
                    }
                }
            }
            catch { /* 损坏配置按默认值启动 */ }
        }
        return options;
    }

    public void Save(ISettingsService settings)
    {
        var obj = new JObject();
        foreach (var name in OrderedNames)
        {
            var option = Get(name);
            if (option.Enabled)
                obj[name] = new JObject { ["Item1"] = ModeName(option.WriteMode), ["Item2"] = option.Overwrite };
        }
        settings.AutoMatchTagsCondition = obj.ToString(Formatting.None);
        settings.AutoMatchTagsWebSearchThreadCount = Math.Max(1, Math.Min(MaxThreadCount, ThreadCount));
        settings.DontDownloadLyricWithInstrumentInTitle = DontDownloadLyricWithInstrumentInTitle;
        settings.Save();
    }

    public static IReadOnlyList<string> Names => OrderedNames;

    public static bool IsInstrumentalTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        var value = title.ToLowerInvariant();
        return value.Contains("instrumental") || value.Contains("off vocal") ||
               value.Contains("伴奏") || value.Contains("纯音乐");
    }

    private static AutoMatchWriteMode ParseMode(string? value)
    {
        return value switch
        {
            "SaveToFile" => AutoMatchWriteMode.SaveToFile,
            "SaveToTagAndFile" => AutoMatchWriteMode.SaveToTagAndFile,
            _ => AutoMatchWriteMode.SaveToTag
        };
    }

    private static string ModeName(AutoMatchWriteMode mode) => mode switch
    {
        AutoMatchWriteMode.SaveToFile => "SaveToFile",
        AutoMatchWriteMode.SaveToTagAndFile => "SaveToTagAndFile",
        _ => "SaveToTag"
    };
}
