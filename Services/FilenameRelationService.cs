using System.Text.RegularExpressions;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;

namespace MusicTagRecover.Services;

public sealed class FilenameRelationService
{
    private static readonly Regex TokenRunRegex = new("(@[0-8])+");
    private static readonly Regex BracketRegex = new(
        "\\([^()]*\\)|\\[[^\\[\\]]*\\]|\\{[^{}]*\\}|<[^<>]*>|（[^（）]*）|【[^【】]*】|《[^《》]*》|“[^“”]”|‘[^‘’]’|『[^『』]』|「[^「」]」");
    private static readonly string[] RelatedImageExtensions = { ".jpg", ".png", ".bmp", ".gif" };

    private readonly ITagService _tagService;
    private readonly ISettingsService _settings;

    public FilenameRelationService(ITagService tagService, ISettingsService settings)
    {
        _tagService = tagService;
        _settings = settings;
    }

    public static bool TryValidatePattern(string pattern, bool changeTags, out string error)
    {
        var value = Regex.Replace(pattern ?? string.Empty, @"[\s/\\]", " ");
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "文件名模式不能为空。";
            return false;
        }

        var seen = new HashSet<string>();
        foreach (Match match in Regex.Matches(value, "@[1-8]"))
        {
            if (!seen.Add(match.Value))
            {
                error = "同一个标签字段不能在模式中重复使用。";
                return false;
            }
        }
        if (seen.Count == 0)
        {
            error = "模式中至少要包含一个 @1 到 @8 占位符。";
            return false;
        }
        if (!changeTags && value.Contains("@0"))
        {
            error = "重命名文件时不能使用 @0。";
            return false;
        }
        if (value.StartsWith("@5@4", StringComparison.Ordinal))
        {
            error = "碟号和音轨连续使用时必须写成 @4@5。";
            return false;
        }

