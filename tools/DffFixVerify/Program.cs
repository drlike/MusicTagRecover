using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.Services;
using MusicTagRecover.Utils;

// ============================================================
// 顶级语句（验证主流程）；辅助类型声明位于文件末尾
// ============================================================
Console.OutputEncoding = Encoding.UTF8;

string f1Path = args.Length > 0
    ? args[0]
    : @"d:\BaiduNetdiskDownload\490张无损音乐专辑合集\滨崎步(Ayumi Hamasaki) - MY STORY (私物语) - 2005  DFF\03 - 滨崎步 - GAME.dff";
string f2Path = args.Length > 1
    ? args[1]
    : @"d:\BaiduNetdiskDownload\490张无损音乐专辑合集\精英国乐团 - 丝竹管弦 - 2001   DFF\01 - 精英国乐团 - 大浪淘沙(琵琶合奏).dff";

string workDir = Path.Combine(Path.GetTempPath(), "mtc_dff_verify_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(workDir);
string broken = Path.Combine(workDir, "f1_broken.dff");
string normal = Path.Combine(workDir, "f2_normal.dff");

Console.WriteLine($"[准备] 工作目录: {workDir}");
File.Copy(f1Path, broken, overwrite: true);
File.Copy(f2Path, normal, overwrite: true);
Console.WriteLine($"[准备] 文件1(故障)副本: {new FileInfo(broken).Length} 字节");
Console.WriteLine($"[准备] 文件2(正常)副本: {new FileInfo(normal).Length} 字节");

int pass = 0, fail = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "[PASS] " : "[FAIL] ")} {name}{(detail == null ? "" : "  -> " + detail)}");
    if (ok) pass++; else fail++;
}

var svc = new TagService(new ConsoleLogger());

// ============================================================
// 0. 修复前状态：畸形复现 + 音频指纹基线
// ============================================================
Console.WriteLine();
Console.WriteLine("========== 场景0：修复前根因复现 ==========");

var brokenStruct0 = DffStructure.Inspect(broken);
Console.WriteLine($"  chunk 链: {brokenStruct0.Diagram}");
Check("文件1 修复前确实存在畸形 chunk 边界", brokenStruct0.Malformed,
    brokenStruct0.MalformedAt >= 0 ? $"首个非法 chunk 头 @ {brokenStruct0.MalformedAt}（奇数边界={brokenStruct0.MalformedAt % 2 != 0}）" : null);

// TagLibSharp2Engine 为 internal，反射调用以复现“media 非空但 Tag == null”的根因
{
    var engineType = typeof(TagService).Assembly
        .GetType("MusicTagRecover.Services.TagLibSharp2Engine", throwOnError: true)!;
    var readMethod = engineType.GetMethod("ReadFile", BindingFlags.Public | BindingFlags.Static)!;
    using var media0 = readMethod.Invoke(null, new object[] { broken }) as IDisposable;
    var tagBefore = media0?.GetType().GetProperty("Tag")?.GetValue(media0);
    Check("文件1 修复前 TagLibSharp2 返回 media 但 Tag == null（写入假成功的根因）",
        media0 != null && tagBefore == null,
        media0 == null ? "media 为 null" : $"Tag={(tagBefore == null ? "null" : "非null")}");
}

string? hashBrokenBefore = DffStructure.HashDsdChunk(broken);
var propsBaseline = ReadProps(broken);
Console.WriteLine($"  文件1 DSD 音频块 SHA256: {Short(hashBrokenBefore)}");
Console.WriteLine($"  文件1 音频属性基线: 时长={propsBaseline.durationMs}ms 采样率={propsBaseline.sampleRate} 声道={propsBaseline.channels}");
Check("文件1 修复前可读出音频属性（DSD 音频块本身完好）",
    propsBaseline.sampleRate > 0 && propsBaseline.channels == 2,
    $"rate={propsBaseline.sampleRate}, ch={propsBaseline.channels}");

