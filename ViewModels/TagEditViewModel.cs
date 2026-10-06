using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Models;

namespace MusicTagRecover.ViewModels;

/// <summary>
/// 左侧标签编辑面板视图模型：12 个文本字段 + 歌词 + 多图封面。
/// 所有需要弹窗/读写文件的动作通过事件交给 MainViewModel 处理，本类只维护编辑状态。
/// </summary>
public partial class TagEditViewModel : ObservableObject
{
    public const string KeepToken = "<keep>";
    public const string BlankToken = "<blank>";

    /// <summary>字段 key → 中文名（顺序即 UI 顺序）</summary>
    public static readonly (string Key, string Label)[] FieldDefs =
    {
        ("title", "标题"), ("artist", "艺术家"), ("album", "专辑"),
        ("albumartist", "专辑艺术家"), ("year", "年份"), ("track", "音轨号"),
        ("disc", "碟号"), ("genre", "风格"), ("composer", "作曲家"),
        ("lyricist", "作词家"), ("comment", "注释"),
    };

    // ===== 由 MainViewModel 订阅的交互事件 =====
    public event Action<TagFieldVm>? EncodingFixRequested;
    public event Action? LyricsEncodingFixRequested;
    public event Action? LyricsEditRequested;
    public event Action? OnlineCoverSearchRequested;
    public event Action? AddCoverFromDiskRequested;
    public event Action? ExtractCoverRequested;
    public event Action? OpenCoverExternalRequested;
    public event Action? CompressCoverRequested;

    public ObservableCollection<TagFieldVm> Fields { get; } = new();

    [ObservableProperty] private string _lyricsText = string.Empty;
    [ObservableProperty] private bool _lyricsModified;

    /// <summary>多选模式：字段默认 &lt;keep&gt;，未改动字段不写回。</summary>
    [ObservableProperty] private bool _isKeepMode;
    [ObservableProperty] private bool _hasSelection;

    // ===== 封面工作区状态 =====
    private readonly List<CoverArt> _pictures = new();
    private int _pictureIndex;
    private bool _coverEdited;          // 封面集合是否被增删改
    private bool _coverKeepUnchanged = true; // 多选模式下封面默认不动

    [ObservableProperty] private BitmapSource? _coverImage;
    [ObservableProperty] private CoverInfoVm? _coverInfo;
    [ObservableProperty] private int _pictureIndex1Based;
    [ObservableProperty] private string _pictureIndexText = "0/0";
    [ObservableProperty] private int _pictureCount;
    [ObservableProperty] private bool _overwriteExisting = true;
    [ObservableProperty] private bool _hasCover;

    private bool _loadingLyrics;

    public TagEditViewModel()
    {
        foreach (var (key, label) in FieldDefs)
            Fields.Add(new TagFieldVm(key, label, key == "comment"));
    }

    partial void OnLyricsTextChanged(string value)
    {
        if (!_loadingLyrics) LyricsModified = true;
    }

    public TagFieldVm? GetField(string key)
        => Fields.FirstOrDefault(f => f.Key == key);

    // ============================================================
    // 载入
    // ============================================================

    /// <summary>无选中：清空面板。</summary>
    public void Clear()
    {
        HasSelection = false;
        IsKeepMode = false;
        foreach (var f in Fields) f.Load("");
        LoadLyrics("");
        ResetPictures(new List<CoverArt>());
        _coverKeepUnchanged = true;
    }

    /// <summary>多选：进入 &lt;keep&gt; 模式。</summary>
    public void LoadKeepMode()
    {
        HasSelection = true;
        IsKeepMode = true;
        foreach (var f in Fields) f.LoadKeepToken();
        _loadingLyrics = true;
        LyricsText = KeepToken;
        LyricsModified = false;
        _loadingLyrics = false;
        ResetPictures(new List<CoverArt>());
        _coverKeepUnchanged = true;
        _coverEdited = false;
    }

