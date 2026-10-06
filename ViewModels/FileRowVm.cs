using CommunityToolkit.Mvvm.ComponentModel;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Models;

namespace MusicTagRecover.ViewModels;

/// <summary>
/// 文件列表行：包装 MusicFile，提供 22 列的展示文本。
/// MusicFile 非 INotifyPropertyChanged，重读标签/重命名后调用 Refresh() 通知 UI。
/// </summary>
public partial class FileRowVm : ObservableObject
{
    public MusicFile File { get; }

    public FileRowVm(MusicFile file) => File = file;

    public string Filename => File.FileName;
    public string Filedir => File.Directory;
    public string TagTypes => File.TagFormat ?? "";
    public string Title => File.Title;
    public string Artist => File.Artist;
    public string Album => File.Album;
    public string AlbumArtist => File.AlbumArtist;
    public string Year => File.Year?.ToString() ?? "";
    public string TrackStr => FormatCount(File.Track, File.TrackCount);
    public string DiscStr => FormatCount(File.Disc, File.DiscCount);
    public string Genre => File.Genre;
    public string Composer => File.Composer;
    public string Lyricist => File.Lyricist;
    public string Comment => File.Comment;
    public string HasPicture
    {
        get
        {
            // 优先用扫描阶段记录的封面数量（轻量模式下 AllPictures 未加载）
            if (File.PictureCount > 0) return File.PictureCount.ToString();
            int count = File.AllPictures?.Count(p => p.HasImage) ?? 0;
            if (count > 0) return count.ToString();
            return File.HasCoverArt ? "●" : "";
        }
    }
    public string Lyrics => File.HasLyrics ? "有" : "";
    public string Channels => File.Channels > 0 ? File.Channels.ToString() : "";
    public string SampleRate => File.SampleRate > 0
        ? (File.SampleRate >= 1000 ? (File.SampleRate / 1000.0).ToString("0.#") + " kHz"
                                  : File.SampleRate + " Hz")
        : "";
    public string BitRate => File.BitRate > 0
        ? (File.BitRate >= 1000 ? (File.BitRate / 1000) + " kbps" : File.BitRate + " bps")
        : "";
    public string BitPerSample => File.BitsPerSample > 0 ? File.BitsPerSample + " bit" : "";
    public string DurationInMs => DurationConverter.FormatDuration((long)File.Duration.TotalMilliseconds);
    public string UpdateTime => File.LastModified == default
        ? "" : File.LastModified.ToString("yyyy-MM-dd HH:mm");

    /// <summary>文件完整路径（排序服务使用 MusicFile，本属性仅供调试/ tooltip）。</summary>
    public string FilePath => File.FilePath;

    private static string FormatCount(uint? value, uint? count)
    {
        if (!value.HasValue) return "";
        return count is > 0 ? $"{value}/{count}" : value.Value.ToString();
    }

    /// <summary>文件被重读/重命名后刷新全部绑定属性。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Filename));
        OnPropertyChanged(nameof(Filedir));
        OnPropertyChanged(nameof(TagTypes));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(Album));
        OnPropertyChanged(nameof(AlbumArtist));
        OnPropertyChanged(nameof(Year));
        OnPropertyChanged(nameof(TrackStr));
        OnPropertyChanged(nameof(DiscStr));
        OnPropertyChanged(nameof(Genre));
        OnPropertyChanged(nameof(Composer));
        OnPropertyChanged(nameof(Lyricist));
        OnPropertyChanged(nameof(Comment));
        OnPropertyChanged(nameof(HasPicture));
        OnPropertyChanged(nameof(Lyrics));
        OnPropertyChanged(nameof(Channels));
        OnPropertyChanged(nameof(SampleRate));
        OnPropertyChanged(nameof(BitRate));
        OnPropertyChanged(nameof(BitPerSample));
        OnPropertyChanged(nameof(DurationInMs));
        OnPropertyChanged(nameof(UpdateTime));
        OnPropertyChanged(nameof(FilePath));
    }
}
