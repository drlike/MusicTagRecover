namespace MusicTagRecover.Models;

/// <summary>
/// 封面图片类型 — 独立于 TagLib 的枚举，值与 TagLib.PictureType 兼容
/// </summary>
public enum CoverPictureType
{
    FrontCover = 0,
    BackCover = 1,
    LeafletPage = 2,
    Media = 3,
    LeadArtist = 4,
    Artist = 5,
    Conductor = 6,
    Band = 7,
    Composer = 8,
    Lyricist = 9,
    RecordingLocation = 10,
    DuringRecording = 11,
    DuringPerformance = 12,
    MovieScreenCapture = 13,
    Illustration = 14,
    BandLogo = 15,
    PublisherLogo = 16,
    FileIcon = 17,
    OtherFileIcon = 18,
    ColoredFish = 19,
    NotAPicture = 20,
    Other = 21,
}
