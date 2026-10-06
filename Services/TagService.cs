using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using TagLib;
using TagLib.Id3v2;
using T2 = TagLibSharp2.Core;
#if NET6_0_OR_GREATER || NETFRAMEWORK
using MediaInfo;
using MediaInfo.Model;
#endif

namespace MusicTagRecover.Services;

/// <summary>
/// 标签读写服务（双轨制）：
/// <list type="bullet">
/// <item><b>音频文件</b>：读取与写入统一使用 TagLibSharp2（NuGet 包 <c>TagLibSharp2</c>，
/// 见 <see cref="TagLibSharp2Engine"/>）。该库以结果类型返回错误、重写标签区时只动标签不动音轨，
/// 对 APE/FLAC/MP3/M4A/OGG/Opus/WMA/WavPack/AIFF/DSF/DFF/MPC 均实测读写无损。
/// 引擎无法处理（WAV 中文 RIFF INFO、畸形文件、ID3v2 前缀 APE 等）时回退 TagLib(Sharp 2.3.0)。</item>
/// <item><b>视频文件</b>：标签读取专用 MediaInfo（对 MKV/AVI/TS 等容器的容错性与 VLC/FFmpeg 同级）；
/// MediaInfo 不提供的封面/歌词用 TagLib 补充。视频标签写入仍走 TagLib（含 M4aTagFixer 兜底）。</item>
/// </list>
/// </summary>
public class TagService : ITagService
{
    private readonly ILoggerService _logger;

