namespace MusicTagRecover.Utils;

/// <summary>
/// DFF（DSDIFF/DSD）容器标签修复器。
/// <para>
/// 背景：大量市面 DFF 文件由不同标签工具反复写入，会出现两类典型容器畸形，
/// 导致 TagLibSharp2 的 DffFile 严格 chunk 遍历读不到标签（Tag == null）：
/// <list type="number">
/// <item>"ID3 " chunk 前多出游离的 0x00 填充字节，使 chunk 头落在奇数偏移
/// （DSDIFF 规范要求 chunk 按偶数字节对齐）——严格解析器在偶数边界读到的是
/// "\0ID3" 与畸形长度，标签块整体被跳过。</item>
/// <item>历次写入残留：无 chunk 封装的裸 ID3v2 标签、重复 "ID3 " chunk、
/// chunk 之间的零填充/旧封面垃圾数据。</item>
/// </list>
/// 修复策略（只重建容器骨架，不重编码、不触碰音频采样）：
/// 保留从文件头到第一个畸形 chunk 边界之前的全部正常 chunk（含 FVER/PROP/DSD
/// 音频块/DIIN/COMT），在全文件中扫描并选出内容最完整的合法 ID3v2 标签，
/// 重新封装为唯一一个偶数对齐的 "ID3 " chunk 追加在音频之后，并重写 FRM8 主长度。
/// </para>
/// <para>
/// 安全性：流式拷贝（不整文件读入内存）；找不到音频块或找不到任何合法标签时不做修改；
/// 先写同卷临时文件再 <see cref="File.Replace(string,string,string)"/> 原子替换。
/// </para>
/// </summary>
public static class DffTagFixer
{
    private static readonly byte[] Frm8 = "FRM8"u8.ToArray();
    private static readonly byte[] Id3Space = "ID3 "u8.ToArray();
    private static readonly byte[] Id3 = "ID3"u8.ToArray();

    private const int ChunkHeaderSize = 12;   // 4 字节 ID + 8 字节 BE 长度
    private const int Id3HeaderSize = 10;      // ID3 标识3 + 版本2 + 标志1 + 长度4
    private const int ScanBufferSize = 1 << 20; // 1 MiB

    /// <summary>
    /// 检测并修复 DFF 容器。仅在结构畸形导致标签不可读时才有意义（由调用方判定）。
    /// </summary>
    /// <param name="filePath">DFF 文件绝对路径</param>
    /// <param name="reason">失败时返回原因</param>
    /// <returns>true 表示文件已被重建为规范容器；false 表示无法/无需修复（文件未改动）</returns>
    public static bool TryFix(string filePath, out string? reason)
    {
        reason = null;
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length < 16)
            {
                reason = "文件不存在或过小";
                return false;
            }

            long fileLength = fi.Length;
            ulong masterSize;
            bool hasAudioChunk;
            long keepEnd;

