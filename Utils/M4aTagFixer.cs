using System.Text;
using MusicTagRecover.Models;

namespace MusicTagRecover.Utils;

/// <summary>
/// M4A (MP4) 标签修复器 — 当 TagLibSharp 因容器畸形无法解析 M4A 文件时，
/// 读取 TagData 生成标准 ilst box 并替换到文件中，使 TagLibSharp 能正常写入。
///
/// 原理：M4A 的标签全部存储在 moov → udta → meta → ilst 路径中，
/// 与音频数据 (mdat) 完全独立。因此可以只替换 ilst box 而不影响音频。
/// </summary>
public static class M4aTagFixer
{
    /// <summary>MP4 标签类型中的 © 字符（0xA9）</summary>
    private static readonly byte[] Mp4Copyright = { 0xA9 };
    /// <summary>
    /// 尝试修复 M4A 文件：用 TagData 中的值替换 ilst box。
    /// 如果新 ilst 比旧的更长，会更新沿途父 box（meta/udta/moov）的 size 字段。
    /// </summary>
    /// <param name="filePath">M4A 文件路径</param>
    /// <param name="tags">要写入的标签数据</param>
    /// <returns>修复成功返回 true</returns>
    public static bool TryFix(string filePath, TagData tags)
    {
        try
        {
            var data = File.ReadAllBytes(filePath);

            // 1. 遍历 box 树，找到 ilst 位置和父链
            var ilstInfo = FindIlst(data);
            if (ilstInfo == null) return false;

            // 2. 生成新的 ilst box
            var newIlst = BuildIlst(tags);
            var oldSize = ilstInfo.Size;
            var delta = newIlst.Length - oldSize;
            int newTotalLen;

            using (var ms = new MemoryStream(data.Length + Math.Max(0, delta)))
            {
                // 写入 ilst 之前的部分
                ms.Write(data, 0, ilstInfo.Offset);

                // 写入新 ilst
                ms.Write(newIlst, 0, newIlst.Length);

                // 如果新 ilst 更短，补 free box
                if (delta < 0)
                {
                    ms.Write(MakeFreeBox(-delta), 0, -delta);
                }

                // 写入 ilst 之后的部分
                int afterEnd = ilstInfo.Offset + oldSize;
                ms.Write(data, afterEnd, data.Length - afterEnd);

                // 3. 更新父链中各个 box 的 size 字段
                var result = ms.ToArray();
                foreach (var parentOffset in ilstInfo.ParentOffsets)
                {
                    var oldParentSize = ReadBE32(result, parentOffset);
                    var newParentSize = (uint)(oldParentSize + delta);
                    WriteBE32(result, parentOffset, newParentSize);
                }

                // 如果 moov 是顶层 box，也可能需要更新
                if (ilstInfo.MoovOffset >= 0)
                {
                    var oldMoovSize = ReadBE32(result, ilstInfo.MoovOffset);
                    var newMoovSize = (uint)(oldMoovSize + delta);
                    WriteBE32(result, ilstInfo.MoovOffset, newMoovSize);
                }

                File.WriteAllBytes(filePath, result);
                newTotalLen = result.Length;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    // ilst box 生成
    // ============================================================

    /// <summary>从 TagData 生成完整的 ilst box</summary>
    private static byte[] BuildIlst(TagData tags)
    {
        var items = new List<byte[]>();

        AddTextItem(items, "©nam", tags.Title);
        AddTextItem(items, "©art", tags.Artist);
        AddTextItem(items, "©alb", tags.Album);
        AddTextItem(items, "aART", tags.AlbumArtist);
        AddTextItem(items, "©wrt", tags.Composer);
        AddTextItem(items, "©gen", tags.Genre);
        AddTextItem(items, "©cmt", tags.Comment);

        if (!string.IsNullOrEmpty(tags.Lyricist) || !string.IsNullOrEmpty(tags.Lyrics))
        {
            // 优先放歌词，如果只有作词者也放
            var lyricText = !string.IsNullOrEmpty(tags.Lyrics) ? tags.Lyrics : tags.Lyricist;
            AddTextItem(items, "©lyr", lyricText);
        }

        if (tags.Year.HasValue)
            AddTextItem(items, "©day", tags.Year.Value.ToString());

        if (tags.Track.HasValue)
            AddNumericItem(items, "trkn", tags.Track.Value, tags.TrackCount ?? 0);

        if (tags.Disc.HasValue)
            AddNumericItem(items, "disk", tags.Disc.Value, tags.DiscCount ?? 0);

        // 封面（covr item）：将图片数据写入 ilst
        if (tags.AllPictures != null)
        {
            foreach (var pic in tags.AllPictures.Where(p => p.HasImage))
            {
                AddCoverItem(items, pic);
            }
        }
        else if (tags.CoverArtData is { Length: > 0 })
        {
            AddCoverItem(items, tags.CoverArtData, tags.CoverArtMimeType ?? "image/jpeg");
        }

        return MakeContainerBox("ilst", items);
    }

    private static void AddTextItem(List<byte[]> items, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        items.Add(MakeItemBox(key, Encoding.UTF8.GetBytes(value), 0x01)); // type=1 = UTF-8
    }

    private static void AddNumericItem(List<byte[]> items, string key, uint number, uint total)
    {
        // trkn/disk: 16 bytes data (reserved+locale = 8, number = 2, total = 2, padding = 4)
        var data = new byte[16];
        data[2] = (byte)((number >> 8) & 0xFF);   // 大端 uint16
        data[3] = (byte)(number & 0xFF);
        data[4] = (byte)((total >> 8) & 0xFF);
        data[5] = (byte)(total & 0xFF);
        items.Add(MakeItemBox(key, data, 0x00)); // type=0 = implicit
    }

    /// <summary>添加封面 covr item（从 CoverArt 对象）</summary>
    private static void AddCoverItem(List<byte[]> items, CoverArt cover)
    {
        var mimeType = cover.MimeType ?? "image/jpeg";
        AddCoverItem(items, cover.ImageData!, mimeType);
    }

    /// <summary>添加封面 covr item（从原始字节）</summary>
    private static void AddCoverItem(List<byte[]> items, byte[] imageData, string mimeType)
    {
        // covr item 的 data box: type=13(JPEG) 或 14(PNG) + 图片原始字节
        var dataType = mimeType.Contains("png") ? 13 + 1 : 13; // 0x0D=JPEG, 0x0E=PNG
        var dataContent = new byte[8 + imageData.Length];
        WriteBE32(dataContent, 0, (uint)dataType);
        Array.Copy(imageData, 0, dataContent, 8, imageData.Length);

        var dataBox = MakeBox("data", dataContent);
        items.Add(MakeBox("covr", dataBox));
    }

    /// <summary>生成单个标签项 box，如 ©nam + data</summary>
    private static byte[] MakeItemBox(string key, byte[] value, int dataType)
    {
        // data box header: size(4) + "data"(4) + flags(4) + locale(4)
        var dataContent = new byte[8 + value.Length];
        WriteBE32(dataContent, 0, (uint)dataType); // type: 1=UTF8, 0=implicit, 13=JPEG
        Array.Copy(value, 0, dataContent, 8, value.Length);

        var dataBox = MakeBox("data", dataContent);
        return MakeBox(key, dataBox);
    }

    /// <summary>生成容器 box（如 ilst），size = 8 + 所有子 box 总长</summary>
    private static byte[] MakeContainerBox(string type, List<byte[]> children)
    {
        var content = Combine(children);
        return MakeBox(type, content);
    }

    /// <summary>生成通用 box（类型名使用 Latin-1 编码，支持 © 等单字节字符）</summary>
    private static byte[] MakeBox(string type, byte[] content)
    {
        var result = new byte[8 + content.Length];
        WriteBE32(result, 0, (uint)(8 + content.Length));
        // MP4 box 类型使用 Latin-1/windows-1252 编码（ASCII 不能表示 © = 0xA9）
        var typeBytes = Encoding.GetEncoding(28591).GetBytes(type);
        Array.Copy(typeBytes, 0, result, 4, Math.Min(4, typeBytes.Length));
        Array.Copy(content, 0, result, 8, content.Length);
        return result;
    }

    /// <summary>生成 free box（填充用）</summary>
    private static byte[] MakeFreeBox(int size)
    {
        if (size < 8) size = 8;
        var result = new byte[size];
        WriteBE32(result, 0, (uint)size);
        result[4] = (byte)'f'; result[5] = (byte)'r'; result[6] = (byte)'e'; result[7] = (byte)'e';
        return result;
    }

    // ============================================================
    // Box 树遍历
    // ============================================================

    private class IlstInfo
    {
        public int Offset;
        public int Size;
        public List<int> ParentOffsets = new();
        public int MoovOffset = -1;
    }

    /// <summary>在文件中查找 ilst box，返回位置和父链</summary>
    private static IlstInfo? FindIlst(byte[] data)
    {
        var info = new IlstInfo();
        // 单次遍历：跟踪容器层级，找到 ilst 时记录父链
        if (!FindIlstRecursive(data, 0, data.Length, info)) return null;
        return info;
    }

    private static bool FindIlstRecursive(byte[] data, int start, int end, IlstInfo info, int depth = 0)
    {
        if (depth > 20) return false;
        int pos = start;
        while (pos < end - 8)
        {
            int size = ReadBE32Int(data, pos);
            if (size < 8) { pos++; continue; }

            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            if (type == "ilst")
            {
                info.Offset = pos;
                info.Size = size;
                return true;
            }

            if (IsContainer(type))
            {
                // 记录 moov/udta/meta/trak 作为潜在的父容器
                if (type is "moov" or "udta" or "meta" or "trak")
                {
                    if (type == "moov") info.MoovOffset = pos;
                    else info.ParentOffsets.Add(pos);
                }

                if (FindIlstRecursive(data, pos + 8, pos + size, info, depth + 1))
                    return true;

                // 回溯父链：只在之前添加过父容器的类型上执行
                if (type == "moov") info.MoovOffset = -1;
                else if (type is "udta" or "meta" or "trak")
                    info.ParentOffsets.RemoveAt(info.ParentOffsets.Count - 1);
            }
            pos += size;
        }
        return false;
    }

    private static bool IsContainer(string type) => type switch
    {
        "udta" or "meta" or "ilst" or "trak" or "mdia" or "minf" or
        "stbl" or "edts" or "dinf" or "gmhd" or "wave" or "moov" => true,
        _ => false
    };

    // ============================================================
    // 二进制工具
    // ============================================================

    private static uint ReadBE32(byte[] data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }

    private static int ReadBE32Int(byte[] data, int offset)
    {
        return (int)ReadBE32(data, offset);
    }

    private static void WriteBE32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)((value >> 24) & 0xFF);
        data[offset + 1] = (byte)((value >> 16) & 0xFF);
        data[offset + 2] = (byte)((value >> 8) & 0xFF);
        data[offset + 3] = (byte)(value & 0xFF);
    }

    private static byte[] Combine(List<byte[]> parts)
    {
        var total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        var offset = 0;
        foreach (var p in parts) { Array.Copy(p, 0, result, offset, p.Length); offset += p.Length; }
        return result;
    }
}