    /// <summary>视频/容器扩展名：读走 MediaInfo，写走 TagLib。</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mpg", ".mpeg", ".mkv", ".avi", ".mov", ".wmv",
        ".flv", ".webm", ".ts", ".m2ts", ".mts", ".vob", ".3gp", ".3g2",
        ".f4v", ".divx", ".ogv", ".ogm", ".rm", ".rmvb", ".asf"
    };

    public TagService(ILoggerService logger)
    {
        _logger = logger;
    }

    private static bool IsVideo(string filePath)
        => VideoExtensions.Contains(Path.GetExtension(filePath));

    // ============================================================
    // 读取
    // ============================================================

    public Task<TagData> ReadTagsAsync(string filePath) => ReadCoreAsync(filePath, false);

    /// <summary>
    /// 轻量读取（目录扫描用）：文本标签/音频属性照常读取，
    /// 但<b>不复制任何封面字节、不持有歌词正文</b>，只返回封面数量、首图类型与有无歌词标志。
    /// 万级文件库的内存峰值主要来自内嵌封面（每图几十~几百 KB，且多在 LOH），
    /// 封面/歌词在用户选中单个文件时由 <see cref="ReadTagsAsync"/> 按需补读。
    /// </summary>
    public Task<TagData> ReadTagsSummaryAsync(string filePath) => ReadCoreAsync(filePath, true);

    private Task<TagData> ReadCoreAsync(string filePath, bool summaryOnly)
    {
        return Task.Run(() =>
        {
            try
            {
#if NET6_0_OR_GREATER || NETFRAMEWORK
                if (IsVideo(filePath))
                    return ReadVideoViaMediaInfo(filePath, summaryOnly);
#endif
                return ReadAudio(filePath, summaryOnly);
            }
            catch
            {
                return new TagData();
            }
        });
    }

    /// <summary>音频读取：轻量扫描 MediaInfo 首选；全量读取 TagLibSharp2 优先，失败回退 TagLib。</summary>
    private TagData ReadAudio(string filePath, bool summaryOnly)
    {
        if (summaryOnly)
        {
            // DFF 扫描主路径：原生 ID3v2 定向读取——只解析 PROP/ID3 帧头部，
            // APIC 封面字节用 Seek 跳过不进内存（TagLibSharp2 全量物化 ID3 chunk 是内存/IO 热点）；
            // 返回 null 表示容器畸形，落到 MediaInfo + T2 覆盖链兜底（只读不写文件）
            if (filePath.EndsWith(".dff", StringComparison.OrdinalIgnoreCase))
            {
                var dffSummary = Utils.DffId3TagIO.TryRead(filePath, full: false);
                if (dffSummary != null) return dffSummary;
            }

#if NET6_0_OR_GREATER || NETFRAMEWORK
            // 目录扫描主路径：MediaInfo 低层绑定读取（详见 TryReadSummaryViaMediaInfo 注释），
            // 不加载封面字节，内存峰值与解析耗时均为最优
            var viaMi = TryReadSummaryViaMediaInfo(filePath);
            if (viaMi != null)
            {
                // DFF 容器可同时携带 DSDIFF 原生 DIIN/COMT 标签与 ID3v2 chunk：
                // MediaInfo 把前者（抓轨工具残留的英文标题/注释）映射为 Title/Comment，
                // 而本应用写入与选中读取均走 ID3v2 → 扫描列表与编辑面板字段不一致。
                // 文本标签统一以 ID3v2(TagLibSharp2) 为准；音频属性保留 MediaInfo 结果
                // （TagLibSharp2 的 DffFile 不回填时长/采样率等属性）。
                if (filePath.EndsWith(".dff", StringComparison.OrdinalIgnoreCase))
                    OverrideDffSummaryTextFromT2(filePath, viaMi);
                return viaMi;
            }
#endif
            // MediaInfo 不可用时：TagLib 惰性封面解析（封面字节保持磁盘惰性，同样不进内存）
            var lazy = TryReadSummaryViaTagLibLazy(filePath);
            if (lazy != null) return lazy;
            // 两者都打不开的罕见格式（DSF/DFF/MPC 等）落到 TagLibSharp2 轻量路径
        }

        // DFF 全量读取：原生 ID3v2 定向解析（属性直接来自 PROP/DSD chunk，不依赖 MediaInfo 兜底）；
        // 容器畸形返回 null 时落到 OpenMediaWithDffRepair 链（先修复再用 TagLibSharp2 读）
        if (filePath.EndsWith(".dff", StringComparison.OrdinalIgnoreCase))
        {
            var dffFull = Utils.DffId3TagIO.TryRead(filePath, full: true);
            if (dffFull != null) return dffFull;
        }

        // WAV 的 RIFF INFO 在 TagLibSharp2 0.6.x 按 Latin-1 写入（中文乱码），标签统一走 TagLib 的 ID3v2
        if (TagLibSharp2Engine.IsSupported(Path.GetExtension(filePath)))
        {
            T2.IMediaFile? media = null;
            try
            {
                // 全量读取单文件时：DFF 容器畸形（错位/残留 chunk）会导致 TagLibSharp2 读不到标签，
                // 就地修复容器后重新打开，让编辑面板看到真实标签（轻量扫描不做任何文件修改）
                media = !summaryOnly && filePath.EndsWith(".dff", StringComparison.OrdinalIgnoreCase)
                    ? OpenMediaWithDffRepair(filePath)
                    : TagLibSharp2Engine.ReadFile(filePath);

                if (media != null)
                {
                    var tag = TagLibSharp2Engine.EnsureTag(media);
                    if (tag != null)
                    {
                        var data = MapT2ToTagData(media, tag, summaryOnly);

                        // TagLibSharp2 对部分 MP3/AIFF 不回填音频属性 → 用 TagLib 仅补属性
                        if (data.DurationMs is null or <= 0)
                            MergeLegacyProperties(filePath, data);

                        // 作词者不是 TagLibSharp2 通用字段：全量读取时用 TagLib 补一次
                        if (!summaryOnly)
                            data.Lyricist = TryReadLegacyLyricist(filePath);

                        // TagLibSharp2 0.6.0 的 FLAC 读取器不把 PICTURE 块映射到 Tag.Pictures
                        // （封面字节已解析但未暴露），全量读取发现零封面时用 TagLib 补一次
                        if (!summaryOnly && data.PictureCount == 0 && data.CoverArtData == null)
                            MergeLegacyPictures(filePath, data);

                        // TagLibSharp2 0.6.0 对 FLAC 码率返回固定值 2304kbps（与 TagLib/MediaInfo
                        // 实测值均不符，属引擎缺陷），全量读取时用 TagLib 计算的码率覆盖
                        if (!summaryOnly && filePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
                            OverrideLegacyBitRate(filePath, data);

                        return data;
                    }
                }
            }
            catch
            {
                // 落到 TagLib 回退
            }
            finally
            {
                (media as IDisposable)?.Dispose();
            }
        }
        return ReadTagsViaTagLib(filePath, summaryOnly);
    }

    /// <summary>
    /// 目录扫描的 TagLib 兜底：Average|PictureLazy 打开文件，
    /// 封面帧只读头部（数量/MimeType/类型），数据字节保持磁盘惰性；解析失败返回 null 交由回退链。
    /// </summary>
    private static TagData? TryReadSummaryViaTagLibLazy(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath, null,
                TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
            return MapToSummaryData(file);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用 TagLib 计算的码率强制覆盖（TagLibSharp2 对 FLAC 码率返回错误固定值）。</summary>
    private static void OverrideLegacyBitRate(string filePath, TagData data)
    {
        try
        {
            using var file = TagLib.File.Create(filePath, null,
                TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
            if (file.Properties?.AudioBitrate > 0)
                data.BitRate = file.Properties.AudioBitrate;
        }
        catch
        {
            // 码率覆盖失败不影响其它字段
        }
    }

    /// <summary>仅用 TagLib 补封面（TagLibSharp2 未暴露 PICTURE 块的格式，如 FLAC）。</summary>
    private static void MergeLegacyPictures(string filePath, TagData data)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var pictures = file.Tag.Pictures;
            if (pictures is not { Length: > 0 }) return;

            var all = new List<CoverArt>(pictures.Length);
            foreach (var pic in pictures)
            {
                if (pic.Data is not { Count: > 0 }) continue;
                all.Add(new CoverArt
                {
                    ImageData = pic.Data.Data,
                    MimeType = pic.MimeType,
                    PictureType = FromLegacyType(pic.Type),
                });
            }
            if (all.Count == 0) return;

            data.AllPictures = all;
            data.PictureCount = all.Count;
            data.CoverArtData = all[0].ImageData;
            data.CoverArtMimeType ??= all[0].MimeType;
            data.CoverArtType ??= PicTypeToName(pictures[0].Type);
        }
        catch
        {
            // 封面补读失败不影响标签文本
        }
    }

    /// <summary>将 TagLibSharp2 媒体对象映射为 TagData。</summary>
    private static TagData MapT2ToTagData(T2.IMediaFile media, T2.Tag tag, bool summaryOnly)
    {
        var data = new TagData
        {
            Title = NullIfEmpty(tag.Title),
            Artist = NullIfEmpty(FirstOrNull(tag.Artist, tag.Performers)),
            Album = NullIfEmpty(tag.Album),
            Year = ParseUint(tag.Year),
            Track = tag.Track is 0 ? null : tag.Track,
            TrackCount = tag.TotalTracks is 0 or null ? null : tag.TotalTracks,
            Genre = NullIfEmpty(FirstOrNull(tag.Genre, tag.Genres)),
            Comment = NullIfEmpty(tag.Comment),
            AlbumArtist = NullIfEmpty(FirstOrNull(tag.AlbumArtist, tag.AlbumArtists)),
            Composer = NullIfEmpty(FirstOrNull(tag.Composer, tag.Composers)),
            Disc = tag.DiscNumber is 0 or null ? null : tag.DiscNumber,
            DiscCount = tag.TotalDiscs is 0 or null ? null : tag.TotalDiscs,
            Lyrics = summaryOnly ? null : NullIfEmpty(tag.Lyrics),
            LyricsPresent = !string.IsNullOrEmpty(tag.Lyrics),
            IsSummary = summaryOnly,
            TagFormat = DescribeT2Tag(tag.TagType, media),
        };

        var pictures = tag.Pictures;
        if (pictures is { Length: > 0 })
        {
            if (summaryOnly)
            {
                // 轻量扫描：只读 Length/MimeType，不复制图片字节
                T2.IPicture? first = null;
                var count = 0;
                foreach (var pic in pictures)
                {
                    if (pic.PictureData.Length <= 0) continue;
                    count++;
                    first ??= pic;
                }
                data.PictureCount = count;
                if (first != null)
                {
                    data.CoverArtMimeType = first.MimeType;
                    data.CoverArtType = PicTypeToName(T2ToLegacyType(first.PictureType));
                }
            }
            else
            {
                List<CoverArt>? all = null;
                foreach (var pic in pictures)
                {
                    if (pic.PictureData.Length <= 0) continue;
                    var bytes = pic.PictureData.Span.ToArray();
                    var art = new CoverArt
                    {
                        ImageData = bytes,
                        MimeType = pic.MimeType,
                        PictureType = FromT2Type(pic.PictureType),
                    };
                    all ??= new List<CoverArt>(pictures.Length);
                    all.Add(art);
                }
                if (all is { Count: > 0 })
                {
                    data.AllPictures = all;
                    data.PictureCount = all.Count;
                    data.CoverArtData = all[0].ImageData;
                    data.CoverArtMimeType = all[0].MimeType;
                    data.CoverArtType = PicTypeToName(T2ToLegacyType(pictures[0].PictureType));
                }
            }
        }

        if (media.AudioProperties is T2.AudioProperties ap && ap.IsValid)
        {
            data.DurationMs = (long)ap.Duration.TotalMilliseconds;
            data.BitRate = ap.Bitrate > 0 ? ap.Bitrate : null;
            data.SampleRate = ap.SampleRate > 0 ? ap.SampleRate : null;
            data.Channels = ap.Channels > 0 ? ap.Channels : null;
            data.BitsPerSample = ap.BitsPerSample > 0 ? ap.BitsPerSample : null;
        }
        return data;
    }

    private static string? FirstOrNull(string? primary, string[]? alternatives)
        => !string.IsNullOrEmpty(primary) ? primary : alternatives?.FirstOrDefault(s => !string.IsNullOrEmpty(s));

    private static uint? ParseUint(string? text)
        => uint.TryParse(text?.Trim(), out var v) && v > 0 ? v : null;

    private static string DescribeT2Tag(T2.TagTypes tagType, T2.IMediaFile media)
    {
        var name = tagType switch
        {
            T2.TagTypes.Id3v2 => "ID3v2",
            T2.TagTypes.Id3v1 => "ID3v1",
            T2.TagTypes.Ape => "APE",
            T2.TagTypes.Xiph or T2.TagTypes.FlacMetadata => "Vorbis Comment",
            T2.TagTypes.Apple => "MP4(iTunes)",
            T2.TagTypes.Asf => "ASF/WMA",
            T2.TagTypes.RiffInfo => "RIFF INFO",
            _ => tagType == T2.TagTypes.None ? "" : tagType.ToString(),
        };
        var codec = (media.AudioProperties is T2.AudioProperties ap && ap.IsValid) ? ap.Codec : null;
        return string.IsNullOrEmpty(codec) ? name : $"{name} · {codec}";
    }

    /// <summary>仅用 TagLib 补音频属性（TagLibSharp2 未回填时），不覆盖标签文本。</summary>
    private static void MergeLegacyProperties(string filePath, TagData data)
    {
        try
        {
            // 这里只缺音频属性：PictureLazy 让封面帧延迟加载（我们不碰图片），
            // 显著降低这次"补属性"二次打开文件的解析开销
            using var file = TagLib.File.Create(filePath, null,
                TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
            var props = file.Properties;
            if (data.DurationMs is null or <= 0)
                data.DurationMs = (long?)props?.Duration.TotalMilliseconds;
            if (data.BitRate is null or <= 0) data.BitRate = props?.AudioBitrate;
            if (data.SampleRate is null or <= 0) data.SampleRate = props?.AudioSampleRate;
            if (data.Channels is null or <= 0) data.Channels = props?.AudioChannels;
            if (data.BitsPerSample is null or <= 0 && props?.BitsPerSample > 0)
                data.BitsPerSample = props.BitsPerSample;
            if (string.IsNullOrEmpty(data.TagFormat)) data.TagFormat = props?.Description;
        }
        catch
        {
            // 属性补读失败不影响标签文本
        }

#if NET6_0_OR_GREATER || NETFRAMEWORK
        // TagLib 不支持的格式（DFF 等，TagLib 对其抛 UnsupportedFormatException）：
        // 用 MediaInfo 补齐音频属性，保证选中读取与扫描（MediaInfo 主路径）的属性字段一致
        MergeMediaInfoProperties(filePath, data);
#endif
    }

#if NET6_0_OR_GREATER || NETFRAMEWORK
    /// <summary>用 MediaInfo 补齐缺失的音频属性（不覆盖已有值，不触碰标签文本）。</summary>
    private static void MergeMediaInfoProperties(string filePath, TagData data)
    {
        try
        {
            using var reader = new MediaInfoScanReader();
            if (!reader.Open(filePath)) return;

            if (data.DurationMs is null or <= 0
                && long.TryParse(reader.General("Duration"), out var dur) && dur > 0)
                data.DurationMs = dur;
            if (data.BitRate is null or <= 0)
            {
                if (long.TryParse(reader.Audio("BitRate"), out var br) && br > 0)
                    data.BitRate = (int)Math.Round(br / 1000.0);
                else if (long.TryParse(reader.General("OverallBitRate"), out var obr) && obr > 0)
                    data.BitRate = (int)Math.Round(obr / 1000.0);
            }
            if (data.SampleRate is null or <= 0
                && int.TryParse(reader.Audio("SamplingRate"), out var sr) && sr > 0)
                data.SampleRate = sr;
            if (data.Channels is null or <= 0
                && int.TryParse(reader.Audio("Channels"), out var ch) && ch > 0)
                data.Channels = ch;
            if (data.BitsPerSample is null or <= 0
                && int.TryParse(reader.Audio("BitDepth"), out var bd) && bd > 0)
                data.BitsPerSample = bd;
            reader.Close();
        }
        catch
        {
            // MediaInfo 补读失败不影响已读字段
        }
    }
#endif

    private static string? TryReadLegacyLyricist(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            return ReadLyricist(file);
        }
        catch
        {
            return null;
        }
    }

#if NET6_0_OR_GREATER || NETFRAMEWORK
    // ============================================================
    // 视频读取（MediaInfo 专用，封面/歌词 TagLib 补充）
    // ============================================================

    private TagData ReadVideoViaMediaInfo(string filePath, bool summaryOnly)
    {
        try
        {
            var mi = new MediaInfoWrapper(filePath);
            if (mi.Success)
            {
                // 视频文件的标题/艺术家在 General(Tags)；若有音轨标签也一并参考
                var result = MapMediaInfoToTagData(mi, mi.Tags);
                try
                {
                    using var tagFile = TagLib.File.Create(filePath);
                    if (summaryOnly)
                        ApplySummary(result, tagFile);
                    else
                        ApplyFullArtwork(result, tagFile);
                }
                catch
                {
                    /* TagLib 补充失败（如 MKV 不支持）不影响 MediaInfo 数据 */
                }
                return result;
            }
        }
        catch
        {
            // 落到 TagLib 回退
        }
        return ReadTagsViaTagLib(filePath, summaryOnly);
    }

    /// <summary>全量补充：复制全部封面字节与歌词正文（选中单文件编辑时使用）。</summary>
    private static void ApplyFullArtwork(TagData result, TagLib.File tagFile)
    {
        var tag = MapToTagData(tagFile);
        if (tag.CoverArtData != null) result.CoverArtData = tag.CoverArtData;
        if (tag.CoverArtMimeType != null) result.CoverArtMimeType = tag.CoverArtMimeType;
        if (tag.CoverArtType != null) result.CoverArtType = tag.CoverArtType;
        if (tag.AllPictures != null)
        {
            result.AllPictures = tag.AllPictures;
            result.PictureCount = tag.AllPictures.Count;
        }
        if (tag.Lyrics != null) result.Lyrics = tag.Lyrics;
    }

    /// <summary>轻量补充：只统计封面数量/首图类型与歌词存在性，不复制图片字节与歌词文本。</summary>
    private static void ApplySummary(TagData result, TagLib.File tagFile)
    {
        result.IsSummary = true;
        try
        {
            var pictures = tagFile.Tag.Pictures;
            if (pictures != null && pictures.Length > 0)
            {
                TagLib.IPicture? first = null;
                var count = 0;
                foreach (var pic in pictures)
                {
                    // pic.Data.Count 读取 ByteVector 长度，不会像 pic.Data.Data 那样复制整个数组
                    if (pic.Data is { Count: > 0 })
                    {
                        count++;
                        first ??= pic;
                    }
                }
                result.PictureCount = count;
                if (first != null)
                {
                    result.CoverArtMimeType = first.MimeType;
                    result.CoverArtType = PicTypeToName(first.Type);
                }
            }
            // 仅探测存在性：tag.Lyrics 会构造字符串，但属于短命临时对象，扫描结束即可回收
            result.LyricsPresent = !string.IsNullOrEmpty(tagFile.Tag.Lyrics);
        }
        catch
        {
            // 统计失败按 0 处理，不影响文本标签
        }
    }

    /// <summary>MediaInfo → TagData 映射</summary>
    private static TagData MapMediaInfoToTagData(MediaInfoWrapper mi, AudioTags? tags)
    {
        var result = new TagData();

        if (tags != null)
        {
            result.Title = NullIfEmpty(tags.Title);
            result.Artist = NullIfEmpty(tags.Artist);
            result.Album = NullIfEmpty(tags.Album);
            result.AlbumArtist = NullIfEmpty(tags.AlbumArtist);
            result.Genre = NullIfEmpty(tags.Genre);
            result.Comment = NullIfEmpty(tags.Comment);
            result.Composer = NullIfEmpty(tags.Composer);
            result.Lyricist = NullIfEmpty(tags.Lyricist);

            if (tags.RecordedDate.HasValue)
            {
                var year = (uint?)tags.RecordedDate.Value.Year;
                if (year > 0) result.Year = year;
            }
            if (tags.TrackPosition.HasValue && tags.TrackPosition.Value > 0)
                result.Track = (uint?)tags.TrackPosition.Value;
            if (tags.TotalTracks.HasValue && tags.TotalTracks.Value > 0)
                result.TrackCount = (uint?)tags.TotalTracks.Value;
            if (tags.DiscNumber.HasValue && tags.DiscNumber.Value > 0)
                result.Disc = (uint?)tags.DiscNumber.Value;
            if (tags.TotalDiscs.HasValue && tags.TotalDiscs.Value > 0)
                result.DiscCount = (uint?)tags.TotalDiscs.Value;
        }

        var audioStream = mi.AudioStreams.FirstOrDefault();
        if (mi.Duration > 0) result.DurationMs = mi.Duration;
        if (mi.AudioSampleRate > 0) result.SampleRate = mi.AudioSampleRate;
        // AudioRate 单位是 bps，TagData.BitRate 统一为 kbps（与 TagLib 约定一致）
        if (mi.AudioRate > 0) result.BitRate = (int)Math.Round(mi.AudioRate / 1000.0);
        if (mi.AudioChannels > 0) result.Channels = mi.AudioChannels;
        if (audioStream?.BitDepth > 0) result.BitsPerSample = audioStream.BitDepth;

        if (!string.IsNullOrEmpty(mi.Format))
            result.TagFormat = mi.Format;

        return result;
    }

    // ============================================================
    // MediaInfo 轻量扫描（目录批量读取的主路径）
    // ============================================================
    //
    // 设计要点（对应扫描性能/内存优化）：
    // 1. 使用低层绑定 MediaInfo.MediaInfo 而非 MediaInfoWrapper：
    //    wrapper 构造时会构建视频流/音频流/字幕/章节等全部对象并查询数百个字段，
    //    低层绑定只按需查询约 20 个字段，单文件解析开销显著降低。
    // 2. 确定性资源管理：MediaInfo 实现 IDisposable，Open→Get→Close→Dispose 后
    //    原生句柄立即归还；避免 MediaInfoWrapper 依赖终结器回收造成的原生内存滞留
    //    （终结器延迟回收还会在进程退出时偶发原生崩溃）。
    // 3. 封面字节天然不进入内存：MediaInfo 只报告 Cover/Cover_Mime/Cover_Type 元数据，
    //    不导出图片数据——这是扫描内存峰值比 TagLibSharp2 低约两个数量级的根本原因。
    //    代价是封面计数只能区分 0/1（MediaInfo 不报告张数），扫描列表场景可接受。
    // 4. 解析失败返回 null，由 TagLib（PictureLazy）/TagLibSharp2 回退链兜底，格式覆盖不缩水。

    /// <summary>MediaInfo 低层绑定读取器：按字段名精确查询，避免 wrapper 的全量构建开销。</summary>
    private sealed class MediaInfoScanReader : IDisposable
    {
        private readonly MediaInfo.MediaInfo _mi = new();

        /// <summary>打开文件，返回是否解析成功（原生返回空指针即失败）。</summary>
        public bool Open(string filePath) => _mi.Open(filePath) != IntPtr.Zero;

        /// <summary>General 流字段（标签文本/时长/封面标志/歌词等）。</summary>
        public string General(string name) => _mi.Get(StreamKind.General, 0, name);

        /// <summary>首个 Audio 流字段（码率/采样率/声道/位深）。</summary>
        public string Audio(string name) => _mi.Get(StreamKind.Audio, 0, name);

        public void Close() => _mi.Close();

        public void Dispose() => _mi.Dispose();
    }

    /// <summary>
    /// 用 MediaInfo 读取轻量摘要：标签文本 + 音频属性 + 封面/歌词存在性。
    /// 不复制封面字节、不构造歌词字符串以外的临时大对象。
    /// </summary>
    private static TagData? TryReadSummaryViaMediaInfo(string filePath)
    {
        try
        {
            using var reader = new MediaInfoScanReader();
            if (!reader.Open(filePath)) return null;

            var data = new TagData { IsSummary = true };

            // --- 标签文本字段（MediaInfo General 参数名） ---
            data.Title = NullIfEmpty(reader.General("Title"));
            data.Artist = NullIfEmpty(reader.General("Performer"));
            data.Album = NullIfEmpty(reader.General("Album"));
            data.AlbumArtist = NullIfEmpty(reader.General("Album/Performer"));
            data.Composer = NullIfEmpty(reader.General("Composer"));
            data.Lyricist = NullIfEmpty(reader.General("Lyricist"));
            data.Genre = NullIfEmpty(reader.General("Genre"));
            data.Comment = NullIfEmpty(reader.General("Comment"));

            // 年份：Released_Date 优先，兼容 "2013" / "2013-05-01" / "UTC 2013-..." 等写法
            data.Year = ParseYear4(reader.General("Released_Date"))
                        ?? ParseYear4(reader.General("Recorded_Date"));

            // 音轨/碟号：字段值可能是 "3" 或 "3/12" 组合形式
            var (track, trackTotal) = SplitPosition(reader.General("Track/Position"));
            data.Track = track;
            data.TrackCount = trackTotal ?? ParseUint(reader.General("Track/Position_Total"));
            var (disc, discTotal) = SplitPosition(reader.General("Part/Position"));
            data.Disc = disc;
            data.DiscCount = discTotal ?? ParseUint(reader.General("Part/Position_Total"));

            // --- 音频属性 ---
            if (long.TryParse(reader.General("Duration"), out var durationMs) && durationMs > 0)
                data.DurationMs = durationMs;
            // BitRate 单位 bps → kbps；音频流缺失时回退总体码率
            if (long.TryParse(reader.Audio("BitRate"), out var bitRate) && bitRate > 0)
                data.BitRate = (int)Math.Round(bitRate / 1000.0);
            else if (long.TryParse(reader.General("OverallBitRate"), out var overallRate) && overallRate > 0)
                data.BitRate = (int)Math.Round(overallRate / 1000.0);
            if (int.TryParse(reader.Audio("SamplingRate"), out var sampleRate) && sampleRate > 0)
                data.SampleRate = sampleRate;
            if (int.TryParse(reader.Audio("Channels"), out var channels) && channels > 0)
                data.Channels = channels;
            if (int.TryParse(reader.Audio("BitDepth"), out var bitDepth) && bitDepth > 0)
                data.BitsPerSample = bitDepth;

            // --- 格式描述 ---
            var format = reader.General("Format");
            if (!string.IsNullOrEmpty(format))
                data.TagFormat = format;

            // --- 封面存在性（只读标志，不导出字节） ---
            if (string.Equals(reader.General("Cover"), "Yes", StringComparison.OrdinalIgnoreCase))
            {
                data.PictureCount = 1;
                data.CoverArtMimeType = NullIfEmpty(reader.General("Cover_Mime"));
                data.CoverArtType = NullIfEmpty(reader.General("Cover_Type"));
            }

            // --- 歌词存在性（只判断非空，不持有正文） ---
            data.LyricsPresent = !string.IsNullOrEmpty(reader.General("Lyrics"));

            reader.Close();
            return data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// DFF 扫描摘要的文本标签统一改写：用 TagLibSharp2 读取 ID3v2 覆盖
    /// MediaInfo 从 DSDIFF 原生 DIIN/COMT chunk 映射来的字段。
    /// 音频属性/封面存在性保留 MediaInfo 结果；T2 读取失败（如畸形容器 Tag==null）
    /// 时保留 MediaInfo 值，扫描阶段绝不修改文件。
    /// </summary>
    private static void OverrideDffSummaryTextFromT2(string filePath, TagData data)
    {
        if (!TagLibSharp2Engine.IsSupported(Path.GetExtension(filePath))) return;

        T2.IMediaFile? media = null;
        try
        {
            media = TagLibSharp2Engine.ReadFile(filePath);
            var tag = media?.Tag;
            if (tag == null) return; // 容器畸形读不到 ID3v2：保留 MediaInfo 结果，不在扫描期修文件

            data.Title = NullIfEmpty(tag.Title);
            data.Artist = NullIfEmpty(FirstOrNull(tag.Artist, tag.Performers));
            data.Album = NullIfEmpty(tag.Album);
            data.AlbumArtist = NullIfEmpty(FirstOrNull(tag.AlbumArtist, tag.AlbumArtists));
            data.Genre = NullIfEmpty(FirstOrNull(tag.Genre, tag.Genres));
            data.Comment = NullIfEmpty(tag.Comment);
            data.Composer = NullIfEmpty(FirstOrNull(tag.Composer, tag.Composers));
            data.Year = ParseUint(tag.Year);
            data.Track = tag.Track is 0 ? null : tag.Track;
            data.TrackCount = tag.TotalTracks is 0 or null ? null : tag.TotalTracks;
            data.Disc = tag.DiscNumber is 0 or null ? null : tag.DiscNumber;
            data.DiscCount = tag.TotalDiscs is 0 or null ? null : tag.TotalDiscs;
            data.Lyricist = TryReadLegacyLyricist(filePath);
            data.LyricsPresent = !string.IsNullOrEmpty(tag.Lyrics);

            // 封面张数：T2 能区分多张，覆盖 MediaInfo 的 0/1 粗值
            var pics = tag.Pictures;
            if (pics is { Length: > 0 })
            {
                var count = 0;
                T2.IPicture? first = null;
                foreach (var pic in pics)
                {
                    if (pic.PictureData.Length <= 0) continue;
                    count++;
                    first ??= pic;
                }
                data.PictureCount = count;
                if (first != null)
                {
                    data.CoverArtMimeType = first.MimeType;
                    data.CoverArtType = PicTypeToName(T2ToLegacyType(first.PictureType));
                }
            }
            else
            {
                data.PictureCount = 0;
                data.CoverArtMimeType = null;
                data.CoverArtType = null;
            }

            // TagFormat 与选中读取同语义（"ID3v2 · DSD" 风格）
            data.TagFormat = DescribeT2Tag(tag.TagType, media!);
        }
        catch
        {
            // T2 覆盖失败：保留 MediaInfo 摘要，扫描不中断
        }
        finally
        {
            (media as IDisposable)?.Dispose();
        }
    }

    /// <summary>从日期字符串中提取 4 位年份（兼容 ISO/UTC 前缀等写法）。</summary>
    private static uint? ParseYear4(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        for (var i = 0; i + 4 <= text.Length; i++)
        {
            if (char.IsDigit(text[i]) && char.IsDigit(text[i + 1])
                && char.IsDigit(text[i + 2]) && char.IsDigit(text[i + 3])
                && uint.TryParse(text.AsSpan(i, 4), out var year) && year is >= 1600 and <= 2100)
                return year;
        }
        return null;
    }

    /// <summary>解析 "3" 或 "3/12" 形式的位置字段，返回 (位置, 总数)。</summary>
    private static (uint? Position, uint? Total) SplitPosition(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var slash = text.IndexOf('/');
        if (slash < 0)
            return (ParseUint(text.Trim()), null);
        return (ParseUint(text[..slash].Trim()), ParseUint(text[(slash + 1)..].Trim()));
    }
#endif

    // ============================================================
    // 写入
    // ============================================================

    /// <summary>批量读取并行度（与目录扫描的 4 路工作池策略保持一致）。</summary>
    private const int BatchReadParallelism = 4;

    public async Task<IReadOnlyList<TagData>> ReadTagsBatchAsync(IEnumerable<string> filePaths)
    {
        var paths = filePaths as IList<string> ?? filePaths.ToList();
        var results = new TagData[paths.Count];

        // 各文件读取完全独立（独立的 TagLibSharp2 句柄），4 路并行按下标归位，保持输入顺序
        await Parallel.ForEachAsync(
            Enumerable.Range(0, paths.Count),
            new ParallelOptions { MaxDegreeOfParallelism = BatchReadParallelism },
            async (i, _) => results[i] = await ReadTagsAsync(paths[i]));

        return results;
    }

    /// <summary>
    /// 打开 TagLibSharp2 媒体文件；当 DFF 容器因 chunk 错位/历史残留数据畸形导致标签整体不可读
    /// （TagLibSharp2 的 DffFile 按偶数边界严格遍历，此时 media.Tag 为 null，
    /// 随后的“新建空标签 + Save”会返回成功但实际不落盘）时，先就地修复 DFF 容器再重新打开。
    /// 结构完整（含本来就没有标签）的文件不会被修改。返回 null 表示文件无法解析，调用方负责 Dispose。
    /// </summary>
    private T2.IMediaFile? OpenMediaWithDffRepair(string filePath)
    {
        var media = TagLibSharp2Engine.ReadFile(filePath);
        if (media?.Tag != null || !filePath.EndsWith(".dff", StringComparison.OrdinalIgnoreCase))
            return media;

        (media as IDisposable)?.Dispose();
        if (Utils.DffTagFixer.TryFix(filePath, out var fixReason))
        {
            _logger.Warn($"DFF 容器畸形导致标签不可读，已自动修复容器结构：{filePath}");
        }
        else if (!string.IsNullOrEmpty(fixReason) && fixReason != "容器 chunk 结构完整，跳过修复")
        {
            _logger.Warn($"DFF 容器自动修复未执行：{filePath}，原因：{fixReason}");
        }

        return TagLibSharp2Engine.ReadFile(filePath);
    }

    public Task<bool> WriteTagsAsync(string filePath, TagData tags, bool keepUpdateTime = false)
        => RunTagWriteWithRetryAsync(filePath, () =>
        {
            // 视频/WAV/引擎不支持的扩展名 → TagLib 路径（含 M4aTagFixer 兜底）
            if (IsVideo(filePath) || !TagLibSharp2Engine.IsSupported(Path.GetExtension(filePath)))
                return WriteTagsViaLegacy(filePath, tags, keepUpdateTime);

            var fileInfo = new FileInfo(filePath);
            var lastWriteTime = fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.MinValue;

            // DFF：原生 ID3v2 定向写入（重建 ID3v2.4 帧序列 + 原子替换，不触碰 DSD 音频块）；
            // 作词者 TEXT 帧一并处理，无需 TagLib 补写
            if (filePath.EndsWith(".dff", StringComparison.OrdinalIgnoreCase))
            {
                if (Utils.DffId3TagIO.TryWrite(filePath, tags, out var dffError))
                {
                    if (keepUpdateTime && lastWriteTime != DateTime.MinValue)
                        System.IO.File.SetLastWriteTime(filePath, lastWriteTime);
                    return true;
                }
                _logger.Warn($"DFF 原生标签写入失败（回退 TagLibSharp2）：{filePath}，原因：{dffError}");
            }

            T2.IMediaFile? media = null;
            try
            {
                media = OpenMediaWithDffRepair(filePath);
                if (media == null)
                    return WriteTagsViaLegacy(filePath, tags, keepUpdateTime);

                var tag = TagLibSharp2Engine.EnsureTag(media);
                if (tag == null)
                    return WriteTagsViaLegacy(filePath, tags, keepUpdateTime);

                ApplyTagDataT2(tag, tags);
                if (!TagLibSharp2Engine.Save(media, filePath, out var error))
                {
                    _logger.Warn($"TagLibSharp2 写入失败（回退 TagLib）：{filePath}，原因：{error}");
                    return WriteTagsViaLegacy(filePath, tags, keepUpdateTime);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 文件被占用/无权限：交由统一外壳重试，不回退（回退也会遇到同样的锁）
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn($"TagLibSharp2 写入异常（回退 TagLib）：{filePath}，原因：{ex.Message}");
                return WriteTagsViaLegacy(filePath, tags, keepUpdateTime);
            }
            finally
            {
                (media as IDisposable)?.Dispose();
            }

            // 作词者不是 TagLibSharp2 通用字段，保存后用 TagLib 补写 TEXT/LYRICIST/WM Lyricist
            if (tags.Lyricist != null)
                WriteLegacyLyricistQuiet(filePath, tags.Lyricist);

            if (keepUpdateTime && lastWriteTime != DateTime.MinValue)
                System.IO.File.SetLastWriteTime(filePath, lastWriteTime);
            return true;
        });

    /// <summary>TagData → TagLibSharp2 Tag；null 表示“不修改该字段”，0/空串表示清空。</summary>
    private static void ApplyTagDataT2(T2.Tag tag, TagData tags)
    {
        if (tags.Title != null) tag.Title = tags.Title;
        if (tags.Artist != null) tag.Performers = new[] { tags.Artist };
        if (tags.Album != null) tag.Album = tags.Album;
        if (tags.Year.HasValue) tag.Year = tags.Year.Value > 0 ? tags.Year.Value.ToString() : string.Empty;
        // 注意：TagLibSharp2 把 null 音轨/碟片号序列化成 "1"（位置字段的空值怪癖），
        // 清空语义必须写 0；读取侧 MapT2ToTagData 已将 0 归一回 null
        if (tags.Track.HasValue) tag.Track = tags.Track.Value > 0 ? tags.Track.Value : 0u;
        if (tags.TrackCount.HasValue) tag.TotalTracks = tags.TrackCount.Value > 0 ? tags.TrackCount.Value : 0u;
        if (tags.Genre != null) tag.Genres = new[] { tags.Genre };
        if (tags.Comment != null) tag.Comment = tags.Comment;
        if (tags.AlbumArtist != null) tag.AlbumArtists = new[] { tags.AlbumArtist };
        if (tags.Composer != null) tag.Composers = new[] { tags.Composer };
        if (tags.Disc.HasValue) tag.DiscNumber = tags.Disc.Value > 0 ? tags.Disc.Value : 0u;
        if (tags.DiscCount.HasValue) tag.TotalDiscs = tags.DiscCount.Value > 0 ? tags.DiscCount.Value : 0u;
        if (tags.Lyrics != null) tag.Lyrics = tags.Lyrics;

        if (tags.AllPictures is { Count: > 0 })
        {
            tag.Pictures = tags.AllPictures
                .Where(p => p.HasImage && p.ImageData != null)
                .Select(p => TagLibSharp2Engine.CreatePicture(
                    p.ImageData!,
                    p.MimeType ?? "image/jpeg",
                    CoverToT2Type(p.PictureType)))
                .ToArray();
        }
        else if (tags.CoverArtData != null)
        {
            tag.Pictures = new T2.IPicture[]
            {
                TagLibSharp2Engine.CreatePicture(tags.CoverArtData, tags.CoverArtMimeType ?? "image/jpeg", T2.PictureType.FrontCover)
            };
        }
    }

    public async Task<int> WriteTagsBatchAsync(
        IEnumerable<KeyValuePair<string, TagData>> fileTags,
        bool keepUpdateTime = false, IProgress<int>? progress = null)
    {
        var items = fileTags.ToList();
        var successCount = 0;

        for (var i = 0; i < items.Count; i++)
        {
            var success = await WriteTagsAsync(items[i].Key, items[i].Value, keepUpdateTime);
            if (success) successCount++;
            progress?.Report(i + 1);
        }

        return successCount;
    }

    public Task<bool> ClearTagsAsync(string filePath)
    {
        return Task.Run(() =>
        {
            // DFF：原生定向删除整个 ID3 chunk（原子重建容器，DSD 音频块不动）
            if (filePath.EndsWith(".dff", StringComparison.OrdinalIgnoreCase))
            {
                if (Utils.DffId3TagIO.TryDelete(filePath, out var dffDeleteError))
                    return true;
                _logger.Warn($"DFF 原生标签删除失败（回退 TagLibSharp2）：{filePath}，原因：{dffDeleteError}");
            }

            // 音频：TagLibSharp2 直接清空整个标签块；视频/不支持格式沿用空值覆盖写
            if (!IsVideo(filePath) && TagLibSharp2Engine.IsSupported(Path.GetExtension(filePath)))
            {
                T2.IMediaFile? media = null;
                try
                {
                    media = OpenMediaWithDffRepair(filePath);
                    if (media != null)
                    {
                        var tag = TagLibSharp2Engine.EnsureTag(media);
                        if (tag != null)
                        {
                            tag.Clear();
                            if (TagLibSharp2Engine.Save(media, filePath, out _))
                                return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"TagLibSharp2 清空标签失败（回退 TagLib）：{filePath}，原因：{ex.Message}");
                }
                finally
                {
                    (media as IDisposable)?.Dispose();
                }
            }

            return WriteTagsViaLegacy(filePath, new TagData
            {
                Title = "",
                Artist = "",
                Album = "",
                Year = 0,
                Track = 0,
                TrackCount = 0,
                Genre = "",
                Comment = "",
                AlbumArtist = "",
                Composer = "",
                Disc = 0,
                DiscCount = 0,
                Lyrics = ""
            }, false);
        });
    }

    public Task<bool> WriteCoverArtAsync(string filePath, CoverArt cover)
    {
        return Task.Run(() =>
        {
            if (!cover.HasImage) return false;

            // DFF：原生 APIC 帧替换（保留其余帧，原子替换容器）
            if (filePath.EndsWith(".dff", StringComparison.OrdinalIgnoreCase))
            {
                if (Utils.DffId3TagIO.TryWriteCover(filePath, cover, out var dffCoverError))
                    return true;
                _logger.Warn($"DFF 原生封面写入失败（回退 TagLibSharp2）：{filePath}，原因：{dffCoverError}");
            }

            if (!IsVideo(filePath) && TagLibSharp2Engine.IsSupported(Path.GetExtension(filePath)))
            {
                T2.IMediaFile? media = null;
                try
                {
                    media = OpenMediaWithDffRepair(filePath);
                    if (media != null)
                    {
                        var tag = TagLibSharp2Engine.EnsureTag(media);
                        if (tag != null)
                        {
                            tag.Pictures = new T2.IPicture[]
                            {
                                TagLibSharp2Engine.CreatePicture(cover.ImageData!, cover.MimeType ?? "image/jpeg",
                                    T2.PictureType.FrontCover)
                            };
                            if (TagLibSharp2Engine.Save(media, filePath, out _))
                                return true;
                        }
                    }
                }
                catch
                {
                    // 回退 TagLib
                }
                finally
                {
                    (media as IDisposable)?.Dispose();
                }
            }

            // 视频/WAV/失败回退
            try
            {
                using var file = TagLib.File.Create(filePath);
                file.Tag.Pictures = new IPicture[]
                {
                    new TagLib.Picture(new ByteVector(cover.ImageData))
                    {
                        MimeType = cover.MimeType ?? "image/jpeg",
                        Type = PictureType.FrontCover
                    }
                };
                file.Save();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        });
    }

    public Task<CoverArt?> ReadCoverArtAsync(string filePath)
    {
        return Task.Run(() =>
        {
            // DFF：原生定向读取封面（选中时才物化 APIC 字节）
            if (filePath.EndsWith(".dff", StringComparison.OrdinalIgnoreCase))
            {
                var dffFull = Utils.DffId3TagIO.TryRead(filePath, full: true);
                if (dffFull != null)
                {
                    var cover = dffFull.AllPictures?.FirstOrDefault(p => p.HasImage);
                    if (cover != null)
                        return new CoverArt { ImageData = cover.ImageData, MimeType = cover.MimeType };
                    return null; // 容器正常但无封面
                }
                // 容器畸形：落到 TagLibSharp2 修复链
            }

            // 音频：TagLibSharp2；视频/WAV/失败：TagLib
            if (!IsVideo(filePath) && TagLibSharp2Engine.IsSupported(Path.GetExtension(filePath)))
            {
                T2.IMediaFile? media = null;
                try
                {
                    media = OpenMediaWithDffRepair(filePath);
                    var tag = media?.Tag;
                    var pic = tag?.Pictures?.FirstOrDefault(p => p.PictureData.Length > 0);
                    if (pic != null)
                        return new CoverArt
                        {
                            ImageData = pic.PictureData.Span.ToArray(),
                            MimeType = pic.MimeType
                        };
                    return null;
                }
                catch
                {
                    // 回退 TagLib
                }
                finally
                {
                    (media as IDisposable)?.Dispose();
                }
            }

            try
            {
                using var file = TagLib.File.Create(filePath);
                var pic = file.Tag.Pictures.FirstOrDefault();
                if (pic == null) return null;

                return new CoverArt
                {
                    ImageData = pic.Data.Data,
                    MimeType = pic.MimeType
                };
            }
            catch (Exception)
            {
                return null;
            }
        });
    }

    public Task<bool> WriteLyricsAsync(string filePath, string lyrics)
        => RunTagWriteWithRetryAsync(filePath, () =>
        {
            // DFF：原生 USLT 帧写入（保留其余帧，原子替换容器）
            if (filePath.EndsWith(".dff", StringComparison.OrdinalIgnoreCase))
            {
                if (Utils.DffId3TagIO.TryWriteLyrics(filePath, lyrics, out var dffLyricsError))
                    return true;
                _logger.Warn($"DFF 原生歌词写入失败（回退 TagLibSharp2）：{filePath}，原因：{dffLyricsError}");
            }

            if (!IsVideo(filePath) && TagLibSharp2Engine.IsSupported(Path.GetExtension(filePath)))
            {
                T2.IMediaFile? media = null;
                try
                {
                    media = OpenMediaWithDffRepair(filePath);
                    var tag = media != null ? TagLibSharp2Engine.EnsureTag(media) : null;
                    if (tag != null)
                    {
                        tag.Lyrics = lyrics;
                        if (TagLibSharp2Engine.Save(media!, filePath, out _))
                            return true;
                    }
                }
                catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException))
                {
                    // 解析/格式类异常：回退 TagLib
                }
                finally
                {
                    (media as IDisposable)?.Dispose();
                }
            }

            try
            {
                using var file = TagLib.File.Create(filePath);
                file.Tag.Lyrics = lyrics;
                file.Save();
                return true;
            }
            catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException))
            {
                _logger.Warn($"[歌词写入] 写入失败（非占用异常）：{filePath}，原因：{ex.Message}");
                return false;
            }
        });

    /// <summary>
    /// 标签写入统一外壳：文件被播放器/杀毒软件短暂占用时按退避重试 3 次；
    /// 文件为只读时临时去除属性，写完（或最终失败）后恢复。
    /// 写入函数遇到 IOException/UnauthorizedAccessException 应直接抛出以便重试，
    /// 其它解析类失败返回 false（不重试）。
    /// </summary>
    private async Task<bool> RunTagWriteWithRetryAsync(string filePath, Func<bool> write)
    {
        return await Task.Run(async () =>
        {
            var clearedReadOnly = false;
            Exception? lastError = null;
            try
            {
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        if (System.IO.File.Exists(filePath))
                        {
                            var attrs = System.IO.File.GetAttributes(filePath);
                            if ((attrs & FileAttributes.ReadOnly) != 0)
                            {
                                System.IO.File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
                                clearedReadOnly = true;
                            }
                        }
                        return write();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        lastError = ex;
                        if (attempt < 3)
                            await Task.Delay(150 * attempt).ConfigureAwait(false);
                    }
                }

                _logger.Error(lastError!, "[标签写入] 文件被占用或无权限，重试后仍失败：{0}", filePath);
                return false;
            }
            finally
            {
                if (clearedReadOnly && System.IO.File.Exists(filePath))
                {
                    try { System.IO.File.SetAttributes(filePath,
                        System.IO.File.GetAttributes(filePath) | FileAttributes.ReadOnly); }
                    catch { /* 只读属性恢复失败不影响写入结果 */ }
                }
            }
        });
    }

    /// <summary>从 MediaInfo 输出提取歌词文本</summary>
    public Task<string?> ReadLyricsAsync(string filePath)
    {
        return Task.Run(() =>
        {
            // 音频：TagLibSharp2 直读
            if (!IsVideo(filePath) && TagLibSharp2Engine.IsSupported(Path.GetExtension(filePath)))
            {
                T2.IMediaFile? media = null;
                try
                {
                    media = OpenMediaWithDffRepair(filePath);
                    var lyrics = media?.Tag?.Lyrics;
                    if (!string.IsNullOrEmpty(lyrics)) return lyrics;
                }
                catch
                {
                    // 落到回退
                }
                finally
                {
                    (media as IDisposable)?.Dispose();
                }
                return TagLibReadLyrics(filePath);
            }

#if NET6_0_OR_GREATER || NETFRAMEWORK
            // 视频：MediaInfo 文本里找 Lyrics 行，找不到再回退 TagLib
            try
            {
                var mi = new MediaInfoWrapper(filePath);
                if (mi.Success && !string.IsNullOrEmpty(mi.Text))
                {
                    var lines = mi.Text.Split('\n');
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("Lyrics", StringComparison.OrdinalIgnoreCase)
                            || trimmed.StartsWith("LYRICS", StringComparison.Ordinal))
                        {
                            var idx = trimmed.IndexOf(':');
                            if (idx > 0)
                                return trimmed.Substring(idx + 1).TrimStart();
                        }
                    }
                }
                return TagLibReadLyrics(filePath);
            }
            catch
            {
                return null;
            }
#else
            return TagLibReadLyrics(filePath);
#endif
        });
    }

    // ============================================================
    // TagLib(Sharp 2.3.0) 旧路径：视频 / WAV / 引擎失败兜底
    // ============================================================

    /// <summary>TagLib 写入主路径（含 M4A/MP4 畸形容器修复重试）。</summary>
    private bool WriteTagsViaLegacy(string filePath, TagData tags, bool keepUpdateTime)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var lastWrite = fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.MinValue;

            using var file = TagLib.File.Create(filePath);
            ApplyTagData(file, tags);
            file.Save();

            if (tags.Lyricist != null)
                WriteLegacyLyricistQuiet(filePath, tags.Lyricist);

            if (keepUpdateTime && lastWrite != DateTime.MinValue)
                System.IO.File.SetLastWriteTime(filePath, lastWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件被占用/无权限：交由 RunTagWriteWithRetryAsync 退避重试
            _logger.Warn($"标签写入遇到文件占用/权限问题，准备重试：{filePath}，原因：{ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "TagService.WriteTagsViaLegacy 失败: {0}", filePath);

            // TagLib 无法解析该文件（如畸形 M4A）：修复标签区后重试
            if (filePath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                return WriteM4aViaFixer(filePath, tags, keepUpdateTime);
            }
            return false;
        }
    }

    private void WriteLegacyLyricistQuiet(string filePath, string lyricist)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            WriteLyricist(file, lyricist);
            file.Save();
        }
        catch (Exception ex)
        {
            _logger.Warn($"作词者补写失败（不影响其他标签）：{filePath}，原因：{ex.Message}");
        }
    }

    /// <summary>对 TagLib 无法解析的 M4A 文件：修复 ilst box，再用 TagLib 写入</summary>
    private bool WriteM4aViaFixer(string filePath, TagData tags, bool keepUpdateTime)
    {
        try
        {
            // 1. 生成标准 ilst 替换文件中的畸形标签区
            if (!Utils.M4aTagFixer.TryFix(filePath, tags))
                return false;

            // 2. 现在 TagLib 应能正常打开
            var fileInfo = new FileInfo(filePath);
            var lastWrite = fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.MinValue;

            using var file = TagLib.File.Create(filePath);
            ApplyTagData(file, tags);
            file.Save();

            if (keepUpdateTime && lastWrite != DateTime.MinValue)
                System.IO.File.SetLastWriteTime(filePath, lastWrite);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "WriteM4aViaFixer 也失败: {0}", filePath);
            return false;
        }
    }

    /// <summary>TagLib 读取标签（TagLibSharp2/MediaInfo 无法解析时的回退路径）</summary>
    private static TagData ReadTagsViaTagLib(string filePath, bool summaryOnly = false)
    {
        try
        {
            // 轻量扫描用 PictureLazy：封面字节不随解析进入内存（数量/类型照取）
            using var file = summaryOnly
                ? TagLib.File.Create(filePath, null,
                    TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy)
                : TagLib.File.Create(filePath);
            // 轻量扫描直接走精简映射，避免先拷贝整张封面字节/歌词正文再丢弃（扫描期主要内存/IO 浪费点）
            return summaryOnly ? MapToSummaryData(file) : MapToTagData(file);
        }
        catch
        {
            return new TagData();
        }
    }

    /// <summary>
    /// 目录扫描专用的精简 TagLib 映射：文本/音频属性照常读取，
    /// 封面仅计数（不复制字节）、歌词仅记有无（不持有正文）、作词者跳过（选中单文件时全量补读）。
    /// </summary>
    private static TagData MapToSummaryData(TagLib.File file)
    {
        var tag = file.Tag;
        var props = file.Properties;
        var pictures = tag.Pictures;
        var picCount = pictures?.Length ?? 0;
        var firstPic = picCount > 0 ? pictures![0] : null;

        return new TagData
        {
            Title = NullIfEmpty(tag.Title),
            Artist = NullIfEmpty(tag.FirstPerformer),
            Album = NullIfEmpty(tag.Album),
            Year = tag.Year,
            Track = tag.Track,
            TrackCount = tag.TrackCount,
            Genre = NullIfEmpty(tag.FirstGenre),
            Comment = NullIfEmpty(tag.Comment),
            AlbumArtist = NullIfEmpty(tag.FirstAlbumArtist),
            Composer = NullIfEmpty(tag.FirstComposer),
            Disc = tag.Disc,
            DiscCount = tag.DiscCount,
            LyricsPresent = !string.IsNullOrEmpty(tag.Lyrics),
            PictureCount = picCount,
            CoverArtMimeType = firstPic?.MimeType,
            CoverArtType = PicTypeToName(firstPic?.Type),
            DurationMs = (long?)props?.Duration.TotalMilliseconds,
            BitRate = props?.AudioBitrate,
            SampleRate = props?.AudioSampleRate,
            Channels = props?.AudioChannels,
            BitsPerSample = props?.BitsPerSample > 0 ? props.BitsPerSample : null,
            TagFormat = DescribeLegacyTagTypes(file),
            IsSummary = true,
        };
    }

    /// <summary>TagLib 读取歌词（回退路径）</summary>
    private static string? TagLibReadLyrics(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            return string.IsNullOrEmpty(file.Tag.Lyrics) ? null : file.Tag.Lyrics;
        }
        catch
        {
            return null;
        }
    }

    #region TagLib 映射/专用字段

    private static TagData MapToTagData(TagLib.File file)
    {
        var tag = file.Tag;
        var props = file.Properties;

        // 读取所有图片
        List<CoverArt>? allPics = null;
        if (tag.Pictures != null && tag.Pictures.Length > 0)
        {
            allPics = new List<CoverArt>(tag.Pictures.Length);
            foreach (var pic in tag.Pictures)
            {
                if (pic.Data != null && pic.Data.Count > 0)
                {
                    allPics.Add(new CoverArt
                    {
                        ImageData = pic.Data.Data,
                        MimeType = pic.MimeType,
                        PictureType = FromLegacyType(pic.Type),
                    });
                }
            }
        }

        var firstPic = tag.Pictures?.FirstOrDefault();
        return new TagData
        {
            Title = NullIfEmpty(tag.Title),
            Artist = NullIfEmpty(tag.FirstPerformer),
            Album = NullIfEmpty(tag.Album),
            Year = tag.Year,
            Track = tag.Track,
            TrackCount = tag.TrackCount,
            Genre = NullIfEmpty(tag.FirstGenre),
            Comment = NullIfEmpty(tag.Comment),
            AlbumArtist = NullIfEmpty(tag.FirstAlbumArtist),
            Composer = NullIfEmpty(tag.FirstComposer),
            Lyricist = ReadLyricist(file),
            Disc = tag.Disc,
            DiscCount = tag.DiscCount,
            Lyrics = NullIfEmpty(tag.Lyrics),
            // 首图复用 AllPics[0] 的同一份字节数组，避免同一张封面在内存中保留两份拷贝
            CoverArtMimeType = allPics?.FirstOrDefault()?.MimeType,
            CoverArtData = allPics?.FirstOrDefault()?.ImageData,
            CoverArtType = PicTypeToName(firstPic?.Type),
            AllPictures = allPics,
            PictureCount = allPics?.Count ?? 0,
            DurationMs = (long?)props?.Duration.TotalMilliseconds,
            BitRate = props?.AudioBitrate,
            SampleRate = props?.AudioSampleRate,
            Channels = props?.AudioChannels,
            BitsPerSample = props?.BitsPerSample > 0 ? props.BitsPerSample : null,
            TagFormat = DescribeLegacyTagTypes(file),
        };
    }

    private static string DescribeLegacyTagTypes(TagLib.File f)
    {
        var tags = f.TagTypes;
        var parts = new List<string>();
        if ((tags & TagLib.TagTypes.Id3v2) != 0)
        {
            if (f.Tag is TagLib.Id3v2.Tag id3v2)
                parts.Add($"ID3v2.{id3v2.Version}");
            else
                parts.Add("ID3v2");
        }
        if ((tags & TagLib.TagTypes.Id3v1) != 0) parts.Add("ID3v1");
        if ((tags & TagLib.TagTypes.Ape) != 0) parts.Add("APE");
        if ((tags & TagLib.TagTypes.FlacMetadata) != 0) parts.Add("FLAC");
        if ((tags & TagLib.TagTypes.Xiph) != 0) parts.Add("Vorbis");
        if ((tags & TagLib.TagTypes.Apple) != 0) parts.Add("MP4");
        if ((tags & TagLib.TagTypes.Asf) != 0) parts.Add("WMA");
        if ((tags & TagLib.TagTypes.RiffInfo) != 0) parts.Add("RIFF");
        if ((tags & TagLib.TagTypes.MovieId) != 0) parts.Add("Movie");
        return parts.Count > 0 ? string.Join(" + ", parts) : f.Properties?.Description ?? "";
    }

    private static void ApplyTagData(TagLib.File file, TagData tags)
    {
        var tag = file.Tag;
        if (tags.Title != null) tag.Title = tags.Title;
        if (tags.Artist != null) tag.Performers = new[] { tags.Artist };
        if (tags.Album != null) tag.Album = tags.Album;
        if (tags.Year.HasValue) tag.Year = tags.Year.Value;
        if (tags.Track.HasValue) tag.Track = tags.Track.Value;
        if (tags.TrackCount.HasValue) tag.TrackCount = tags.TrackCount.Value;
        if (tags.Genre != null) tag.Genres = new[] { tags.Genre };
        if (tags.Comment != null) tag.Comment = tags.Comment;
        if (tags.AlbumArtist != null) tag.AlbumArtists = new[] { tags.AlbumArtist };
        if (tags.Composer != null) tag.Composers = new[] { tags.Composer };
        if (tags.Lyricist != null) WriteLyricist(file, tags.Lyricist);
        if (tags.Disc.HasValue) tag.Disc = tags.Disc.Value;
        if (tags.DiscCount.HasValue) tag.DiscCount = tags.DiscCount.Value;
        if (tags.Lyrics != null) tag.Lyrics = tags.Lyrics;

        if (tags.AllPictures != null && tags.AllPictures.Count > 0)
        {
            tag.Pictures = tags.AllPictures
                .Where(p => p.HasImage)
                .Select(p => (IPicture)new TagLib.Picture(new ByteVector(p.ImageData))
                {
                    MimeType = p.MimeType ?? "image/jpeg",
                    Type = CoverToLegacyType(p.PictureType)
                })
                .ToArray();
        }
        else if (tags.CoverArtData != null)
        {
            tag.Pictures = new IPicture[]
            {
                new TagLib.Picture(new ByteVector(tags.CoverArtData))
                {
                    MimeType = tags.CoverArtMimeType ?? "image/jpeg",
                    Type = PictureType.FrontCover
                }
            };
        }
    }

    private static string? ReadLyricist(TagLib.File file)
    {
        // ID3v2 (MP3, some FLAC/APE) — TEXT 帧
        if ((file.TagTypes & TagLib.TagTypes.Id3v2) != 0)
        {
            var id3v2 = file.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag;
            if (id3v2 != null)
            {
                var frames = id3v2.GetFrames<TextInformationFrame>("TEXT");
                var text = frames.FirstOrDefault()?.Text;
                if (text != null && text.Length > 0 && !string.IsNullOrEmpty(text[0]))
                    return text[0];
            }
        }

        // Xiph Comment (OGG, FLAC, Opus) — LYRICIST 字段
        if (file.Tag is TagLib.Ogg.XiphComment xiph)
        {
            var fields = xiph.GetField("LYRICIST");
            if (fields.Length > 0 && !string.IsNullOrEmpty(fields[0]))
                return fields[0];
        }

        // APE tag（APE/MPC/WavPack）— Lyricist item
        if (file.Tag is TagLib.Ape.Tag ape)
        {
            var item = ape.GetItem("Lyricist");
            if (item != null && !string.IsNullOrEmpty(item.ToString()))
                return item.ToString();
        }

        // ASF/WMA — WM/Lyricist 描述符
        if (file.Tag is TagLib.Asf.Tag asf)
        {
            var descriptor = asf.ExtendedContentDescriptionObject
                .GetDescriptors(new[] { "WM/Lyricist" }).FirstOrDefault();
            if (descriptor != null)
            {
                var val = descriptor.ToString();
                if (!string.IsNullOrEmpty(val))
                    return val;
            }
        }

        return null;
    }

    private static void WriteLyricist(TagLib.File file, string lyricist)
    {
        if (string.IsNullOrEmpty(lyricist)) return;

        // ID3v2 (MP3, some FLAC/APE) — TEXT 帧
        if ((file.TagTypes & TagLib.TagTypes.Id3v2) != 0)
        {
            var id3v2 = file.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag;
            if (id3v2 != null)
            {
                var frame = TextInformationFrame.Get(id3v2, "TEXT", true);
                frame.Text = new[] { lyricist };
                return;
            }
        }

        // Xiph Comment (OGG, FLAC, Opus) — LYRICIST 字段
        if (file.Tag is TagLib.Ogg.XiphComment xiph)
        {
            xiph.SetField("LYRICIST", new[] { lyricist });
            return;
        }

        // APE tag（APE/MPC/WavPack）— Lyricist item
        if (file.Tag is TagLib.Ape.Tag ape)
        {
            ape.SetValue("Lyricist", lyricist);
            return;
        }

        // ASF/WMA — WM/Lyricist 描述符
        if (file.Tag is TagLib.Asf.Tag asf)
        {
            asf.ExtendedContentDescriptionObject.SetDescriptors("WM/Lyricist",
                new[] { new TagLib.Asf.ContentDescriptor("WM/Lyricist", lyricist) });
            return;
        }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;

    // —— 封面类型转换：CoverPictureType 与 TagLib/TagLibSharp2 的 PictureType 名称一致但数值不同，
    //    必须按名称映射，不能做 (int) 强转（否则非 FrontCover 类型全部错位）。 ——

    private static CoverPictureType? FromT2Type(T2.PictureType type) =>
        Enum.TryParse<CoverPictureType>(type.ToString(), out var v) ? v : CoverPictureType.Other;

    private static T2.PictureType CoverToT2Type(CoverPictureType? type) =>
        type.HasValue && Enum.TryParse<T2.PictureType>(type.Value.ToString(), out var v)
            ? v : T2.PictureType.FrontCover;

    private static CoverPictureType? FromLegacyType(TagLib.PictureType type) =>
        Enum.TryParse<CoverPictureType>(type.ToString(), out var v) ? v : CoverPictureType.Other;

    private static TagLib.PictureType CoverToLegacyType(CoverPictureType? type) =>
        type.HasValue && Enum.TryParse<TagLib.PictureType>(type.Value.ToString(), out var v)
            ? v : TagLib.PictureType.FrontCover;

    private static TagLib.PictureType T2ToLegacyType(T2.PictureType type) =>
        Enum.TryParse<TagLib.PictureType>(type.ToString(), out var v)
            ? v : TagLib.PictureType.Other;

    private static string? PicTypeToName(TagLib.PictureType? type)
    {
        if (type == null) return null;
        return type switch
        {
            TagLib.PictureType.FrontCover => "封面",
            TagLib.PictureType.BackCover => "封底",
            TagLib.PictureType.LeafletPage => "插页",
            TagLib.PictureType.Media => "介质",
            TagLib.PictureType.LeadArtist => "主要艺术家",
            TagLib.PictureType.Artist => "艺术家",
            TagLib.PictureType.Conductor => "指挥",
            TagLib.PictureType.Band => "乐队",
            TagLib.PictureType.Composer => "作曲家",
            TagLib.PictureType.Lyricist => "作词家",
            TagLib.PictureType.RecordingLocation => "录制地点",
            TagLib.PictureType.DuringRecording => "录制中",
            TagLib.PictureType.DuringPerformance => "表演中",
            TagLib.PictureType.MovieScreenCapture => "电影截图",
            TagLib.PictureType.Illustration => "插图",
            TagLib.PictureType.BandLogo => "乐队标志",
            TagLib.PictureType.PublisherLogo => "出版商标志",
            TagLib.PictureType.FileIcon => "文件图标",
            TagLib.PictureType.OtherFileIcon => "其他文件图标",
            TagLib.PictureType.ColoredFish => "彩色鱼",
            TagLib.PictureType.NotAPicture => "非图片",
            _ => "其他"
        };
    }

    #endregion
}