        var check = value;
        if (check.StartsWith("@4@5", StringComparison.Ordinal)) check = check.Substring(4);
        else if (check.StartsWith("@4", StringComparison.Ordinal) || check.StartsWith("@5", StringComparison.Ordinal))
            check = check.Substring(2);
        check = check.Replace("@4@5", "@4");
        if (Regex.IsMatch(check, "@[0-8]@[0-8]"))
        {
            error = "除开头的 @4@5 外，占位符之间必须有分隔文字。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryValidateRegex(string pattern, IDictionary<int, int> groupMap, out string error)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "正则表达式不能为空。";
            return false;
        }
        try
        {
            _ = new Regex(pattern);
        }
        catch (ArgumentException ex)
        {
            error = "正则表达式无效：" + ex.Message;
            return false;
        }
        if (groupMap.Count == 0)
        {
            error = "至少要为一个捕获组选择标签字段。";
            return false;
        }
        var fields = groupMap.Values.Where(v => v > 0).ToList();
        if (fields.Count != fields.Distinct().Count())
        {
            error = "同一个标签字段不能分配给多个捕获组。";
            return false;
        }
        error = string.Empty;
        return true;
    }

    public static string BuildFilename(MusicFile file, string pattern)
    {
        if (pattern.Contains("@1") && string.IsNullOrWhiteSpace(file.Title)) return string.Empty;
        if (pattern.Contains("@2") && string.IsNullOrWhiteSpace(file.Artist)) return string.Empty;

        var value = pattern
            .Replace("@1", (file.Title ?? string.Empty).Trim())
            .Replace("@2", (file.Artist ?? string.Empty).Trim())
            .Replace("@3", (file.Album ?? string.Empty).Trim())
            .Replace("@4", file.Disc.HasValue ? file.Disc.Value.ToString() : string.Empty)
            .Replace("@5", file.Track.HasValue ? file.Track.Value.ToString("D2") : string.Empty)
            .Replace("@6", file.Year.HasValue ? file.Year.Value.ToString() : string.Empty)
            .Replace("@7", (file.Comment ?? string.Empty).Trim())
            .Replace("@8", (file.AlbumArtist ?? string.Empty).Trim());
        value = Regex.Replace(value, @"[\\/]", ";");
        return Regex.Replace(value, "[\\s\":*?<>|]", " ");
    }

    public static bool TryParseFilename(string filePath, FilenameRelationOptions options,
        TagData current, out TagData updated)
    {
        updated = CloneTags(current);
        var filename = NormalizeWhitespace(Path.GetFileNameWithoutExtension(filePath));
        var changed = options.UseRegex
            ? ApplyRegex(filename, options.RegexPattern, options.RegexGroupMap, updated)
            : ApplyPattern(filename, options.Pattern, updated);
        return changed;
    }

    public async Task<FilenameRelationResult> ExecuteAsync(IReadOnlyList<MusicFile> files,
        FilenameRelationOptions options, bool overwriteReadOnly,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new FilenameRelationResult();
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            try
            {
                if (options.Mode == FilenameRelationMode.ChangeTags)
                    await ChangeTagsAsync(file, options, overwriteReadOnly, result).ConfigureAwait(false);
                else
                    await RenameFileAsync(file, options, result).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.ErrorCount++;
                result.Errors.Add(Path.GetFileName(file.FilePath) + ": " + ex.Message);
            }
            progress?.Report(i + 1);
        }
        return result;
    }

    private async Task ChangeTagsAsync(MusicFile file, FilenameRelationOptions options,
        bool overwriteReadOnly, FilenameRelationResult result)
    {
        var attributes = File.GetAttributes(file.FilePath);
        var isReadOnly = (attributes & FileAttributes.ReadOnly) != 0;
        if (isReadOnly && !overwriteReadOnly)
        {
            result.SkippedCount++;
            return;
        }

        var tags = await _tagService.ReadTagsAsync(file.FilePath).ConfigureAwait(false);
        if (!TryParseFilename(file.FilePath, options, tags, out var updated))
        {
            result.SkippedCount++;
            return;
        }

        if (isReadOnly) File.SetAttributes(file.FilePath, attributes & ~FileAttributes.ReadOnly);
        try
        {
            if (await _tagService.WriteTagsAsync(file.FilePath, updated,
                    _settings.SaveTagsKeepUpdateTime).ConfigureAwait(false))
            {
                result.ChangedCount++;
                result.TagChangedFiles.Add(file);
            }
            else
            {
                result.ErrorCount++;
                result.Errors.Add(Path.GetFileName(file.FilePath) + ": 写入标签失败");
            }
        }
        finally
        {
            if (isReadOnly && File.Exists(file.FilePath)) File.SetAttributes(file.FilePath, attributes);
        }
    }

    private async Task RenameFileAsync(MusicFile file, FilenameRelationOptions options,
        FilenameRelationResult result)
    {
        var tags = await _tagService.ReadTagsAsync(file.FilePath).ConfigureAwait(false);
        var source = ToMusicFile(file.FilePath, tags);
        var basename = BuildFilename(source, options.Pattern);
        if (string.IsNullOrWhiteSpace(basename))
        {
            result.ErrorCount++;
            result.Errors.Add(Path.GetFileName(file.FilePath) + ": 标题或艺术家标签为空");
            return;
        }

        var oldPath = file.FilePath;
        var directory = Path.GetDirectoryName(oldPath) ?? string.Empty;
        var extension = Path.GetExtension(oldPath);
        var newPath = Path.Combine(directory, basename + extension);
        if (string.Equals(Path.GetFileName(newPath), Path.GetFileName(oldPath), StringComparison.Ordinal))
        {
            result.SkippedCount++;
            return;
        }
        if (File.Exists(newPath))
        {
            var suffix = 1;
            do
            {
                newPath = Path.Combine(directory, basename + " (" + suffix + ")" + extension);
                suffix++;
            } while (File.Exists(newPath));
        }

        var related = options.RenameRelatedFiles ? FindRelatedFiles(oldPath, newPath) : new List<KeyValuePair<string, string>>();
        File.Move(oldPath, newPath);
        foreach (var pair in related)
        {
            if (!File.Exists(pair.Value)) File.Move(pair.Key, pair.Value);
        }
        file.FilePath = newPath;
        result.ChangedCount++;
    }

    private static List<KeyValuePair<string, string>> FindRelatedFiles(string oldPath, string newPath)
    {
        var result = new List<KeyValuePair<string, string>>();
        AddRelated(result, Path.ChangeExtension(oldPath, ".lrc"), Path.ChangeExtension(newPath, ".lrc"));
        foreach (var extension in RelatedImageExtensions)
        {
            var source = Path.ChangeExtension(oldPath, extension);
            if (File.Exists(source))
            {
                AddRelated(result, source, Path.ChangeExtension(newPath, extension));
                break;
            }
        }
        return result;
    }

    private static void AddRelated(List<KeyValuePair<string, string>> files, string source, string destination)
    {
        if (File.Exists(source) && !File.Exists(destination))
            files.Add(new KeyValuePair<string, string>(source, destination));
    }

    private static bool ApplyRegex(string filename, string pattern, IDictionary<int, int> map, TagData tags)
    {
        var match = Regex.Match(filename, NormalizeWhitespace(pattern));
        if (!match.Success) return false;
        var changed = false;
        foreach (var pair in map)
        {
            if (pair.Key <= 0 || pair.Key >= match.Groups.Count || !match.Groups[pair.Key].Success) continue;
            changed |= ApplyMappedField(tags, pair.Value, match.Groups[pair.Key].Value.Trim());
        }
        return changed;
    }

    private static bool ApplyPattern(string filename, string pattern, TagData tags)
    {
        var normalized = NormalizeWhitespace(pattern);
        var tokenMatches = TokenRunRegex.Matches(normalized).Cast<Match>().ToList();
        if (tokenMatches.Count == 0) return false;

        var regex = new System.Text.StringBuilder();
        var position = 0;
        foreach (var token in tokenMatches)
        {
            regex.Append(Regex.Escape(normalized.Substring(position, token.Index - position)));
            regex.Append("(.*)");
            position = token.Index + token.Length;
        }
        regex.Append(Regex.Escape(normalized.Substring(position)));

        var values = MatchWithBracketProtection(filename, regex.ToString());
        if (values == null) return false;
        var changed = false;
        for (var i = 0; i < values.Count && i < tokenMatches.Count; i++)
            changed |= ApplyPatternToken(tags, tokenMatches[i].Value, values[i].Trim());
        return changed;
    }

    private static bool ApplyPatternToken(TagData tags, string token, string value)
    {
        if (token == "@4@5")
        {
            int combined;
            if (!int.TryParse(value, out combined)) return false;
            var changed = SetNumber(tags.Disc, combined / 100, out var disc);
            tags.Disc = disc;
            var trackChanged = SetNumber(tags.Track, combined % 100, out var track);
            tags.Track = track;
            return trackChanged || changed;
        }
        if (token.Length > 2)
        {
            var match = Regex.Match(token, "^(@4@5|@4|@5)(@[0-8])$");
            var parts = Regex.Match(value, "^(\\d*)(.*)$");
            if (match.Success && parts.Success)
            {
                var changed = ApplyPatternToken(tags, match.Groups[1].Value, parts.Groups[1].Value);
                return ApplyPatternToken(tags, match.Groups[2].Value, parts.Groups[2].Value.Trim()) || changed;
            }
        }
        int field;
        if (!int.TryParse(token.Substring(1), out field) || field == 0) return false;
        return ApplyMappedField(tags, field, value);
    }

    private static bool ApplyMappedField(TagData tags, int field, string value)
    {
        bool changed;
        switch (field)
        {
            case 1:
                changed = SetText(tags.Title, value, out var title); tags.Title = title; return changed;
            case 2:
                changed = SetText(tags.Artist, value, out var artist); tags.Artist = artist; return changed;
            case 3:
                changed = SetText(tags.Album, value, out var album); tags.Album = album; return changed;
            case 4:
                changed = SetParsedNumber(tags.Disc, value, out var disc); tags.Disc = disc; return changed;
            case 5:
                changed = SetParsedNumber(tags.Track, value, out var track); tags.Track = track; return changed;
            case 6:
                changed = SetParsedNumber(tags.Year, value, out var year); tags.Year = year; return changed;
            case 7:
                changed = SetText(tags.Comment, value, out var comment); tags.Comment = comment; return changed;
            case 8:
                changed = SetText(tags.AlbumArtist, value, out var albumArtist); tags.AlbumArtist = albumArtist; return changed;
            default: return false;
        }
    }

    private static bool SetText(string? current, string value, out string? updated)
    {
        updated = current;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(current, value, StringComparison.Ordinal)) return false;
        updated = value;
        return true;
    }

    private static bool SetParsedNumber(uint? current, string value, out uint? updated)
    {
        updated = current;
        int number;
        if (!int.TryParse(value, out number) || number < 0) return false;
        return SetNumber(current, number, out updated);
    }

    private static bool SetNumber(uint? current, int value, out uint? updated)
    {
        updated = value > 0 ? (uint?)value : null;
        if (current == updated) return false;
        return true;
    }

    private static List<string>? MatchWithBracketProtection(string filename, string pattern)
    {
        var states = new List<ProtectionState>();
        var current = filename;
        var layer = 0;
        while (BracketRegex.IsMatch(current))
        {
            var state = new ProtectionState { Text = current, Layer = layer };
            var index = 0;
            current = BracketRegex.Replace(current, match =>
            {
                state.Values.Add(match.Value);
                return "\t" + layer.ToString("D5") + index++;
            });
            states.Add(state);
            layer++;
        }
        if (states.Count == 0) states.Add(new ProtectionState { Text = filename, Layer = 0 });
        states.Reverse();

        for (var stateIndex = 0; stateIndex < states.Count; stateIndex++)
        {
            var match = Regex.Match(states[stateIndex].Text, pattern);
            if (!match.Success) continue;
            var result = new List<string>();
            for (var group = 1; group < match.Groups.Count; group++)
            {
                var value = match.Groups[group].Value;
                for (var restore = stateIndex + 1; restore < states.Count; restore++)
                {
                    var state = states[restore];
                    for (var i = 0; i < state.Values.Count; i++)
                        value = value.Replace("\t" + state.Layer.ToString("D5") + i, state.Values[i]);
                }
                result.Add(value);
            }
            return result;
        }
        return null;
    }

    private static string NormalizeWhitespace(string value) => Regex.Replace(value ?? string.Empty, "\\s", " ");

    private static TagData CloneTags(TagData tags) => new()
    {
        Title = tags.Title, Artist = tags.Artist, Album = tags.Album, Year = tags.Year,
        Track = tags.Track, TrackCount = tags.TrackCount, Genre = tags.Genre, Comment = tags.Comment,
        AlbumArtist = tags.AlbumArtist, Composer = tags.Composer, Lyricist = tags.Lyricist,
        Disc = tags.Disc, DiscCount = tags.DiscCount, CoverArtData = tags.CoverArtData,
        CoverArtMimeType = tags.CoverArtMimeType, CoverArtType = tags.CoverArtType,
        AllPictures = tags.AllPictures, Lyrics = tags.Lyrics, TagFormat = tags.TagFormat,
    };

    private static MusicFile ToMusicFile(string path, TagData tags) => new()
    {
        FilePath = path,
        Title = tags.Title ?? string.Empty,
        Artist = tags.Artist ?? string.Empty,
        Album = tags.Album ?? string.Empty,
        Disc = tags.Disc,
        Track = tags.Track,
        Year = tags.Year,
        Comment = tags.Comment ?? string.Empty,
        AlbumArtist = tags.AlbumArtist ?? string.Empty,
    };

    private sealed class ProtectionState
    {
        public string Text { get; set; } = string.Empty;
        public int Layer { get; set; }
        public List<string> Values { get; } = new();
    }
}