// ============================================================
// 1. 读取触发自动修复：原有标签被抢救并读出
// ============================================================
Console.WriteLine();
Console.WriteLine("========== 场景1：读取时自动修复容器，原标签完整恢复 ==========");

var restored = svc.ReadTagsAsync(broken).GetAwaiter().GetResult();
Check("修复后读出原标题 TIT2=GAME", restored.Title == "GAME", $"实际: {restored.Title}");
Check("修复后读出原艺术家 TPE1", !string.IsNullOrEmpty(restored.Artist), $"实际: {restored.Artist}");
Check("修复后读出原专辑 TALB=MY STORY", restored.Album == "MY STORY", $"实际: {restored.Album}");
Check("修复后读出原年份 TDRC=2004", restored.Year == 2004, $"实际: {restored.Year}");
Check("修复后读出原音轨 TRCK=3", restored.Track == 3, $"实际: {restored.Track}");

var brokenStruct1 = DffStructure.Inspect(broken);
Check("修复后顶层 chunk 全部偶数对齐、无畸形", !brokenStruct1.Malformed, brokenStruct1.Diagram);
Check("修复后 FRM8 主长度与文件实际长度一致", brokenStruct1.FrmSizeConsistent);

string? hashBrokenAfterFix = DffStructure.HashDsdChunk(broken);
Check("修复前后 DSD 音频块字节完全一致（零音频损伤）", hashBrokenBefore == hashBrokenAfterFix);
var propsAfterFix = ReadProps(broken);
Check("修复后音频属性保持不变",
    propsAfterFix.sampleRate == propsBaseline.sampleRate
    && propsAfterFix.channels == propsBaseline.channels
    && Math.Abs((propsAfterFix.durationMs ?? 0) - (propsBaseline.durationMs ?? 0)) < 100,
    $"rate {propsBaseline.sampleRate}->{propsAfterFix.sampleRate}, ch {propsBaseline.channels}->{propsAfterFix.channels}");

// 修复幂等：再跑一次修复器应跳过且文件不变
long lenBefore2 = new FileInfo(broken).Length;
bool fixAgain = DffTagFixer.TryFix(broken, out var reason2);
long lenAfter2 = new FileInfo(broken).Length;
Check("对已修复文件再次执行修复器：跳过且不改动", !fixAgain && lenBefore2 == lenAfter2, reason2);

// ============================================================
// 2. 场景A：修改已有标签
// ============================================================
Console.WriteLine();
Console.WriteLine("========== 场景A：修改已有标签 ==========");

var modified = new TagData
{
    Title = "修复验证-标题已修改",
    Artist = "修复验证-艺术家",
    Album = "修复验证-专辑",
    Year = 2005,
    Track = 7,
    Genre = "修复验证-流派",
    Comment = "修复验证-备注",
};
bool w1 = svc.WriteTagsAsync(broken, modified).GetAwaiter().GetResult();
Check("WriteTagsAsync 修改标签返回成功", w1);

var r1 = svc.ReadTagsAsync(broken).GetAwaiter().GetResult();
Check("修改后回读标题一致", r1.Title == modified.Title, $"实际: {r1.Title}");
Check("修改后回读艺术家一致", r1.Artist == modified.Artist, $"实际: {r1.Artist}");
Check("修改后回读专辑一致", r1.Album == modified.Album, $"实际: {r1.Album}");
Check("修改后回读年份一致", r1.Year == 2005, $"实际: {r1.Year}");
Check("修改后回读音轨一致", r1.Track == 7, $"实际: {r1.Track}");
Check("修改后回流派一致", r1.Genre == modified.Genre, $"实际: {r1.Genre}");
Check("修改后回读备注一致", r1.Comment == modified.Comment, $"实际: {r1.Comment}");
Check("修改标签后 DSD 音频块不变", hashBrokenBefore == DffStructure.HashDsdChunk(broken));
Check("修改标签后音频属性不变", AudioPropsEqual(propsBaseline, ReadProps(broken)));

