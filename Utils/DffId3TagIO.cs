using System.Text;
using MusicTagRecover.Models;

namespace MusicTagRecover.Utils;

/// <summary>DSDIFF 原生 DIIN 子 chunk 文本字段（如 DITI 标题 / DIAR 艺术家）。</summary>
public sealed class DffDiinField
{
    /// <summary>4 字节子 chunk ID（如 DITI、DIAR、DGEN、DINA、DARE）。</summary>
    public string Id { get; set; } = "";
    /// <summary>文本内容（DIIN 子 chunk 数据 = uint32 长度 + UTF-8/Latin-1 文本）。</summary>
    public string Text { get; set; } = "";
}

/// <summary>DSDIFF 原生 COMT chunk 中的一条注释。</summary>
public sealed class DffCommtComment
{
    public ushort Year { get; set; }
    public byte Month { get; set; }
    public byte Day { get; set; }
    public byte Hour { get; set; }
    public byte Minute { get; set; }
    public byte Second { get; set; }
    /// <summary>注释类型（0=general…）。</summary>
    public byte CommentType { get; set; }
    /// <summary>注释引用编号。</summary>
    public ushort CommentReference { get; set; }
    /// <summary>注释正文字节长度。</summary>
    public int Count { get; set; }
    /// <summary>注释正文（UTF-8 编码，兼容中文）。</summary>
    public string Text { get; set; } = "";
}

/// <summary>
/// DFF（DSDIFF/DSD）文件 ID3v2 标签的定向读写（不依赖 TagLibSharp2 全量解析）。
/// <para>
/// 背景：TagLibSharp2 的 DffFile 读取会完整物化整个 ID3 chunk（含 APIC 封面字节，
/// 常见几百 KB ~ 数 MB）并构建完整帧对象图，目录扫描时造成 LOH 内存激增与 IO 放大；
/// 而 MediaInfo 对 DFF 映射的是原生 DIIN/COMT chunk（抓轨残留），与 ID3v2 不一致。
/// 本模块只做定向访问：
/// <list type="bullet">
/// <item>读：顺序流式遍历 chunk 定位 "ID3 " chunk，逐帧解析；文本帧（T***/COMM/USLT）
/// 只读头部+小载荷，APIC 封面只取 MIME/类型/张数，字节数据用 Seek 跳过，不进内存。</item>
/// <item>属性：直接解析 PROP 子 chunk（FS 采样率 / CHNL 声道）+ DSD 块字节数推导时长与码率，
/// 不依赖 MediaInfo/TagLib。</item>
/// <item>写/删：重建 ID3v2.4 帧序列（保留未修改的 APIC/USLT 等原有帧），
/// 同卷临时文件 + File.Replace 原子替换，全程不重编码、不触碰 DSD 音频块字节。</item>
/// </list>
/// </para>
/// </summary>
public static class DffId3TagIO
{
    private const int ChunkHeaderSize = 12; // 4 字节 ID + 8 字节 BE 长度
    private const long MaxId3ChunkBytes = 64L << 20; // 单个 ID3 chunk 上限 64MB（异常防御）

    private sealed class ChunkInfo
    {
        public string Id = "";
        public long HeaderOffset;  // chunk 头起始
        public long DataOffset;    // 数据区起始
        public long DataLength;    // 数据区字节数（不含 pad）
        public long TotalLength => ChunkHeaderSize + DataLength + (DataLength & 1);
    }

    private sealed class DffLayout
    {
        public bool Valid;
        public string? Error;
        public string FormType = "";
        public List<ChunkInfo> Chunks = new();
        public ChunkInfo? Prop;
        public ChunkInfo? Dsd;
        public ChunkInfo? Id3;
        public ChunkInfo? Diin;
        public ChunkInfo? Comt;
    }

    // ============================================================
    // 读取
    // ============================================================