    /// <summary>单选：载入文件当前标签（封面、歌词）。</summary>
    public void LoadSingle(MusicFile file)
    {
        HasSelection = true;
        IsKeepMode = false;

        GetField("title")!.Load(file.Title);
        GetField("artist")!.Load(file.Artist);
        GetField("album")!.Load(file.Album);
        GetField("albumartist")!.Load(file.AlbumArtist);
        GetField("year")!.Load(file.Year?.ToString());
        GetField("track")!.Load(FormatTrackLike(file.Track, file.TrackCount));
        GetField("disc")!.Load(FormatTrackLike(file.Disc, file.DiscCount));
        GetField("genre")!.Load(file.Genre);
        GetField("composer")!.Load(file.Composer);
        GetField("lyricist")!.Load(file.Lyricist);
        GetField("comment")!.Load(file.Comment);
        LoadLyrics(file.Lyrics ?? "");

        var pics = file.AllPictures?
            .Where(p => p.HasImage)
            .Select(CloneCover)
            .ToList() ?? new List<CoverArt>();
        if (pics.Count == 0 && file.CoverArtData is { Length: > 0 })
        {
            pics.Add(new CoverArt
            {
                ImageData = file.CoverArtData,
                MimeType = file.CoverArtMimeType ?? "image/jpeg",
            });
        }
        ResetPictures(pics);
        _pictureIndex = Math.Clamp(file.CurrentPictureIndex, 0, Math.Max(0, pics.Count - 1));
        _coverKeepUnchanged = false;
        _coverEdited = false;
        ShowCurrentPicture();
    }

    private static string FormatTrackLike(uint? value, uint? count)
    {
        if (!value.HasValue) return "";
        return count is > 0 ? $"{value}/{count}" : value.Value.ToString();
    }

    private void LoadLyrics(string value)
    {
        _loadingLyrics = true;
        LyricsText = value;
        LyricsModified = false;
        _loadingLyrics = false;
    }

    private static CoverArt CloneCover(CoverArt p) => new()
    {
        ImageData = p.ImageData,
        MimeType = p.MimeType,
        Width = p.Width,
        Height = p.Height,
        PictureType = p.PictureType,
    };

    // ============================================================
    // 保存时构建 TagData
    // ============================================================

    /// <summary>
    /// 根据面板内容构建写回用 TagData。
    /// keep 模式：仅改动过的字段（含 &lt;blank&gt; 清空）；单文件模式：全部字段写回。
    /// </summary>
    public TagData BuildTagData()
    {
        var data = new TagData();
        foreach (var field in Fields)
        {
            if (IsKeepMode && !field.IsModified) continue;
            var text = field.Text == BlankToken ? "" : field.Text.Trim();
            ApplyTextField(data, field.Key, text);
        }

        if (!IsKeepMode || LyricsModified)
        {
            data.Lyrics = LyricsText == BlankToken ? "" : LyricsText;
        }

        // 封面：未改动不动；改动后按"覆盖/追加"语义处理（追加由 MainViewModel 合并旧图）
        if (!_coverKeepUnchanged && _coverEdited)
        {
            data.AllPictures = _pictures.Where(p => p.HasImage).ToList();
        }
        return data;
    }

    /// <summary>多选追加模式（未勾选"覆盖"）时，MainViewModel 用这些图追加到原封面之后。</summary>
    public IReadOnlyList<CoverArt> EditedPictures => _pictures;

    /// <summary>封面是否被编辑且需要写回。</summary>
    public bool CoverChanged => !_coverKeepUnchanged && _coverEdited;

    private static void ApplyTextField(TagData data, string key, string text)
    {
        switch (key)
        {
            case "title": data.Title = text; break;
            case "artist": data.Artist = text; break;
            case "album": data.Album = text; break;
            case "albumartist": data.AlbumArtist = text; break;
            case "genre": data.Genre = text; break;
            case "composer": data.Composer = text; break;
            case "lyricist": data.Lyricist = text; break;
            case "comment": data.Comment = text; break;
            case "year": data.Year = ParseUInt(text); break;
            case "track":
                var (t, tc) = ParseTrackLike(text);
                data.Track = t; data.TrackCount = tc;
                break;
            case "disc":
                var (d, dc) = ParseTrackLike(text);
                data.Disc = d; data.DiscCount = dc;
                break;
        }
    }

    private static uint? ParseUInt(string text)
        => uint.TryParse(text.Trim(), out var v) ? v : null;