// 歌词写入（独立入口）
bool wl = svc.WriteLyricsAsync(broken, "修复验证歌词第一行\n第二行").GetAwaiter().GetResult();
Check("WriteLyricsAsync 写入成功", wl);
var lyricsBack = svc.ReadLyricsAsync(broken).GetAwaiter().GetResult();
Check("歌词回读一致", lyricsBack != null && lyricsBack.Contains("第一行") && lyricsBack.Contains("第二行"),
    lyricsBack == null ? "null" : lyricsBack.Replace("\n", "\\n"));
Check("写歌词后 DSD 音频块不变", hashBrokenBefore == DffStructure.HashDsdChunk(broken));

// 封面写入（独立入口）
bool wc = svc.WriteCoverArtAsync(broken, new CoverArt { ImageData = TestAssets.SamplePng, MimeType = "image/png" })
    .GetAwaiter().GetResult();
Check("WriteCoverArtAsync 写入成功", wc);
var coverBack = svc.ReadCoverArtAsync(broken).GetAwaiter().GetResult();
Check("封面回读字节一致", coverBack != null && coverBack.ImageData.AsSpan().SequenceEqual(TestAssets.SamplePng),
    coverBack == null ? "null" : $"{coverBack!.ImageData.Length} 字节");
Check("写封面后 DSD 音频块不变", hashBrokenBefore == DffStructure.HashDsdChunk(broken));

// ============================================================
// 3. 场景B：清空 -> 新增标签
// ============================================================
Console.WriteLine();
Console.WriteLine("========== 场景B：清空标签后重新新增 ==========");

bool c1 = svc.ClearTagsAsync(broken).GetAwaiter().GetResult();
Check("ClearTagsAsync 清空返回成功", c1);
var rEmpty = svc.ReadTagsAsync(broken).GetAwaiter().GetResult();
Check("清空后标题为空", string.IsNullOrEmpty(rEmpty.Title), $"实际: '{rEmpty.Title}'");
Check("清空后艺术家为空", string.IsNullOrEmpty(rEmpty.Artist), $"实际: '{rEmpty.Artist}'");
Check("清空后专辑为空", string.IsNullOrEmpty(rEmpty.Album), $"实际: '{rEmpty.Album}'");
Check("清空后封面被移除", svc.ReadCoverArtAsync(broken).GetAwaiter().GetResult() == null);
Check("清空后 DSD 音频块不变", hashBrokenBefore == DffStructure.HashDsdChunk(broken));
Check("清空后音频属性不变", AudioPropsEqual(propsBaseline, ReadProps(broken)));

var added = new TagData
{
    Title = "修复验证-新增标题",
    Artist = "修复验证-新增艺术家",
    Album = "修复验证-新增专辑",
    Year = 2024,
    Track = 9,
    Genre = "J-Pop",
};
bool w2 = svc.WriteTagsAsync(broken, added).GetAwaiter().GetResult();
Check("对空标签文件 WriteTagsAsync 新增返回成功", w2);
var r2 = svc.ReadTagsAsync(broken).GetAwaiter().GetResult();
Check("新增后回读标题一致", r2.Title == added.Title, $"实际: {r2.Title}");
Check("新增后回读艺术家一致", r2.Artist == added.Artist, $"实际: {r2.Artist}");
Check("新增后回读专辑一致", r2.Album == added.Album, $"实际: {r2.Album}");
Check("新增后回读年份一致", r2.Year == 2024, $"实际: {r2.Year}");
Check("新增后回读音轨一致", r2.Track == 9, $"实际: {r2.Track}");
Check("新增标签后 DSD 音频块不变", hashBrokenBefore == DffStructure.HashDsdChunk(broken));
Check("新增标签后 chunk 结构仍规范", !DffStructure.Inspect(broken).Malformed);

// ============================================================
// 4. 场景C：空标签写入（空值覆盖）
// ============================================================
Console.WriteLine();
Console.WriteLine("========== 场景C：空标签（空值覆盖写入） ==========");

