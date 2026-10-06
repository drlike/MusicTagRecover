using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.Services.Compare;

namespace MusicTagRecover.Services;

/// <summary>
/// 文件扫描/操作服务
/// </summary>
public class FileScannerService : IFileScannerService
{
    /// <summary>
    /// 支持加载的音视频扩展名白名单。
    /// 视频扩展名用于「预览播放」（内置 FFmpeg 解码），标签字段读不到时保持空白，不影响列表与播放。
    /// </summary>
    private static readonly HashSet<string> DefaultExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        // 音频
        ".mp3", ".flac", ".m4a", ".ogg", ".wma", ".wav",
        ".aiff", ".aif", ".ape", ".wv", ".mpc", ".opus", ".aac", ".dsf", ".dff",
        // 视频 / 容器（主流编码均可由内置 VLC 引擎播放）
        ".mp4", ".m4v", ".mpg", ".mpeg", ".mkv", ".avi", ".mov", ".wmv",
        ".flv", ".webm", ".ts", ".m2ts", ".mts", ".vob", ".3gp", ".3g2",
        ".f4v", ".divx", ".ogv", ".ogm", ".rm", ".rmvb"
    };

    private readonly ITagService _tagService;

    public FileScannerService(ITagService tagService)
    {
        _tagService = tagService;
    }

    /// <summary>
    /// 标签读取并行度。TagLibSharp2 的各格式读取相互独立（无共享句柄），
    /// 按 CPU 核数在 4~8 路之间自适应：SSD/NVMe 高并发下大目录扫描更快，
    /// 同时设上限避免机械盘因高队列深度产生抖动。
    /// </summary>
    public static int ScanParallelism { get; } =
        Math.Clamp(Environment.ProcessorCount, 4, 8);

    public async Task<IReadOnlyList<MusicFile>> ScanDirectoryAsync(
        string directory, bool includeSubDirs = true, IProgress<int>? progress = null)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<MusicFile>();

        // 流式枚举：不一次性物化全部路径，遇到无权限子目录也不中断其余枚举
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubDirs,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };
        var musicFiles = Directory.EnumerateFiles(directory, "*.*", enumerationOptions)
            .Where(IsSupportedFile)
            .ToList();

        // 并行读取（有界工作池），按下标写入预分配数组保证最终顺序与文件枚举顺序一致；
        // 目录扫描一律走轻量读取：不加载封面字节/歌词正文，选中单文件时再按需补读
        var results = new MusicFile[musicFiles.Count];
        var done = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, musicFiles.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism },
            async (i, _) =>
            {
                var mf = MusicFile.FromPath(musicFiles[i]);
                try
                {
                    var tags = await _tagService.ReadTagsSummaryAsync(musicFiles[i]);
                    ApplyTags(mf, tags);
                }
                catch
                {
                    // 单个文件读取失败不中断整批扫描
                }
                results[i] = mf;
                progress?.Report(Interlocked.Increment(ref done));
            });

        return results;
    }

    public MusicFile? AddFile(string filePath)
    {
        if (!File.Exists(filePath) || !IsSupportedFile(filePath))
            return null;
        return MusicFile.FromPath(filePath);
    }

    public IReadOnlyList<MusicFile> FilterFiles(
        IEnumerable<MusicFile> files, string? keyword = null,
        string? typeFilter = null, bool? filterByDuration = null, bool? ignoreVideo = null)
    {
        var query = files.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            query = query.Where(f =>
                f.FileName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                f.Title.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                f.Artist.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                f.Album.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        if (!string.IsNullOrWhiteSpace(typeFilter))
        {
            query = query.Where(f =>
                f.Extension.Equals(typeFilter.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (ignoreVideo == true)
        {
            query = query.Where(f => !IsVideoFile(f.Extension));
        }

        return query.ToList();
    }

    public IReadOnlyList<MusicFile> SortFiles(
        IEnumerable<MusicFile> files, string sortField = "FileName", bool ascending = true)
    {
        var sorted = sortField.ToLowerInvariant() switch
        {
            "filename" => files.OrderBy(f => f.FileName),
            "directory" => files.OrderBy(f => f.Directory),
            "audioformat" => files.OrderBy(f => f.AudioFormat),
            "title" => files.OrderBy(f => f.Title),
            "artist" => files.OrderBy(f => f.Artist).ThenBy(f => f.Title),
            "album" => files.OrderBy(f => f.Album).ThenBy(f => f.Track),
            "albumartist" => files.OrderBy(f => f.AlbumArtist),
            "year" => files.OrderBy(f => f.Year),
            "track" => files.OrderBy(f => f.Track),
            "disc" => files.OrderBy(f => f.Disc),
            "genre" => files.OrderBy(f => f.Genre),
            "composer" => files.OrderBy(f => f.Composer),
            "lyricist" => files.OrderBy(f => f.Lyricist),
            "comment" => files.OrderBy(f => f.Comment),
            "hascoverart" => files.OrderBy(f => f.HasCoverArt),
            "lyrics" => files.OrderBy(f => f.Lyrics),
            "channels" => files.OrderBy(f => f.Channels),
            "samplerate" => files.OrderBy(f => f.SampleRate),
            "bitrate" => files.OrderBy(f => f.BitRate),
            "bitspersample" => files.OrderBy(f => f.BitsPerSample),
            "duration" => files.OrderBy(f => f.Duration),
            "filesize" => files.OrderBy(f => f.FileSize),
            "lastmodified" => files.OrderBy(f => f.LastModified),
            _ => files.OrderBy(f => f.FileName)
        };
        return (ascending ? sorted : sorted.Reverse()).ToList();
    }

    public async Task<int> DeleteFilesAsync(
        IEnumerable<MusicFile> files, IProgress<int>? progress = null)
    {
        var list = files.ToList();
        var count = 0;
        for (var i = 0; i < list.Count; i++)
        {
            try
            {
                // 安全兜底：改物理删除为移入回收站（SHFileOperation FOF_ALLOWUNDO），误删可恢复
                if (RecycleBinService.DeleteToRecycleBin(list[i].FilePath))
                    count++;
            }
            catch { /* skip errors */ }
            progress?.Report(i + 1);
        }
        return await Task.FromResult(count);
    }

    public async Task<int> RenameFilesAsync(
        IEnumerable<MusicFile> files,
        Func<MusicFile, string> nameGenerator, IProgress<int>? progress = null)
    {
        var list = files.ToList();
        var count = 0;
        for (var i = 0; i < list.Count; i++)
        {
            try
            {
                var f = list[i];
                var newName = nameGenerator(f);
                if (string.IsNullOrEmpty(newName)) continue;

                var dir = Path.GetDirectoryName(f.FilePath)!;
                var newPath = Path.Combine(dir, newName + f.Extension);
                if (newPath == f.FilePath) continue;

                File.Move(f.FilePath, newPath);
                count++;
            }
            catch { /* skip errors */ }
            progress?.Report(i + 1);
        }
        return await Task.FromResult(count);
    }

    public ISet<string> GetSupportedExtensions() => new HashSet<string>(DefaultExtensions);

    public bool IsSupportedFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return DefaultExtensions.Contains(ext);
    }

    private static bool IsVideoFile(string ext) =>
        ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm";

    private static void ApplyTags(MusicFile mf, TagData tags)
    {
        mf.Title = tags.Title ?? "";
        mf.Artist = tags.Artist ?? "";
        mf.Album = tags.Album ?? "";
        mf.Year = tags.Year;
        mf.Track = tags.Track;
        mf.TrackCount = tags.TrackCount ?? 0;
        mf.Genre = tags.Genre ?? "";
        mf.Comment = tags.Comment ?? "";
        mf.AlbumArtist = tags.AlbumArtist ?? "";
        mf.Composer = tags.Composer ?? "";
        mf.Lyricist = tags.Lyricist ?? "";
        mf.Disc = tags.Disc;
        mf.DiscCount = tags.DiscCount ?? 0;
        mf.HasLyrics = !string.IsNullOrEmpty(tags.Lyrics) || tags.LyricsPresent;
        mf.HasCoverArt = tags.CoverArtData != null || tags.AllPictures is { Count: > 0 }
                         || tags.PictureCount > 0;
        mf.PictureCount = tags.AllPictures?.Count ?? tags.PictureCount;
        mf.DetailsLoaded = !tags.IsSummary;
        mf.CoverArtMimeType = tags.CoverArtMimeType;
        mf.CoverArtType = tags.CoverArtType;
        if (tags.IsSummary)
        {
            mf.CoverArtData = null;
            mf.AllPictures = null;
            mf.Lyrics = null;
        }
        else
        {
            mf.Lyrics = tags.Lyrics;
            mf.CoverArtData = tags.CoverArtData;
            if (tags.AllPictures != null) mf.AllPictures = tags.AllPictures;
        }

        // 音频属性
        if (tags.DurationMs.HasValue) mf.Duration = TimeSpan.FromMilliseconds(tags.DurationMs.Value);
        if (tags.BitRate.HasValue) mf.BitRate = tags.BitRate.Value;
        if (tags.SampleRate.HasValue) mf.SampleRate = tags.SampleRate.Value;
        if (tags.Channels.HasValue) mf.Channels = tags.Channels.Value;
        if (tags.BitsPerSample.HasValue) mf.BitsPerSample = tags.BitsPerSample.Value;
        if (tags.TagFormat != null) mf.TagFormat = tags.TagFormat;
    }
}
