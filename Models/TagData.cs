namespace MusicTagRecover.Models;

/// <summary>
/// 标签数据对象，用于批量标签操作的数据传输
/// </summary>
public class TagData
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public uint? Year { get; set; }
    public uint? Track { get; set; }
    public uint? TrackCount { get; set; }
    public string? Genre { get; set; }
    public string? Comment { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Composer { get; set; }
    public string? Lyricist { get; set; }
    public uint? Disc { get; set; }
    public uint? DiscCount { get; set; }
    public byte[]? CoverArtData { get; set; }
    public string? CoverArtMimeType { get; set; }
    public string? CoverArtType { get; set; }
    public List<CoverArt>? AllPictures { get; set; }
    public string? Lyrics { get; set; }
    public string? TagFormat { get; set; }

    // ===== 轻量扫描（不加载封面字节/歌词正文）补充信息 =====

    /// <summary>内嵌封面数量（轻量读取时使用，全量读取时由 AllPictures.Count 推导）。</summary>
    public int PictureCount { get; set; }

    /// <summary>是否存在歌词正文（轻量读取时不填充 Lyrics，只给标志）。</summary>
    public bool LyricsPresent { get; set; }

    /// <summary>true 表示本对象为轻量扫描结果（无封面字节、无歌词正文）。</summary>
    public bool IsSummary { get; set; }

    // 音频属性（来自 file.Properties）
    public long? DurationMs { get; set; }
    public int? BitRate { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public int? BitsPerSample { get; set; }

    /// <summary>
    /// 从 MusicFile 提取标签数据
    /// </summary>
    public static TagData FromMusicFile(MusicFile file) => new()
    {
        Title = file.Title,
        Artist = file.Artist,
        Album = file.Album,
        Year = file.Year,
        Track = file.Track,
        TrackCount = file.TrackCount,
        Genre = file.Genre,
        Comment = file.Comment,
        AlbumArtist = file.AlbumArtist,
        Composer = file.Composer,
        Lyricist = file.Lyricist,
        Disc = file.Disc,
        DiscCount = file.DiscCount,
        Lyrics = string.IsNullOrEmpty(file.Lyrics) ? null : file.Lyrics,
        AllPictures = file.AllPictures?.Select(p => new CoverArt
        {
            ImageData = p.ImageData,
            MimeType = p.MimeType,
            PictureType = p.PictureType,
        }).ToList(),
    };

    /// <summary>
    /// 检查是否有任何非空字段
    /// </summary>
    public bool HasAnyValue => Title != null || Artist != null || Album != null ||
        Year != null || Track != null || TrackCount != null || Genre != null ||
        Comment != null || AlbumArtist != null || Composer != null || Lyricist != null ||
        Disc != null || DiscCount != null || CoverArtData != null ||
        Lyrics != null;
}