var blanks = new TagData
{
    Title = "", Artist = "", Album = "", Genre = "", Comment = "",
    Year = 0, Track = 0, TrackCount = 0, AlbumArtist = "", Composer = "",
    Disc = 0, DiscCount = 0, Lyrics = "",
};
bool w3 = svc.WriteTagsAsync(broken, blanks).GetAwaiter().GetResult();
Check("空标签写入返回成功", w3);
var r3 = svc.ReadTagsAsync(broken).GetAwaiter().GetResult();
Check("空值写入后标题为空", string.IsNullOrEmpty(r3.Title), $"实际: '{r3.Title}'");
Check("空值写入后艺术家为空", string.IsNullOrEmpty(r3.Artist), $"实际: '{r3.Artist}'");
Check("空值写入后专辑为空", string.IsNullOrEmpty(r3.Album), $"实际: '{r3.Album}'");
Check("空值写入后年份为空/0", r3.Year is null or 0, $"实际: {r3.Year}");
Check("空值写入后音轨为空/0", r3.Track is null or 0, $"实际: {r3.Track}");
Check("空标签写入后 DSD 音频块不变", hashBrokenBefore == DffStructure.HashDsdChunk(broken));
Check("空标签写入后音频属性不变", AudioPropsEqual(propsBaseline, ReadProps(broken)));
Check("空标签写入后 chunk 结构仍规范", !DffStructure.Inspect(broken).Malformed);

// ============================================================
// 5. 文件2（正常文件）回归：原有功能不受影响
// ============================================================
Console.WriteLine();
Console.WriteLine("========== 回归：文件2（正常 DFF）读写与不被误修 ==========");

string hashNormalWholeBefore = Sha256WholeFile(normal);
bool fixNormal = DffTagFixer.TryFix(normal, out var reasonNormal);
string hashNormalWholeAfter = Sha256WholeFile(normal);
Check("修复器对结构正常的文件2跳过、字节不变",
    !fixNormal && hashNormalWholeBefore == hashNormalWholeAfter, reasonNormal);

var n0 = svc.ReadTagsAsync(normal).GetAwaiter().GetResult();
Console.WriteLine($"  文件2 原标签: 标题='{n0.Title}' 艺术家='{n0.Artist}' 专辑='{n0.Album}' 年={n0.Year} 轨={n0.Track}");
Check("文件2 原标签可读",
    !string.IsNullOrEmpty(n0.Title) && !string.IsNullOrEmpty(n0.Album) && n0.Track == 5,
    $"track={n0.Track}");
var propsN0 = ReadProps(normal);
Check("文件2 音频属性正常（DSD64/双声道/时长可读）",
    propsN0.sampleRate == 2822400 && propsN0.channels == 2 && (propsN0.durationMs ?? 0) > 20_000,
    $"rate={propsN0.sampleRate}, ch={propsN0.channels}, dur={propsN0.durationMs}ms");

string? hashNormalDsd = DffStructure.HashDsdChunk(normal);
bool nw = svc.WriteTagsAsync(normal, new TagData { Title = n0.Title + "-回归修改", Artist = n0.Artist })
    .GetAwaiter().GetResult();
Check("文件2 修改标签成功", nw);
var n1 = svc.ReadTagsAsync(normal).GetAwaiter().GetResult();
Check("文件2 修改后回读一致", n1.Title == n0.Title + "-回归修改", $"实际: {n1.Title}");
Check("文件2 修改后 DSD 音频块不变", hashNormalDsd == DffStructure.HashDsdChunk(normal));
var propsN1 = ReadProps(normal);
Check("文件2 修改后音频属性不变",
    propsN1.sampleRate == propsN0.sampleRate && propsN1.channels == propsN0.channels
    && Math.Abs((propsN1.durationMs ?? 0) - (propsN0.durationMs ?? 0)) < 100);