            // 同卷临时文件：先完整重建，关闭输入句柄后再做原子替换
            // （Windows 下输入文件仍有打开句柄时 File.Replace 会抛共享冲突）
            string tempPath = filePath + ".dffrepair" + Guid.NewGuid().ToString("N");
            try
            {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // 1. 校验 FRM8 头
                Span<byte> head = stackalloc byte[16];
                if (fs.Read(head) != 16 || !head[..4].SequenceEqual(Frm8))
                {
                    reason = "不是合法的 FRM8(DSDIFF) 文件";
                    return false;
                }
                masterSize = ReadBe64(head.Slice(4, 8));
                if (masterSize < 4 || masterSize > (ulong)(fileLength + 8))
                {
                    reason = "FRM8 主长度非法";
                    return false;
                }

                // 2. 严格按偶数字节边界遍历顶层 chunk，走到第一个畸形 chunk 为止
                long pos = 16;
                keepEnd = pos;
                hasAudioChunk = false;
                bool malformed = false;
                Span<byte> hdr = stackalloc byte[ChunkHeaderSize];
                while (pos + ChunkHeaderSize <= fileLength)
                {
                    fs.Position = pos;
                    if (fs.Read(hdr) != ChunkHeaderSize) { malformed = true; break; }

                    if (!IsPrintableChunkId(hdr[..4])) { malformed = true; break; }
                    ulong size = ReadBe64(hdr.Slice(4, 8));
                    long dataEnd = pos + ChunkHeaderSize + (long)size + (long)(size & 1UL);
                    if (size > (ulong)fileLength || dataEnd > fileLength) { malformed = true; break; }

                    var id = System.Text.Encoding.ASCII.GetString(hdr[..4]);
                    if (id is "DSD " or "DST " or "DSTC")
                        hasAudioChunk = true;

                    keepEnd = dataEnd;
                    pos = dataEnd;
                }

                // 未发现畸形 chunk（正常走到文件尾，剩余至多 1 字节奇偶填充）：
                // 容器本身没问题，标签不可读另有原因，不做无谓改写
                if (!malformed)
                {
                    reason = "容器 chunk 结构完整，跳过修复";
                    return false;
                }

                if (!hasAudioChunk)
                {
                    reason = "未在畸形点之前找到音频块（DSD/DST），放弃修复";
                    return false;
                }
                if ((keepEnd & 1) != 0)
                {
                    reason = "保留区边界不是偶数，结构超出修复器支持范围";
                    return false;
                }

                // 3. 扫描全文，抢救最完整的合法 ID3v2 标签
                var best = FindBestId3Tag(fs, fileLength);
                if (best == null)
                {
                    reason = "文件中找不到任何结构合法的 ID3v2 标签";
                    return false;
                }

                // 4. 同卷临时文件重建容器（File.Replace 在外层、输入读句柄关闭后执行）
                using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                        // 4.1 FRM8 占位头（主长度最后回填）
                        output.Write(Frm8);
                        output.Write(stackalloc byte[8]);
                        Span<byte> form = stackalloc byte[4];
                        fs.Position = 12;
                        if (fs.Read(form) != 4)
                        {
                            reason = "读取 form 类型失败";
                            return false;
                        }
                        output.Write(form);

                        // 4.2 原始文件 16 .. keepEnd 全部正常 chunk（含音频块）
                        fs.Position = 16;
                        CopyRange(fs, output, keepEnd - 16);

                        // 4.3 唯一的、偶数对齐的 ID3 chunk
                        var tagOffset = best.Value.Offset;
                        var tagLength = best.Value.Length;
                        output.Write(Id3Space);
                        WriteBe64(output, (ulong)tagLength);
                        fs.Position = tagOffset;
                        CopyRange(fs, output, tagLength);
                        if ((tagLength & 1) != 0) output.WriteByte(0); // 奇数字节补偶

                        // 4.4 回填 FRM8 主长度 = 文件总长 - 12
                        output.Flush(true);
                        long newLength = output.Length;
                        output.Position = 4;
                        WriteBe64(output, (ulong)(newLength - 12));
                } // using(output) 关闭（临时文件写完）
            } // using(fs) 关闭：输入读句柄释放后 File.Replace 才能执行

            // 5. 原子替换（同卷）
            File.Replace(tempPath, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return true;
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 临时文件清理失败可忽略 */ }
            }
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>扫描窗口中发现的标签候选（标签起始偏移与字节长度）。</summary>
    private readonly record struct TagCandidate(long Offset, int Length, bool Wrapped, int FrameCount, bool HasApic, bool HasLyrics);

    /// <summary>
    /// 扫描全文寻找合法 ID3v2 标签并选出内容最完整者。
    /// 优先级：带正确 "ID3 " chunk 封装的 > 裸标签；帧多的 > 帧少的；带封面/歌词加权。
    /// </summary>
    private static TagCandidate? FindBestId3Tag(FileStream fs, long fileLength)
    {
        fs.Position = 0;
        byte[] buffer = new byte[ScanBufferSize + 16]; // 尾部重叠 16 字节防跨块遗漏
        long baseOffset = 0;
        int validLength = 0;
        var best = new TagCandidate?();
        long bestScore = long.MinValue;

        while (baseOffset < fileLength)
        {
            fs.Position = baseOffset;
            int read = fs.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            validLength = read;

            for (int i = 0; i + Id3HeaderSize <= read; i++)
            {
                if (buffer[i] != 0x49 || buffer[i + 1] != 0x44 || buffer[i + 2] != 0x33) continue;
                byte ver = buffer[i + 3];
                if (ver is not (2 or 3 or 4)) continue;
                if (buffer[i + 4] != 0) continue; // 标志位（flags）非 0：压缩/加密/扩展头，抢救场景里直接跳过

                int tagHeaderSize = ver == 2 ? 8 : Id3HeaderSize; // v2.2 头 8 字节，v2.3/4 头 10 字节
                int tagLen;
                if (ver == 2)
                {
                    // ID3v2.2：3 字节 syncsafe 长度
                    if (buffer[i + 5] > 0x7F || buffer[i + 6] > 0x7F || buffer[i + 7] > 0x7F) continue;
                    tagLen = tagHeaderSize + (buffer[i + 5] << 14) | (buffer[i + 6] << 7) | buffer[i + 7];
                }
                else
                {
                    if (buffer[i + 6] > 0x7F || buffer[i + 7] > 0x7F || buffer[i + 8] > 0x7F || buffer[i + 9] > 0x7F)
                        continue;
                    tagLen = tagHeaderSize
                        | (buffer[i + 6] << 21) | (buffer[i + 7] << 14)
                        | (buffer[i + 8] << 7) | buffer[i + 9];
                }
                if (tagLen < tagHeaderSize + 16) continue;
                long absoluteOffset = baseOffset + i;
                if (absoluteOffset + tagLen > fileLength) continue;

                bool wrapped = i >= 12
                    && buffer[i - 12] == 0x49 && buffer[i - 11] == 0x44 && buffer[i - 10] == 0x33 && buffer[i - 9] == 0x20
                    && ReadBe64(buffer.AsSpan(i - 8, 8)) == (ulong)(tagLen);

                var score = InspectTag(fs, fileLength, absoluteOffset, tagLen, ver, out int frameCount, out bool hasApic, out bool hasLyrics);
                if (score < 0) continue;

                score += (wrapped ? 1_000_000 : 0);
                score += frameCount * 10 + (hasApic ? 500 : 0) + (hasLyrics ? 200 : 0);
                // 同分时取靠前的（避免把更晚写入的残留垃圾选上来）
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new TagCandidate(absoluteOffset, tagLen, wrapped, frameCount, hasApic, hasLyrics);
                }
            }

            if (baseOffset + read >= fileLength) break;
            baseOffset += read - 16; // 重叠 16 字节
        }

        return best;
    }

    /// <summary>
    /// 校验候选标签内部帧结构并统计价值分。帧不合法返回 -1；
    /// 只要存在至少一个结构合法的帧即视为可抢救的标签。
    /// </summary>
    private static int InspectTag(FileStream fs, long fileLength, long tagOffset, int tagLength, byte ver,
        out int frameCount, out bool hasApic, out bool hasLyrics)
    {
        frameCount = 0;
        hasApic = false;
        hasLyrics = false;

        int tagHeaderSize = ver == 2 ? 8 : Id3HeaderSize;
        int header = ver == 2 ? 6 : 10;
        long bodyEnd = tagOffset + tagLength;
        if (tagOffset + tagHeaderSize > bodyEnd) return -1;

        byte[] data = new byte[Math.Min(tagLength, 4 * 1024 * 1024)]; // 单标签 >4MB 视为异常
        if (tagLength > data.Length) return -1;
        fs.Position = tagOffset;
        int got = 0;
        while (got < tagLength)
        {
            int r = fs.Read(data, got, tagLength - got);
            if (r <= 0) return -1;
            got += r;
        }

        int p = tagHeaderSize;
        int validFrames = 0;
        while (p + header <= tagLength)
        {
            // 尾部零填充（ID3 标签允许 padding）
            if (data[p] == 0) break;

            int frameHeaderIdLen = ver == 2 ? 3 : 4;
            for (int k = 0; k < frameHeaderIdLen; k++)
            {
                byte c = data[p + k];
                // 帧 ID 只接受 A-Z 0-9（TIT2/APIC/USLT/TXXX/WXXX/PRIV...）
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                    return validFrames > 0 ? validFrames : -1;
            }

            int frameSize;
            if (ver == 2)
            {
                frameSize = (data[p + 3] << 16) | (data[p + 4] << 8) | data[p + 5];
            }
            else if (ver == 4)
            {
                if (data[p + 4] > 0x7F || data[p + 5] > 0x7F || data[p + 6] > 0x7F || data[p + 7] > 0x7F)
                    return validFrames > 0 ? validFrames : -1;
                frameSize = (data[p + 4] << 21) | (data[p + 5] << 14) | (data[p + 6] << 7) | data[p + 7];
            }
            else
            {
                frameSize = (data[p + 4] << 24) | (data[p + 5] << 16) | (data[p + 6] << 8) | data[p + 7];
            }
            long next = p + header + frameSize;
            if (next > tagLength) return validFrames > 0 ? validFrames : -1;

            string fid = System.Text.Encoding.ASCII.GetString(data, p, frameHeaderIdLen);
            if (fid is "APIC" or "PIC") hasApic = true;
            if (fid is "USLT" or "SYLT" or "ULT") hasLyrics = true;
            validFrames++;
            frameCount = validFrames;
            p = (int)next;
        }

        return validFrames;
    }

    private static bool IsPrintableChunkId(ReadOnlySpan<byte> id)
    {
        foreach (var b in id)
            if (b < 0x20 || b > 0x7E) return false;
        return true;
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

    private static void CopyRange(Stream input, Stream output, long count)
    {
        byte[] buffer = new byte[81920];
        while (count > 0)
        {
            int take = (int)Math.Min(buffer.Length, count);
            int read = input.Read(buffer, 0, take);
            if (read <= 0) throw new EndOfStreamException($"DFF 修复：读取音频区数据意外结束（剩余 {count} 字节）");
            output.Write(buffer, 0, read);
            count -= read;
        }
    }
}