    /// <summary>解析 "3" 或 "3/10"。</summary>
    private static (uint? value, uint? count) ParseTrackLike(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return (null, null);
        var slash = text.IndexOf('/');
        if (slash < 0) return (ParseUInt(text), null);
        return (ParseUInt(text[..slash]), ParseUInt(text[(slash + 1)..]));
    }

    /// <summary>保存成功后清除所有"已修改"标记。</summary>
    public void ResetModifiedFlags()
    {
        foreach (var f in Fields) f.IsModified = false;
        LyricsModified = false;
        _coverEdited = false;
    }

    // ============================================================
    // 歌词
    // ============================================================

    [RelayCommand]
    private void EditLyrics() => LyricsEditRequested?.Invoke();

    [RelayCommand]
    private void FixLyricsEncoding() => LyricsEncodingFixRequested?.Invoke();

    /// <summary>歌词编辑器返回后写回预览。</summary>
    public void SetLyrics(string text)
    {
        _loadingLyrics = false;
        LyricsText = text;
        LyricsModified = true;
    }

    /// <summary>
    /// 外部窗口（歌词搜索、歌词编辑器）已把歌词直接写入文件标签后回填预览，
    /// 内容已落盘，因此不置脏标记，避免下次「保存修改」重复写入。
    /// </summary>
    public void LoadLyricsExternal(string text)
    {
        _loadingLyrics = true;
        LyricsText = text ?? "";
        LyricsModified = false;
        _loadingLyrics = false;
    }

    // ============================================================
    // 封面
    // ============================================================

    private void ResetPictures(List<CoverArt> pictures)
    {
        _pictures.Clear();
        _pictures.AddRange(pictures);
        _pictureIndex = 0;
        _coverEdited = false;
        ShowCurrentPicture();
    }

    private void ShowCurrentPicture()
    {
        PictureCount = _pictures.Count;
        HasCover = _pictures.Count > 0;
        PictureIndex1Based = _pictures.Count == 0 ? 0 : _pictureIndex + 1;
        PictureIndexText = _pictures.Count == 0
            ? "0/0" : $"{_pictureIndex + 1}/{_pictures.Count}";

        var pic = HasCover ? _pictures[_pictureIndex] : null;
        CoverImage = pic != null ? ImageHelper.ToBitmapSource(pic.ImageData, 240) : null;
        if (pic == null)
        {
            CoverInfo = null;
            return;
        }

        // 元数据：优先用已读出的尺寸，缺失时从解码图像补
        int w = pic.Width, h = pic.Height;
        if ((w <= 0 || h <= 0) && CoverImage != null)
        {
            w = CoverImage!.PixelWidth;
            h = CoverImage.PixelHeight;
            pic.Width = w; pic.Height = h;
        }
        CoverInfo = new CoverInfoVm(
            FormatMime(pic.MimeType),
            w > 0 ? $"{w} × {h}" : "未知",
            FileSizeConverter.FormatSize(pic.FileSizeBytes),
            DescribeType(pic.PictureType));
    }

    private static string FormatMime(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/jpeg" => "JPG",
        "image/png" => "PNG",
        "image/bmp" => "BMP",
        "image/gif" => "GIF",
        "image/webp" => "WEBP",
        _ => string.IsNullOrEmpty(mime) ? "未知" : mime.ToUpperInvariant(),
    };

    public static string DescribeType(CoverPictureType? type) => type switch
    {
        CoverPictureType.FrontCover => "封面",
        CoverPictureType.BackCover => "封底",
        CoverPictureType.LeafletPage => "插页",
        CoverPictureType.Media => "介质",
        CoverPictureType.LeadArtist => "主要艺术家",
        CoverPictureType.Artist => "艺术家",
        CoverPictureType.Conductor => "指挥",
        CoverPictureType.Band => "乐队",
        CoverPictureType.Composer => "作曲家",
        CoverPictureType.Lyricist => "作词家",
        CoverPictureType.RecordingLocation => "录制地点",
        CoverPictureType.DuringRecording => "录制中",
        CoverPictureType.DuringPerformance => "表演中",
        CoverPictureType.MovieScreenCapture => "电影截图",
        CoverPictureType.Illustration => "插图",
        CoverPictureType.BandLogo => "乐队标志",
        CoverPictureType.PublisherLogo => "出版商标志",
        _ => "其他",
    };

