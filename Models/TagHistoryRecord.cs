namespace MusicTagRecover.Models;

/// <summary>
/// 标签历史记录 — 含全部文本字段和封面路径
/// </summary>
public class TagHistoryRecord
{
    public string Serial { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; }

    // 6 个基础字段
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Year { get; set; }
    public string? TrackStr { get; set; }
    public string? DiscStr { get; set; }

    // 扩展字段
    public string? Genre { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Composer { get; set; }
    public string? Lyricist { get; set; }
    public string? Comment { get; set; }
    public string? Lyrics { get; set; }

    /// <summary>封面文件路径（相对 cache\history\ 目录的内容寻址文件名），null 表示无封面</summary>
    public string? CoverPath { get; set; }
}
