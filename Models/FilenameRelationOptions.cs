namespace MusicTagRecover.Models;

public enum FilenameRelationMode
{
    RenameFiles,
    ChangeTags,
}

public sealed class FilenameRelationOptions
{
    public string Pattern { get; set; } = "@2 - @1";
    public FilenameRelationMode Mode { get; set; }
    public bool RenameRelatedFiles { get; set; }
    public bool UseRegex { get; set; }
    public string RegexPattern { get; set; } = string.Empty;
    public Dictionary<int, int> RegexGroupMap { get; set; } = new();
}

public sealed class FilenameRelationResult
{
    public int ChangedCount { get; set; }
    public int SkippedCount { get; set; }
    public int ErrorCount { get; set; }
    public List<MusicFile> TagChangedFiles { get; } = new();
    public List<string> Errors { get; } = new();
}