bool nc = svc.ClearTagsAsync(normal).GetAwaiter().GetResult();
var n2 = svc.ReadTagsAsync(normal).GetAwaiter().GetResult();
Check("文件2 清空标签成功且标题为空", nc && string.IsNullOrEmpty(n2.Title));
Check("文件2 清空后 DSD 音频块不变", hashNormalDsd == DffStructure.HashDsdChunk(normal));
Check("文件2 清空后音频属性不变", AudioPropsEqual(propsN0, ReadProps(normal)));

// ============================================================
// 汇总
// ============================================================
Console.WriteLine();
Console.WriteLine($"========== 验证汇总: {pass} 通过 / {fail} 失败 ==========");
Console.WriteLine($"(测试副本目录: {workDir}，原始下载文件未被修改)");
return fail == 0 ? 0 : 1;

// ============================================================
// 辅助
// ============================================================
static string? Short(string? s) => s == null ? null : s[..16] + "...";

(long? durationMs, int? sampleRate, int? channels) ReadProps(string path)
{
    // TagLibSharp2 0.6 的 DffFile.Properties 不回填音频属性，直接解析容器 PROP/DSD
    var p = DffStructure.AudioProps(path);
    return p == null ? (null, null, null) : (p.Value.durationMs, (int?)p.Value.sampleRate, (int?)p.Value.channels);
}

static bool AudioPropsEqual((long? durationMs, int? sampleRate, int? channels) a,
                            (long? durationMs, int? sampleRate, int? channels) b)
    => a.sampleRate == b.sampleRate && a.channels == b.channels
       && Math.Abs((a.durationMs ?? 0) - (b.durationMs ?? 0)) < 100;

static string Sha256WholeFile(string path)
{
    using var sha = SHA256.Create();
    using var fs = File.OpenRead(path);
    return Convert.ToHexString(sha.ComputeHash(fs));
}

// ============================================================
// 辅助类型（顶级语句规则：类型声明必须位于顶级语句之后）
// ============================================================
internal static class TestAssets
{
    // 1x1 透明 PNG
    public static readonly byte[] SamplePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
}

internal sealed class ConsoleLogger : ILoggerService
{
    public void Debug(string message) { }
    public void Debug(string format, params object?[] args) { }
    public void Info(string message) => Console.WriteLine("  [日志] " + message);
    public void Info(string format, params object?[] args) => Console.WriteLine("  [日志] " + format, args);
    public void Warn(string message) => Console.WriteLine("  [警告] " + message);
    public void Error(string message) => Console.WriteLine("  [错误] " + message);
    public void Error(Exception ex, string message) => Console.WriteLine($"  [错误] {message} :: {ex.Message}");
    public void Error(Exception ex, string format, params object?[] args)
        => Console.WriteLine($"  [错误] {string.Format(format, args)} :: {ex.Message}");
}

/// <summary>DSDIFF 顶层 chunk 结构检视（严格偶数边界遍历）。</summary>
internal static class DffStructure
{
    public sealed class Report
    {
        public bool Malformed;
        public long MalformedAt = -1;
        public bool FrmSizeConsistent;
        public string Diagram = "";
    }

    public static Report Inspect(string path)
    {
        var r = new Report();
        using var fs = File.OpenRead(path);
        long len = fs.Length;
        Span<byte> head16 = stackalloc byte[16];
        if (fs.Read(head16) != 16 || !"FRM8"u8.SequenceEqual(head16[..4]))
        {
            r.Malformed = true; r.MalformedAt = 0; return r;
        }
        ulong master = Be64(head16[4..]);
        r.FrmSizeConsistent = master == (ulong)(len - 12);
        Span<byte> h = stackalloc byte[12];

        var sb = new StringBuilder("FRM8/DSD");
        long pos = 16;
        while (pos + 12 <= len)
        {
            fs.Position = pos;
            if (fs.Read(h) != 12) { Mark(r, pos); break; }
            bool printable = true;
            foreach (var b in h[..4]) if (b < 0x20 || b > 0x7E) printable = false;
            if (!printable) { Mark(r, pos); break; }
            ulong size = Be64(h[4..]);
            long end = pos + 12 + (long)size + (long)(size & 1UL);
            if (size > (ulong)len || end > len) { Mark(r, pos); break; }
            sb.Append(" -> ").Append(Encoding.ASCII.GetString(h[..4])).Append('(').Append(size).Append(')');
            pos = end;
        }
        r.Diagram = sb.ToString();
        return r;

        static void Mark(Report rr, long at) { rr.Malformed = true; rr.MalformedAt = at; }
    }