    /// <summary>
    /// 定向读取 DFF 标签。返回 null 表示容器结构无法解析（调用方走既有回退链）。
    /// ID3 chunk 缺失时返回只有音频属性的空标签结果（属正常状态，不算失败）。
    /// </summary>
    /// <param name="full">false=扫描摘要（不读封面字节/歌词正文）；true=选中全量</param>
    public static TagData? TryRead(string filePath, bool full)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.SequentialScan);
            var layout = ParseLayout(fs);
            if (!layout.Valid) return null;

            var data = new TagData { IsSummary = !full };
            ReadAudioProperties(fs, layout, data);

            if (layout.Id3 != null)
                ReadId3Frames(fs, layout.Id3, data, full);
            else
                data.TagFormat = "无 ID3 标签";

            return data;
        }
        catch
        {
            return null;
        }
        finally
        {
            fs?.Dispose();
        }
    }

    /// <summary>解析顶层 chunk 布局；畸形（非可打印 ID / 长度越界）时 Valid=false。</summary>
    private static DffLayout ParseLayout(FileStream fs)
    {
        var layout = new DffLayout();
        long fileLength = fs.Length;
        if (fileLength < 16) { layout.Error = "文件过小"; return layout; }

        Span<byte> head = stackalloc byte[16];
        fs.Position = 0;
        if (fs.Read(head) != 16 || !head[..4].SequenceEqual("FRM8"u8)) { layout.Error = "非 FRM8"; return layout; }
        layout.FormType = Encoding.ASCII.GetString(head.Slice(12, 4));

        Span<byte> hdr = stackalloc byte[ChunkHeaderSize];
        long pos = 16;
        while (pos + ChunkHeaderSize <= fileLength)
        {
            fs.Position = pos;
            if (fs.Read(hdr) != ChunkHeaderSize) { layout.Error = "chunk 头读取失败"; return layout; }
            if (!IsPrintableId(hdr[..4])) { layout.Error = $"畸形 chunk @{pos}"; return layout; }

            ulong size = ReadBe64(hdr.Slice(4, 8));
            long dataLen = (long)size;
            long total = ChunkHeaderSize + dataLen + (dataLen & 1);
            if (size > (ulong)fileLength || pos + total > fileLength || total < 0)
            { layout.Error = $"chunk 长度越界 @{pos}"; return layout; }

            var chunk = new ChunkInfo
            {
                Id = Encoding.ASCII.GetString(hdr[..4]),
                HeaderOffset = pos,
                DataOffset = pos + ChunkHeaderSize,
                DataLength = dataLen,
            };
            layout.Chunks.Add(chunk);
            if (chunk.Id == "PROP") layout.Prop = chunk;
            else if (chunk.Id is "DSD " or "DST ") layout.Dsd ??= chunk;
            else if (chunk.Id == "ID3 ") layout.Id3 ??= chunk; // 重复 ID3 chunk 取第一个（DffTagFixer 负责清理）
            else if (chunk.Id == "DIIN") layout.Diin ??= chunk;
            else if (chunk.Id == "COMT") layout.Comt ??= chunk;

            pos += total;
        }

        layout.Valid = true;
        return layout;
    }

    /// <summary>从 PROP(FS/CHNL) 与 DSD 块推导音频属性，全程只读几十字节。</summary>
    private static void ReadAudioProperties(FileStream fs, DffLayout layout, TagData data)
    {
        long sampleRate = 0;
        int channels = 0;

        if (layout.Prop != null && layout.Prop.DataLength is >= 4 and < 1 << 20)
        {
            // PROP 数据区 = 4 字节 form("SND ") + 子 chunk 序列
            byte[] prop = new byte[layout.Prop.DataLength];
            fs.Position = layout.Prop.DataOffset;
            if (ReadExactly(fs, prop, 0, prop.Length))
            {
                int p = 4;
                while (p + ChunkHeaderSize <= prop.Length)
                {
                    string cid = Encoding.ASCII.GetString(prop, p, 4);
                    ulong csize = ReadBe64(prop.AsSpan(p + 4, 8));
                    int dp = p + ChunkHeaderSize;
                    if (cid == "FS  " && csize >= 4 && dp + 4 <= prop.Length)
                        sampleRate = (long)((prop[dp] << 24) | (prop[dp + 1] << 16) | (prop[dp + 2] << 8) | prop[dp + 3]);
                    else if (cid == "CHNL" && csize >= 2 && dp + 2 <= prop.Length)
                        channels = (prop[dp] << 8) | prop[dp + 1];
                    long next = dp + (long)csize + ((long)csize & 1);
                    if (next <= p) break;
                    p = (int)next;
                }
            }
        }

        if (sampleRate > 0) data.SampleRate = (int)sampleRate;
        if (channels > 0) data.Channels = channels;
        data.BitsPerSample = 1; // DSD 固定 1bit

        if (layout.Dsd != null && sampleRate > 0 && channels > 0)
        {
            long dsdBytes = layout.Dsd.DataLength;
            // DSD 1bit/采样：采样帧数 = dsdBytes * 8 / channels（交织，每帧 channels bit）；
            // 时长(ms) = 帧数 / 采样率 * 1000 = dsdBytes * 8000 / (sampleRate * channels)
            data.DurationMs = dsdBytes * 8000 / (sampleRate * channels);
            // DSD 1bit：码率 = 采样率 × 声道数（bps → kbps）
            data.BitRate = (int)(sampleRate * channels / 1000);
        }
    }

    /// <summary>逐帧解析 ID3v2；文本帧读小载荷，APIC 只取元数据（full 时才复制字节）。</summary>
    private static void ReadId3Frames(FileStream fs, ChunkInfo id3, TagData data, bool full)
    {
        if (id3.DataLength < 10 || id3.DataLength > MaxId3ChunkBytes) return;

        Span<byte> header = stackalloc byte[10];
        fs.Position = id3.DataOffset;
        if (fs.Read(header) != 10) return;
        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return;
        byte ver = header[3];
        if (ver is not (2 or 3 or 4)) return;

        int tagBodySize = ReadSyncSafe(header.Slice(6, 4));
        long bodyEnd = Math.Min(id3.DataOffset + 10 + tagBodySize, id3.DataOffset + id3.DataLength);

        int frameHeaderSize = ver == 2 ? 6 : 10;
        var covers = new List<CoverArt>();
        int picCount = 0;
        string? firstMime = null, firstType = null;
        bool lyricsPresent = false;

        long pos = id3.DataOffset + 10;
        Span<byte> fh = stackalloc byte[10];
        while (pos + frameHeaderSize <= bodyEnd)
        {
            fs.Position = pos;
            if (fs.Read(fh[..frameHeaderSize]) != frameHeaderSize) break;
            if (fh[0] == 0) break; // padding

            int idLen = ver == 2 ? 3 : 4;
            string fid = Encoding.ASCII.GetString(fh[..idLen]);
            long frameSize = ver switch
            {
                2 => (fh[3] << 16) | (fh[4] << 8) | fh[5],
                4 => ReadSyncSafe(fh.Slice(4, 4)),
                _ => ((long)fh[4] << 24) | ((long)fh[5] << 16) | ((long)fh[6] << 8) | fh[7],
            };
            long payloadOffset = pos + frameHeaderSize;
            if (frameSize < 0 || payloadOffset + frameSize > bodyEnd) break;

            switch (fid)
            {
                case "TIT2" or "TT2": data.Title = ReadTextFrame(fs, payloadOffset, frameSize); break;
                case "TPE1" or "TP1": data.Artist = ReadTextFrame(fs, payloadOffset, frameSize); break;
                case "TALB" or "TAL": data.Album = ReadTextFrame(fs, payloadOffset, frameSize); break;
                case "TPE2" or "TP2": data.AlbumArtist = ReadTextFrame(fs, payloadOffset, frameSize); break;
                case "TCON" or "TCO": data.Genre = ReadTextFrame(fs, payloadOffset, frameSize); break;
                case "TCOM" or "TCM": data.Composer = ReadTextFrame(fs, payloadOffset, frameSize); break;
                case "TEXT" or "TXT": data.Lyricist = ReadTextFrame(fs, payloadOffset, frameSize); break;
                case "TDRC" or "TYER" or "TYE":
                    data.Year = ParseYear(ReadTextFrame(fs, payloadOffset, frameSize)); break;
                case "TRCK" or "TRK":
                    (data.Track, data.TrackCount) = SplitPair(ReadTextFrame(fs, payloadOffset, frameSize)); break;
                case "TPOS" or "TPA":
                    (data.Disc, data.DiscCount) = SplitPair(ReadTextFrame(fs, payloadOffset, frameSize)); break;
                case "COMM" or "COM":
                    data.Comment ??= ReadCommentFrame(fs, payloadOffset, frameSize); break;
                case "USLT" or "ULT":
                    lyricsPresent = true;
                    if (full) data.Lyrics ??= ReadCommentFrame(fs, payloadOffset, frameSize);
                    break;
                case "APIC" or "PIC":
                    ReadPicFrame(fs, payloadOffset, frameSize, ver, full, covers,
                        ref picCount, ref firstMime, ref firstType);
                    break;
            }

            pos = payloadOffset + frameSize;
        }

        data.LyricsPresent = lyricsPresent || !string.IsNullOrEmpty(data.Lyrics);
        data.PictureCount = picCount;
        data.CoverArtMimeType = firstMime;
        data.CoverArtType = firstType;
        if (full && covers.Count > 0)
        {
            data.AllPictures = covers;
            data.CoverArtData = covers[0].ImageData;
        }
        data.TagFormat = ver == 2 ? "ID3v2.2" : ver == 3 ? "ID3v2.3" : "ID3v2";
    }

    /// <summary>读文本帧：编码字节 + 文本（上限 1MB 防御）。</summary>
    private static string? ReadTextFrame(FileStream fs, long offset, long size)
    {
        if (size <= 1 || size > 1 << 20) return null;
        byte[] buf = new byte[size];
        fs.Position = offset;
        if (!ReadExactly(fs, buf, 0, buf.Length)) return null;
        return DecodeText(buf, 0, buf.Length);
    }

    /// <summary>COMM/USLT：编码 + 3 字节语言 + 短描述（空）+ 正文。</summary>
    private static string? ReadCommentFrame(FileStream fs, long offset, long size)
    {
        if (size <= 4 || size > 4 << 20) return null;
        byte[] buf = new byte[size];
        fs.Position = offset;
        if (!ReadExactly(fs, buf, 0, buf.Length)) return null;

        byte enc = buf[0];
        int p = 4; // 编码1 + 语言3
        // 跳过短描述（编码相关的终止符）
        p = SkipTerminated(buf, p, enc);
        if (p >= buf.Length) return null;
        return DecodeText(buf, p, buf.Length - p, buf[0]);
    }

    /// <summary>APIC/PIC：解析 MIME/类型；full 时才复制图片字节。</summary>
    private static void ReadPicFrame(FileStream fs, long offset, long size, byte ver, bool full,
        List<CoverArt> covers, ref int picCount, ref string? firstMime, ref string? firstType)
    {
        if (size < 4 || size > MaxId3ChunkBytes) return;

        // 头部探测：编码1 + MIME(Latin1 串, v2.2 为 3 字节定长) + 类型1
        int probeLen = (int)Math.Min(size, 260);
        byte[] probe = new byte[probeLen];
        fs.Position = offset;
        if (!ReadExactly(fs, probe, 0, probeLen)) return;

        byte enc = probe[0];
        int p = 1;
        string mime;
        if (ver == 2)
        {
            if (p + 3 > probeLen) return;
            mime = Encoding.Latin1.GetString(probe, p, 3).Trim();
            mime = mime switch { "PNG" => "image/png", "JPG" => "image/jpeg", _ => mime };
            p += 3;
        }
        else
        {
            int end = Array.IndexOf(probe, (byte)0, p);
            if (end < 0) return;
            mime = Encoding.Latin1.GetString(probe, p, end - p);
            p = end + 1;
        }
        if (p >= probeLen) return;
        byte picType = probe[p]; p++;

        // 描述（编码相关终止符）→ 图片数据起点
        long descStart = offset + p;
        // 描述可能很长：流式定位终止符
        int descBytes = SkipDescriptionInStream(fs, descStart, offset + size, enc);
        if (descBytes < 0) return;
        long imageOffset = descStart + descBytes;
        long imageLen = offset + size - imageOffset;
        if (imageLen <= 0) return;

        picCount++;
        firstMime ??= string.IsNullOrEmpty(mime) ? "image/jpeg" : mime;
        firstType ??= PicTypeName(picType);

        if (full)
        {
            if (imageLen > MaxId3ChunkBytes) return;
            byte[] img = new byte[imageLen];
            fs.Position = imageOffset;
            if (!ReadExactly(fs, img, 0, img.Length)) return;
            covers.Add(new CoverArt
            {
                ImageData = img,
                MimeType = string.IsNullOrEmpty(mime) ? "image/jpeg" : mime,
                PictureType = PicTypeToEnum(picType),
            });
        }
    }

    // ============================================================
    // 写入 / 删除
    // ============================================================

    /// <summary>
    /// 写入/更新 ID3v2.4 标签（保留未指定字段的原有帧；null=不修改，空串/0=清除）。
    /// 容器畸形时先经 DffTagFixer 修复再重试一次。
    /// </summary>
    public static bool TryWrite(string filePath, TagData tags, out string? error)
        => TryWriteWithRepair(filePath, () =>
        {
            var layout = ReadLayoutOrThrow(filePath);
            var frames = LoadExistingFrames(filePath, layout);
            ApplyTagDataToFrames(frames, tags);
            byte[] id3 = BuildId3V24(frames);
            RebuildContainer(filePath, layout, id3);
        }, out error);

    /// <summary>删除整个 ID3v2 标签块（原子重建容器，音频块不动）。</summary>
    public static bool TryDelete(string filePath, out string? error)
        => TryWriteWithRepair(filePath, () =>
        {
            var layout = ReadLayoutOrThrow(filePath);
            RebuildContainer(filePath, layout, null);
        }, out error);

    /// <summary>仅更新歌词（USLT），保留其余帧。</summary>
    public static bool TryWriteLyrics(string filePath, string lyrics, out string? error)
        => TryWriteWithRepair(filePath, () =>
        {
            var layout = ReadLayoutOrThrow(filePath);
            var frames = LoadExistingFrames(filePath, layout);
            frames.RemoveAll(f => f.Id == "USLT");
            if (!string.IsNullOrEmpty(lyrics))
                frames.Add(new Frame { Id = "USLT", Payload = BuildCommentPayload(lyrics) });
            byte[] id3 = BuildId3V24(frames);
            RebuildContainer(filePath, layout, id3);
        }, out error);

    /// <summary>仅替换封面（APIC），保留其余帧。</summary>
    public static bool TryWriteCover(string filePath, CoverArt cover, out string? error)
        => TryWriteWithRepair(filePath, () =>
        {
            var layout = ReadLayoutOrThrow(filePath);
            var frames = LoadExistingFrames(filePath, layout);
            frames.RemoveAll(f => f.Id == "APIC");
            if (cover.HasImage && cover.ImageData != null)
                frames.Add(new Frame
                {
                    Id = "APIC",
                    Payload = BuildApicPayload(cover.ImageData, cover.MimeType ?? "image/jpeg"),
                });
            byte[] id3 = BuildId3V24(frames);
            RebuildContainer(filePath, layout, id3);
        }, out error);

    // ============================================================
    // 原生 DSDIFF DIIN / COMT 字段读写
    // ============================================================

    /// <summary>读取 DIIN chunk 中的文本字段（DITI/DIAR/DGEN/DINA/DARE 等）。</summary>
    public static List<DffDiinField> ReadDiinFields(string filePath)
    {
        var result = new List<DffDiinField>();
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var layout = ParseLayout(fs);
            if (!layout.Valid || layout.Diin == null) return result;
            ReadDiinFieldsInternal(fs, layout.Diin, result);
        }
        catch { /* 返回空列表 */ }
        return result;
    }

    /// <summary>修改或新增 DIIN 子字段（fieldId 如 DITI/DIAR）；text 为空字符串表示清空（保留子 chunk 但文本置空）。</summary>
    public static bool TryWriteDiinField(string filePath, string fieldId, string text, out string? error)
        => TryWriteWithRepair(filePath, () =>
        {
            if (fieldId.Length != 4) throw new ArgumentException("DIIN 子字段 ID 必须为 4 字节", nameof(fieldId));
            var layout = ReadLayoutOrThrow(filePath);
            var fields = new List<DffDiinField>();
            if (layout.Diin != null)
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ReadDiinFieldsInternal(fs, layout.Diin, fields);

            // 替换或新增目标字段
            bool replaced = false;
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Id == fieldId) { fields[i].Text = text; replaced = true; }
            }
            if (!replaced) fields.Add(new DffDiinField { Id = fieldId, Text = text });

            byte[] diin = BuildDiinChunk(fields);
            RebuildContainerReplacingChunk(filePath, layout, "DIIN", diin);
        }, out error);

    /// <summary>删除 DIIN chunk 中指定子字段；若删完后 DIIN 为空则整体删除 DIIN chunk。</summary>
    public static bool TryDeleteDiinField(string filePath, string fieldId, out string? error)
        => TryWriteWithRepair(filePath, () =>
        {
            if (fieldId.Length != 4) throw new ArgumentException("DIIN 子字段 ID 必须为 4 字节", nameof(fieldId));
            var layout = ReadLayoutOrThrow(filePath);
            if (layout.Diin == null) return; // 无 DIIN，无需操作

            var fields = new List<DffDiinField>();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                ReadDiinFieldsInternal(fs, layout.Diin, fields);

            fields.RemoveAll(f => f.Id == fieldId);

            if (fields.Count == 0)
                RebuildContainerReplacingChunk(filePath, layout, "DIIN", null);
            else
            {
                byte[] diin = BuildDiinChunk(fields);
                RebuildContainerReplacingChunk(filePath, layout, "DIIN", diin);
            }
        }, out error);

    /// <summary>读取 COMT chunk 中的全部注释。</summary>
    public static List<DffCommtComment> ReadCommtComments(string filePath)
    {
        var result = new List<DffCommtComment>();
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var layout = ParseLayout(fs);
            if (!layout.Valid || layout.Comt == null) return result;
            ReadCommtInternal(fs, layout.Comt, result);
        }
        catch { /* 返回空列表 */ }
        return result;
    }

    /// <summary>修改指定索引的 COMT 注释（索引从 0 开始）；不存在则返回 false。</summary>
    public static bool TryWriteCommtComment(string filePath, int index, DffCommtComment comment, out string? error)
        => TryWriteWithRepair(filePath, () =>
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            var layout = ReadLayoutOrThrow(filePath);
            var comments = new List<DffCommtComment>();
            if (layout.Comt != null)
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ReadCommtInternal(fs, layout.Comt, comments);

            if (index >= comments.Count)
                throw new ArgumentOutOfRangeException(nameof(index), $"注释索引 {index} 超出范围（共 {comments.Count} 条）");
            comments[index] = comment;

            byte[] comt = BuildCommtChunk(comments);
            RebuildContainerReplacingChunk(filePath, layout, "COMT", comt);
        }, out error);

    /// <summary>追加一条 COMT 注释。</summary>
    public static bool TryAddCommtComment(string filePath, DffCommtComment comment, out string? error)
        => TryWriteWithRepair(filePath, () =>
        {
            var layout = ReadLayoutOrThrow(filePath);
            var comments = new List<DffCommtComment>();
            if (layout.Comt != null)
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ReadCommtInternal(fs, layout.Comt, comments);
            comments.Add(comment);
            byte[] comt = BuildCommtChunk(comments);
            RebuildContainerReplacingChunk(filePath, layout, "COMT", comt);
        }, out error);

    /// <summary>删除指定索引的 COMT 注释；删完后若无注释则整体删除 COMT chunk。</summary>
    public static bool TryDeleteCommtComment(string filePath, int index, out string? error)
        => TryWriteWithRepair(filePath, () =>
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            var layout = ReadLayoutOrThrow(filePath);

            var comments = new List<DffCommtComment>();
            if (layout.Comt != null)
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ReadCommtInternal(fs, layout.Comt, comments);

            // 显式索引删除必须校验越界（无 COMT/注释不足都视为索引无效），
            // 与 TryWriteCommtComment 的索引语义保持一致
            if (index >= comments.Count)
                throw new ArgumentOutOfRangeException(nameof(index), $"注释索引 {index} 超出范围（共 {comments.Count} 条）");
            comments.RemoveAt(index);

            if (comments.Count == 0)
                RebuildContainerReplacingChunk(filePath, layout, "COMT", null);
            else
            {
                byte[] comt = BuildCommtChunk(comments);
                RebuildContainerReplacingChunk(filePath, layout, "COMT", comt);
            }
        }, out error);

    private delegate void WriteAction();

    /// <summary>写操作统一外壳：畸形容器先修复重试一次；异常转 error 返回。</summary>
    private static bool TryWriteWithRepair(string filePath, WriteAction action, out string? error)
    {
        error = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (InvalidDataException) when (attempt == 0)
            {
                // 容器畸形：修复后重试（复用既有修复器，保证原子性）
                if (!DffTagFixer.TryFix(filePath, out var reason))
                {
                    error = $"容器畸形且修复失败：{reason}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        error = "未知错误";
        return false;
    }

    private static DffLayout ReadLayoutOrThrow(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var layout = ParseLayout(fs);
        if (!layout.Valid) throw new InvalidDataException(layout.Error ?? "DFF 布局解析失败");
        if (layout.Dsd == null) throw new InvalidDataException("缺少 DSD 音频块");
        return layout;
    }

    // ============================================================
    // ID3v2.4 帧模型与序列化
    // ============================================================

    private sealed class Frame
    {
        public string Id = "";
        public byte[] Payload = Array.Empty<byte>(); // 帧数据区（不含帧头）
    }

    /// <summary>把现有 ID3 chunk 中有保留价值的帧整体取出（APIC/USLT/未知帧原样保留字节）。</summary>
    private static List<Frame> LoadExistingFrames(string filePath, DffLayout layout)
    {
        var frames = new List<Frame>();
        if (layout.Id3 == null || layout.Id3.DataLength < 10 || layout.Id3.DataLength > MaxId3ChunkBytes)
            return frames;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[10];
        fs.Position = layout.Id3.DataOffset;
        if (fs.Read(header) != 10) return frames;
        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return frames;
        byte ver = header[3];
        if (ver is not (3 or 4)) return frames; // v2.2 帧不保留（升级重写为新帧）

        int bodySize = ReadSyncSafe(header.Slice(6, 4));
        long bodyEnd = Math.Min(layout.Id3.DataOffset + 10 + bodySize,
            layout.Id3.DataOffset + layout.Id3.DataLength);

        long pos = layout.Id3.DataOffset + 10;
        Span<byte> fh = stackalloc byte[10];
        while (pos + 10 <= bodyEnd)
        {
            fs.Position = pos;
            if (fs.Read(fh) != 10) break;
            if (fh[0] == 0) break;
            string fid = Encoding.ASCII.GetString(fh[..4]);
            long size = ver == 4 ? ReadSyncSafe(fh.Slice(4, 4))
                                 : ((long)fh[4] << 24) | ((long)fh[5] << 16) | ((long)fh[6] << 8) | fh[7];
            long payloadOffset = pos + 10;
            if (size < 0 || payloadOffset + size > bodyEnd || size > MaxId3ChunkBytes) break;

            byte[] payload = new byte[size];
            fs.Position = payloadOffset;
            if (!ReadExactly(fs, payload, 0, payload.Length)) break;
            frames.Add(new Frame { Id = fid, Payload = payload });
            pos = payloadOffset + size;
        }
        return frames;
    }

    /// <summary>按 TagData 更新帧集合：null=不动；空串/0=删除对应帧；值=替换。</summary>
    private static void ApplyTagDataToFrames(List<Frame> frames, TagData tags)
    {
        void SetText(string id, string? value)
        {
            if (value == null) return;
            frames.RemoveAll(f => f.Id == id);
            if (value.Length > 0)
                frames.Add(new Frame { Id = id, Payload = BuildTextPayload(value) });
        }

        void SetPair(string id, uint? pos, uint? total)
        {
            if (pos == null && total == null) return;
            frames.RemoveAll(f => f.Id == id);
            string text = (pos ?? 0) > 0
                ? ((total ?? 0) > 0 ? $"{pos}/{total}" : pos!.Value.ToString())
                : "";
            if (text.Length > 0)
                frames.Add(new Frame { Id = id, Payload = BuildTextPayload(text) });
        }

        SetText("TIT2", tags.Title);
        SetText("TPE1", tags.Artist);
        SetText("TALB", tags.Album);
        SetText("TPE2", tags.AlbumArtist);
        SetText("TCON", tags.Genre);
        SetText("TCOM", tags.Composer);
        SetText("TEXT", tags.Lyricist);
        if (tags.Year.HasValue)
            SetText("TDRC", tags.Year.Value > 0 ? tags.Year.Value.ToString() : "");
        SetPair("TRCK", tags.Track, tags.TrackCount);
        SetPair("TPOS", tags.Disc, tags.DiscCount);

        if (tags.Comment != null)
        {
            frames.RemoveAll(f => f.Id == "COMM");
            if (tags.Comment.Length > 0)
                frames.Add(new Frame { Id = "COMM", Payload = BuildCommentPayload(tags.Comment) });
        }
        if (tags.Lyrics != null)
        {
            frames.RemoveAll(f => f.Id == "USLT");
            if (tags.Lyrics.Length > 0)
                frames.Add(new Frame { Id = "USLT", Payload = BuildCommentPayload(tags.Lyrics) });
        }

        // 封面：AllPictures 优先，其次单封面；两者都为 null 表示不动封面
        if (tags.AllPictures != null)
        {
            frames.RemoveAll(f => f.Id == "APIC");
            foreach (var p in tags.AllPictures.Where(p => p.HasImage && p.ImageData != null))
                frames.Add(new Frame
                {
                    Id = "APIC",
                    Payload = BuildApicPayload(p.ImageData!, p.MimeType ?? "image/jpeg"),
                });
        }
        else if (tags.CoverArtData != null)
        {
            frames.RemoveAll(f => f.Id == "APIC");
            frames.Add(new Frame
            {
                Id = "APIC",
                Payload = BuildApicPayload(tags.CoverArtData, tags.CoverArtMimeType ?? "image/jpeg"),
            });
        }
    }

    private static byte[] BuildTextPayload(string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[1 + body.Length];
        payload[0] = 3; // UTF-8
        body.CopyTo(payload, 1);
        return payload;
    }

    /// <summary>COMM/USLT：UTF-8 + 语言"eng" + 空描述（\0）+ 正文。</summary>
    private static byte[] BuildCommentPayload(string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[5 + body.Length];
        payload[0] = 3;                    // UTF-8
        payload[1] = (byte)'e'; payload[2] = (byte)'n'; payload[3] = (byte)'g';
        payload[4] = 0;                    // 空描述终止符
        body.CopyTo(payload, 5);
        return payload;
    }

    /// <summary>APIC：UTF-8 + MIME\0 + 类型(FrontCover) + 空描述\0 + 图片字节。</summary>
    private static byte[] BuildApicPayload(byte[] image, string mime)
    {
        byte[] mimeBytes = Encoding.Latin1.GetBytes(mime);
        byte[] payload = new byte[1 + mimeBytes.Length + 1 + 1 + 1 + image.Length];
        int p = 0;
        payload[p++] = 3;
        mimeBytes.CopyTo(payload, p); p += mimeBytes.Length;
        payload[p++] = 0;
        payload[p++] = 3; // FrontCover
        payload[p++] = 0;
        image.CopyTo(payload, p);
        return payload;
    }

    /// <summary>序列化为 ID3v2.4 标签（syncsafe 帧长与总长）。</summary>
    private static byte[] BuildId3V24(List<Frame> frames)
    {
        int bodySize = frames.Sum(f => 10 + f.Payload.Length);
        byte[] tag = new byte[10 + bodySize];
        tag[0] = (byte)'I'; tag[1] = (byte)'D'; tag[2] = (byte)'3';
        tag[3] = 4; tag[4] = 0; tag[5] = 0;
        WriteSyncSafe(tag, 6, bodySize);

        int p = 10;
        foreach (var f in frames)
        {
            var idBytes = Encoding.ASCII.GetBytes(f.Id);
            idBytes.CopyTo(tag, p);
            WriteSyncSafe(tag, p + 4, f.Payload.Length);
            // flags = 0
            f.Payload.CopyTo(tag, p + 10);
            p += 10 + f.Payload.Length;
        }
        return tag;
    }

    /// <summary>
    /// 原子重建容器：保留除旧 ID3 chunk 外的一切 chunk（含 DSD 音频块原字节流式拷贝），
    /// 新 ID3 chunk 追加到末尾（偶数对齐），回填 FRM8 主长度，File.Replace 原子替换。
    /// </summary>
    private static void RebuildContainer(string filePath, DffLayout layout, byte[]? newId3)
    {
        string tempPath = filePath + ".dffwrite" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // FRM8 头（主长度占位，最后回填）
                output.Write("FRM8"u8);
                output.Write(stackalloc byte[8]);
                output.Write(Encoding.ASCII.GetBytes(layout.FormType));

                foreach (var chunk in layout.Chunks)
                {
                    if (chunk.Id == "ID3 ") continue; // 旧标签块整体替换/删除
                    input.Position = chunk.HeaderOffset;
                    CopyRange(input, output, chunk.TotalLength);
                }

                if (newId3 is { Length: > 0 })
                {
                    output.Write("ID3 "u8);
                    WriteBe64(output, (ulong)newId3.Length);
                    output.Write(newId3);
                    if ((newId3.Length & 1) != 0) output.WriteByte(0);
                }

                output.Flush(true);
                long newLength = output.Length;
                output.Position = 4;
                WriteBe64(output, (ulong)(newLength - 12));
            }

            File.Replace(tempPath, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 清理失败可忽略 */ }
        }
    }

    /// <summary>
    /// 通用容器重建：用 newChunkData（可为 null 表示删除）替换指定顶层 chunkId，
    /// 其余 chunk（含 DSD 音频块）原样流式拷贝，回填 FRM8 主长度，File.Replace 原子替换。
    /// 新 chunk 追加在原位置之后（保持 chunk 顺序稳定）。
    /// </summary>
    private static void RebuildContainerReplacingChunk(string filePath, DffLayout layout,
        string chunkId, byte[]? newChunkData)
    {
        string tempPath = filePath + ".dffnative" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write("FRM8"u8);
                output.Write(stackalloc byte[8]);
                output.Write(Encoding.ASCII.GetBytes(layout.FormType));

                bool replaced = false;
                foreach (var chunk in layout.Chunks)
                {
                    if (chunk.Id == chunkId)
                    {
                        // 跳过旧 chunk；在其原位置写入新 chunk（若有）
                        if (newChunkData is { Length: > 0 })
                            WriteChunk(output, chunkId, newChunkData);
                        replaced = true;
                        continue;
                    }
                    input.Position = chunk.HeaderOffset;
                    CopyRange(input, output, chunk.TotalLength);
                }

                // 若目标 chunk 原本不存在且需新增：追加到末尾
                if (!replaced && newChunkData is { Length: > 0 })
                    WriteChunk(output, chunkId, newChunkData);

                output.Flush(true);
                long newLength = output.Length;
                output.Position = 4;
                WriteBe64(output, (ulong)(newLength - 12));
            }

            File.Replace(tempPath, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 清理失败可忽略 */ }
        }
    }

    private static void WriteChunk(Stream output, string id, byte[] data)
    {
        output.Write(Encoding.ASCII.GetBytes(id));
        WriteBe64(output, (ulong)data.Length);
        output.Write(data);
        if ((data.Length & 1) != 0) output.WriteByte(0); // 偶数对齐
    }

    /// <summary>解析 DIIN chunk 的文本子字段（数据区 = 子 chunk 序列，文本子 chunk 数据=uint32长度+文本）。</summary>
    private static void ReadDiinFieldsInternal(FileStream fs, ChunkInfo diin, List<DffDiinField> result)
    {
        if (diin.DataLength < 0 || diin.DataLength > MaxId3ChunkBytes) return;
        byte[] buf = new byte[diin.DataLength];
        fs.Position = diin.DataOffset;
        if (!ReadExactly(fs, buf, 0, buf.Length)) return;

        int p = 0;
        while (p + ChunkHeaderSize <= buf.Length)
        {
            string cid = Encoding.ASCII.GetString(buf, p, 4);
            if (!IsPrintableId(buf.AsSpan(p, 4))) break;
            long csize = (long)ReadBe64(buf.AsSpan(p + 4, 8));
            int dp = p + ChunkHeaderSize;
            if (csize < 0 || dp + csize > buf.Length) break;

            // 文本类子 chunk（DITI/DIAR/DGEN/DINA/DARE/DPRS 等）：4 字节 BE 长度 + 文本
            if (csize >= 4)
            {
                uint textLen = ((uint)buf[dp] << 24) | ((uint)buf[dp + 1] << 16) | ((uint)buf[dp + 2] << 8) | buf[dp + 3];
                int textStart = dp + 4;
                if (textStart + textLen <= buf.Length)
                {
                    string text = Encoding.UTF8.GetString(buf, textStart, (int)textLen);
                    result.Add(new DffDiinField { Id = cid, Text = text });
                }
            }
            long next = dp + csize + (csize & 1);
            if (next <= p) break;
            p = (int)next;
        }
    }

    /// <summary>构建 DIIN chunk 数据区（子 chunk 序列）。</summary>
    private static byte[] BuildDiinChunk(List<DffDiinField> fields)
    {
        using var ms = new MemoryStream();
        foreach (var f in fields)
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(f.Text ?? "");
            byte[] sub = new byte[4 + textBytes.Length];
            sub[0] = (byte)(textBytes.Length >> 24);
            sub[1] = (byte)(textBytes.Length >> 16);
            sub[2] = (byte)(textBytes.Length >> 8);
            sub[3] = (byte)textBytes.Length;
            textBytes.CopyTo(sub, 4);
            WriteChunk(ms, f.Id, sub);
        }
        return ms.ToArray();
    }

    private const int CommtHeaderSize = 14; // year(2)+mon(1)+day(1)+hour(1)+min(1)+sec(1)+type(1)+ref(2)+count(4)

    /// <summary>解析 COMT chunk：uint16 条数 + N 条(14字节头+文本)。</summary>
    private static void ReadCommtInternal(FileStream fs, ChunkInfo comt, List<DffCommtComment> result)
    {
        if (comt.DataLength < 2 || comt.DataLength > MaxId3ChunkBytes) return;
        byte[] buf = new byte[comt.DataLength];
        fs.Position = comt.DataOffset;
        if (!ReadExactly(fs, buf, 0, buf.Length)) return;

        int count = (buf[0] << 8) | buf[1];
        int p = 2;
        for (int i = 0; i < count; i++)
        {
            if (p + CommtHeaderSize > buf.Length) break;
            var c = new DffCommtComment
            {
                Year = (ushort)((buf[p] << 8) | buf[p + 1]),
                Month = buf[p + 2],
                Day = buf[p + 3],
                Hour = buf[p + 4],
                Minute = buf[p + 5],
                Second = buf[p + 6],
                CommentType = buf[p + 7],
                CommentReference = (ushort)((buf[p + 8] << 8) | buf[p + 9]),
                Count = (buf[p + 10] << 24) | (buf[p + 11] << 16) | (buf[p + 12] << 8) | buf[p + 13],
            };
            int textStart = p + CommtHeaderSize;
            if (c.Count >= 0 && textStart + c.Count <= buf.Length)
                c.Text = Encoding.UTF8.GetString(buf, textStart, c.Count);
            result.Add(c);
            p = textStart + c.Count;
        }
    }

    /// <summary>构建 COMT chunk 数据区。</summary>
    private static byte[] BuildCommtChunk(List<DffCommtComment> comments)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(comments.Count >> 8));
        ms.WriteByte((byte)comments.Count);
        foreach (var c in comments)
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(c.Text ?? "");
            ms.WriteByte((byte)(c.Year >> 8)); ms.WriteByte((byte)c.Year);
            ms.WriteByte(c.Month); ms.WriteByte(c.Day);
            ms.WriteByte(c.Hour); ms.WriteByte(c.Minute); ms.WriteByte(c.Second);
            ms.WriteByte(c.CommentType);
            ms.WriteByte((byte)(c.CommentReference >> 8)); ms.WriteByte((byte)c.CommentReference);
            ms.WriteByte((byte)(textBytes.Length >> 24));
            ms.WriteByte((byte)(textBytes.Length >> 16));
            ms.WriteByte((byte)(textBytes.Length >> 8));
            ms.WriteByte((byte)textBytes.Length);
            ms.Write(textBytes);
        }
        return ms.ToArray();
    }

    // ============================================================
    // 基础工具
    // ============================================================

    private static string? DecodeText(byte[] buf, int offset, int length, byte? encOverride = null)
    {
        if (length <= 0) return null;
        byte enc = encOverride ?? buf[offset];
        int start = offset;
        int len = length;
        if (encOverride == null) { start++; len--; } // 跳过编码字节

        string text = enc switch
        {
            0 => Encoding.Latin1.GetString(buf, start, len),
            1 => DecodeUtf16Bom(buf, start, len),
            2 => Encoding.BigEndianUnicode.GetString(buf, start, len),
            3 => Encoding.UTF8.GetString(buf, start, len),
            _ => Encoding.UTF8.GetString(buf, start, len),
        };
        // 多值帧取首个非空值；去掉尾零
        var first = text.Split('\0').FirstOrDefault(s => s.Length > 0);
        return string.IsNullOrEmpty(first) ? null : first.TrimEnd('\0');
    }

    private static string DecodeUtf16Bom(byte[] buf, int start, int len)
    {
        if (len >= 2 && buf[start] == 0xFF && buf[start + 1] == 0xFE)
            return Encoding.Unicode.GetString(buf, start + 2, len - 2);
        if (len >= 2 && buf[start] == 0xFE && buf[start + 1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(buf, start + 2, len - 2);
        return Encoding.Unicode.GetString(buf, start, len);
    }

    /// <summary>在缓冲区内跳过编码相关的终止符（描述字段），返回正文起始下标。</summary>
    private static int SkipTerminated(byte[] buf, int p, byte enc)
    {
        if (enc is 1 or 2)
        {
            // UTF-16：双零（按 2 字节对齐）
            while (p + 1 < buf.Length)
            {
                if (buf[p] == 0 && buf[p + 1] == 0) return p + 2;
                p += 2;
            }
            return buf.Length;
        }
        while (p < buf.Length && buf[p] != 0) p++;
        return p + 1;
    }

    /// <summary>流式跳过 APIC 描述字段，返回描述区字节数（含终止符）；失败返回 -1。</summary>
    private static int SkipDescriptionInStream(FileStream fs, long start, long frameEnd, byte enc)
    {
        byte[] window = new byte[4096];
        long pos = start;
        int step = enc is 1 or 2 ? 2 : 1;
        while (pos < frameEnd)
        {
            int want = (int)Math.Min(window.Length, frameEnd - pos);
            fs.Position = pos;
            if (!ReadExactly(fs, window, 0, want)) return -1;
            for (int i = 0; i + step <= want; i += step)
            {
                bool terminator = step == 1 ? window[i] == 0 : window[i] == 0 && window[i + 1] == 0;
                if (terminator) return (int)(pos - start) + i + step;
            }
            pos += want;
        }
        return -1;
    }

    private static (uint? Pos, uint? Total) SplitPair(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var slash = text.IndexOf('/');
        if (slash < 0) return (ParseNum(text.Trim()), null);
        return (ParseNum(text[..slash].Trim()), ParseNum(text[(slash + 1)..].Trim()));
    }

    private static uint? ParseNum(string s)
        => uint.TryParse(s, out var v) && v > 0 ? v : null;

    private static uint? ParseYear(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        for (int i = 0; i + 4 <= text.Length; i++)
            if (char.IsDigit(text[i]) && char.IsDigit(text[i + 1]) && char.IsDigit(text[i + 2]) && char.IsDigit(text[i + 3])
                && uint.TryParse(text.AsSpan(i, 4), out var y) && y is >= 1600 and <= 2100)
                return y;
        return null;
    }

    // APIC 类型字节遵循 ID3 编号（3=FrontCover…），与 TagLib.PictureType 数值一致；
    // 应用内 CoverPictureType 按名称对应（FromLegacyType 同款约定）
    private static CoverPictureType PicTypeToEnum(byte type) =>
        Enum.TryParse<CoverPictureType>(((TagLib.PictureType)type).ToString(), out var v)
            ? v : CoverPictureType.Other;

    // 与应用 PicTypeToName 一致的中文类型名
    private static string PicTypeName(byte type) => type switch
    {
        3 => "封面",
        4 => "封底",
        _ => "其他",
    };

    private static bool IsPrintableId(ReadOnlySpan<byte> id)
    {
        foreach (var b in id)
            if (b < 0x20 || b > 0x7E) return false;
        return true;
    }

    private static bool ReadExactly(Stream s, byte[] buf, int offset, int count)
    {
        int got = 0;
        while (got < count)
        {
            int r = s.Read(buf, offset + got, count - got);
            if (r <= 0) return false;
            got += r;
        }
        return true;
    }

    private static void CopyRange(Stream input, Stream output, long count)
    {
        byte[] buffer = new byte[81920];
        while (count > 0)
        {
            int take = (int)Math.Min(buffer.Length, count);
            int read = input.Read(buffer, 0, take);
            if (read <= 0) throw new EndOfStreamException("DFF 重建：数据意外结束");
            output.Write(buffer, 0, read);
            count -= read;
        }
    }

    private static ulong ReadBe64(ReadOnlySpan<byte> b)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[i];
        return v;
    }

    private static void WriteBe64(Stream s, ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        for (int i = 7; i >= 0; i--) { b[i] = (byte)(v & 0xFF); v >>= 8; }
        s.Write(b);
    }

    private static int ReadSyncSafe(ReadOnlySpan<byte> b)
        => (b[0] << 21) | (b[1] << 14) | (b[2] << 7) | b[3];

    private static void WriteSyncSafe(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)((value >> 21) & 0x7F);
        buf[offset + 1] = (byte)((value >> 14) & 0x7F);
        buf[offset + 2] = (byte)((value >> 7) & 0x7F);
        buf[offset + 3] = (byte)(value & 0x7F);
    }
}