    /// <summary>菜单项用的常用封面类型（17 项）。</summary>
    public static IReadOnlyList<CoverPictureType> MenuTypes { get; } = new[]
    {
        CoverPictureType.FrontCover, CoverPictureType.BackCover, CoverPictureType.LeafletPage,
        CoverPictureType.Media, CoverPictureType.LeadArtist, CoverPictureType.Artist,
        CoverPictureType.Conductor, CoverPictureType.Band, CoverPictureType.Composer,
        CoverPictureType.Lyricist, CoverPictureType.RecordingLocation, CoverPictureType.DuringRecording,
        CoverPictureType.DuringPerformance, CoverPictureType.MovieScreenCapture,
        CoverPictureType.Illustration, CoverPictureType.BandLogo, CoverPictureType.PublisherLogo,
    };

    [RelayCommand]
    private void PrevPicture()
    {
        if (_pictures.Count == 0) return;
        _pictureIndex = (_pictureIndex - 1 + _pictures.Count) % _pictures.Count;
        ShowCurrentPicture();
    }

    [RelayCommand]
    private void NextPicture()
    {
        if (_pictures.Count == 0) return;
        _pictureIndex = (_pictureIndex + 1) % _pictures.Count;
        ShowCurrentPicture();
    }

    /// <summary>当前封面（供提取/外部打开/压缩使用）。</summary>
    public CoverArt? CurrentPicture => HasCover ? _pictures[_pictureIndex] : null;

    [RelayCommand]
    private void DeleteCurrentPicture()
    {
        if (!HasCover) return;
        _pictures.RemoveAt(_pictureIndex);
        if (_pictureIndex >= _pictures.Count) _pictureIndex = _pictures.Count - 1;
        _coverKeepUnchanged = false;
        _coverEdited = true;
        ShowCurrentPicture();
    }

    /// <summary>设置（替换/追加）封面。overwrite=true 替换全部；false 追加。</summary>
    public void AddPictures(IEnumerable<CoverArt> covers, bool overwrite)
    {
        var list = covers.Where(p => p.HasImage).ToList();
        if (list.Count == 0) return;
        _coverKeepUnchanged = false;
        _coverEdited = true;
        if (overwrite || _pictures.Count == 0)
        {
            ResetPictures(list);
            _coverEdited = true; // ResetPictures 会清标记
        }
        else
        {
            _pictures.AddRange(list);
            _pictureIndex = _pictures.Count - 1;
            ShowCurrentPicture();
        }
    }

    /// <summary>压缩/改分辨率完成后替换当前图。</summary>
    public void ReplaceCurrentPicture(CoverArt cover)
    {
        if (!HasCover || !cover.HasImage) return;
        _pictures[_pictureIndex] = cover;
        _coverKeepUnchanged = false;
        _coverEdited = true;
        ShowCurrentPicture();
    }

    [RelayCommand]
    private void SetPictureType(object? parameter)
    {
        if (!HasCover || parameter == null) return;
        if (Enum.TryParse<CoverPictureType>(parameter.ToString(), out var type))
        {
            _pictures[_pictureIndex].PictureType = type;
            _coverKeepUnchanged = false;
            _coverEdited = true;
            ShowCurrentPicture();
        }
    }

    [RelayCommand] private void RequestEncodingFix(object? field)
    {
        if (field is TagFieldVm f) EncodingFixRequested?.Invoke(f);
    }
    [RelayCommand] private void RequestOnlineSearch() => OnlineCoverSearchRequested?.Invoke();
    [RelayCommand] private void RequestAddFromDisk() => AddCoverFromDiskRequested?.Invoke();
    [RelayCommand] private void RequestExtract() => ExtractCoverRequested?.Invoke();
    [RelayCommand] private void RequestOpenExternal() => OpenCoverExternalRequested?.Invoke();
    [RelayCommand] private void RequestCompress() => CompressCoverRequested?.Invoke();
}