    /// <summary>定位 DSD 音频块并对其数据区计算 SHA256（用于证明音频零损伤）。</summary>
    public static string? HashDsdChunk(string path)
    {
        using var fs = File.OpenRead(path);
        long len = fs.Length;
        Span<byte> h = stackalloc byte[12];
        long pos = 16;
        while (pos + 12 <= len)
        {
            fs.Position = pos;
            if (fs.Read(h) != 12) return null;
            string id = Encoding.ASCII.GetString(h[..4]);
            ulong size = Be64(h[4..]);
            if (id == "DSD ")
            {
                using var sha = SHA256.Create();
                long remain = (long)size;
                byte[] buf = new byte[1 << 20];
                while (remain > 0)
                {
                    int take = (int)Math.Min(buf.Length, remain);
                    int read = fs.Read(buf, 0, take);
                    if (read <= 0) return null;
                    sha.TransformBlock(buf, 0, read, null, 0);
                    remain -= read;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Convert.ToHexString(sha.Hash!);
            }
            pos += 12 + (long)size + (long)(size & 1UL);
        }
        return null;
    }

    /// <summary>
    /// 直接解析 PROP(FS/CHNL) 与 DSD 音频块，得出采样率/声道/时长。
    /// TagLibSharp2 0.6 的 DffFile.Properties 不回填这些字段，因此验证不依赖它。
    /// </summary>
    public static (long sampleRate, int channels, long durationMs)? AudioProps(string path)
    {
        using var fs = File.OpenRead(path);
        long len = fs.Length;
        Span<byte> h = stackalloc byte[12];
        long pos = 16;
        long rate = 0; int channels = 0; long dsdBytes = 0;
        while (pos + 12 <= len)
        {
            fs.Position = pos;
            if (fs.Read(h) != 12) break;
            string id = Encoding.ASCII.GetString(h[..4]);
            ulong size = Be64(h[4..]);
            long end = pos + 12 + (long)size + (long)(size & 1UL);
            if (size > (ulong)len || end > len || end < pos) break; // 畸形 chunk（如错位 ID3）：停止遍历

            if (id == "DSD ") dsdBytes = (long)size;

            if (id == "PROP" && size >= 4)
            {
                byte[] prop = new byte[size];
                fs.ReadExactly(prop);
                // form type(4) + 子 chunk（ID4 + BE64 size + data + 奇偶 pad）
                int p = 4;
                while (p + 12 <= prop.Length)
                {
                    string cid = Encoding.ASCII.GetString(prop, p, 4);
                    ulong csize = Be64(prop.AsSpan(p + 4, 8));
                    int dp = p + 12;
                    if (cid == "FS  " && csize >= 4)
                        rate = csize >= 8 ? (long)Be64(prop.AsSpan(dp, 8))
                                          : (long)((prop[dp] << 24) | (prop[dp + 1] << 16) | (prop[dp + 2] << 8) | prop[dp + 3]);
                    else if (cid == "CHNL" && csize >= 2)
                        channels = (prop[dp] << 8) | prop[dp + 1];
                    p = dp + (int)csize + (int)(csize & 1UL);
                }
            }

            pos = end;
        }
        if (rate <= 0 || channels <= 0 || dsdBytes <= 0) return null;
        long durationMs = dsdBytes * 1000 / rate / channels;
        return (rate, channels, durationMs);
    }

    private static ulong Be64(ReadOnlySpan<byte> b)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[i];
        return v;
    }
}
