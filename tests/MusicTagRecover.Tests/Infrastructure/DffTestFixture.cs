using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MusicTagRecover.Tests.Infrastructure;

/// <summary>
/// 测试用最小合法 DFF（DSDIFF）文件构造器：
/// FRM8 → FVER + PROP(SND/FS/CHNL) + DSD(确定性音频字节)，
/// 全部 chunk 偶数对齐。测试结束后释放临时目录。
/// </summary>
public sealed class DffTestFixture : IDisposable
{
    public string Directory { get; }
    public const int SampleRate = 2822400;
    public const int Channels = 2;
    public const int DsdBytes = 4096;

    public DffTestFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "dff_native_tests_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    /// <summary>创建一个最小 DFF 文件并返回路径。</summary>
    public string CreateFile(string name = "test.dff")
    {
        var path = Path.Combine(Directory, name);
        BuildMinimalDff(path);
        return path;
    }

    /// <summary>创建多个 DFF 文件（批量测试用）。</summary>
    public IReadOnlyList<string> CreateFiles(int count)
    {
        var paths = new List<string>(count);
        for (int i = 0; i < count; i++)
            paths.Add(CreateFile($"track{i + 1:00}.dff"));
        return paths;
    }

    public static void BuildMinimalDff(string path)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write(Encoding.ASCII.GetBytes("FRM8"));
        w.Write(new byte[8]); // 主长度占位
        w.Write(Encoding.ASCII.GetBytes("DSD ")); // 表单类型固定 4 字节 "DSD "（尾空格）

        // FVER（DSDIFF 1.5.0.0）
        WriteChunk(w, "FVER", new byte[] { 0x01, 0x05, 0x00, 0x00 });

        // PROP → SND + FS + CHNL
        using (var prop = new MemoryStream())
        using (var pw = new BinaryWriter(prop))
        {
            pw.Write(Encoding.ASCII.GetBytes("SND "));
            var fs = new byte[4];
            fs[0] = unchecked((byte)(SampleRate >> 24));
            fs[1] = unchecked((byte)(SampleRate >> 16));
            fs[2] = unchecked((byte)(SampleRate >> 8));
            fs[3] = unchecked((byte)SampleRate);
            WriteChunk(pw, "FS  ", fs);
            WriteChunk(pw, "CHNL", new byte[] { (byte)(Channels >> 8), (byte)Channels });
            WriteChunk(w, "PROP", prop.ToArray());
        }

        // DSD 音频块：确定性字节模式
        var audio = new byte[DsdBytes];
        for (int i = 0; i < audio.Length; i++) audio[i] = (byte)((i * 31 + 7) & 0xFF);
        WriteChunk(w, "DSD ", audio);

        // 回填 FRM8 主长度
        var data = ms.ToArray();
        ulong bodyLen = (ulong)(data.Length - 12);
        WriteBe64At(data, 4, bodyLen);
        File.WriteAllBytes(path, data);
    }

    private static void WriteChunk(BinaryWriter w, string id, byte[] data)
    {
        w.Write(Encoding.ASCII.GetBytes(id));
        var len = new byte[8];
        for (int i = 0; i < 8; i++)
            len[i] = (byte)((ulong)data.Length >> (56 - i * 8));
        w.Write(len);
        w.Write(data);
        if ((data.Length & 1) != 0) w.Write((byte)0); // 偶数对齐
    }

    private static void WriteBe64At(byte[] buf, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
            buf[offset + i] = (byte)(value >> (56 - i * 8));
    }

    /// <summary>计算 DSD chunk 数据区 SHA256（验证写操作不触碰音频数据）。</summary>
    public static string DsdSha256(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int pos = 16;
        while (pos + 12 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes, pos, 4);
            long len = ReadBe64(bytes, pos + 4);
            if (id == "DSD ")
                return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(pos + 12, (int)len)));
            pos += 12 + (int)len + (int)(len & 1);
        }
        throw new InvalidDataException("测试文件缺少 DSD chunk");
    }

    /// <summary>顶层 chunk 列表（含数据偏移与长度）。</summary>
    public static IReadOnlyList<(string Id, long Offset, long Length)> ListChunks(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var list = new List<(string, long, long)>();
        int pos = 16;
        while (pos + 12 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes, pos, 4);
            long len = ReadBe64(bytes, pos + 4);
            list.Add((id, pos + 12, len));
            pos += 12 + (int)len + (int)(len & 1);
        }
        return list;
    }

    public static bool ContainerWalkable(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 16 || !Encoding.ASCII.GetString(bytes, 0, 4).SequenceEqual("FRM8"))
            return false;
        if ((long)ReadBe64(bytes, 4) != bytes.Length - 12) return false;
        int pos = 16;
        while (pos + 12 <= bytes.Length)
        {
            for (int i = 0; i < 4; i++)
                if (bytes[pos + i] < 0x20 || bytes[pos + i] > 0x7E) return false;
            long len = ReadBe64(bytes, pos + 4);
            long total = 12 + len + (len & 1);
            if (pos + total > bytes.Length) return false;
            pos += (int)total;
        }
        return pos == bytes.Length;
    }

    private static long ReadBe64(byte[] b, int o)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
        return (long)v;
    }

    public void Dispose()
    {
        try
        {
            // 解除可能遗留的只读属性后清理
            if (System.IO.Directory.Exists(Directory))
            {
                foreach (var f in System.IO.Directory.GetFiles(Directory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                System.IO.Directory.Delete(Directory, true);
            }
        }
        catch { /* 临时目录清理失败不影响测试结果 */ }
    }
}
