using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.Services;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

string path = args.Length > 0 ? args[0]
    : @"d:\BaiduNetdiskDownload\490张无损音乐专辑合集\精英国乐团 - 丝竹管弦 - 2001   DFF\01 - 精英国乐团 - 大浪淘沙(琵琶合奏).dff";

string workDir = Path.Combine(Path.GetTempPath(), "mtc_diag_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(workDir);
string probePath = Path.Combine(workDir, "probe.dff");
File.Copy(path, probePath, true);

var svc = new TagService(new NullLogger());
int pass = 0, fail = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"  {(ok ? "[PASS]" : "[FAIL]")} {name}{(detail == null ? "" : "  -> " + detail)}");
    if (ok) pass++; else fail++;
}

Console.WriteLine("=== DFF 扫描 vs 选中读取 一致性验证 ===");
Console.WriteLine($"  文件: {probePath} ({new FileInfo(probePath).Length} 字节)");
Console.WriteLine();

// 逐字段对比（摘要 vs 全量共有的字段）
static (string Name, string? A, string? B)[] Compare(TagData s, TagData f) => new (string, string?, string?)[]
{
    ("Title",       s.Title,                  f.Title),
    ("Artist",      s.Artist,                 f.Artist),
    ("Album",       s.Album,                  f.Album),
    ("AlbumArtist", s.AlbumArtist,            f.AlbumArtist),
    ("Genre",       s.Genre,                  f.Genre),
    ("Comment",     s.Comment,                f.Comment),
    ("Composer",    s.Composer,               f.Composer),
    ("Lyricist",    s.Lyricist,               f.Lyricist),
    ("Year",        s.Year?.ToString(),       f.Year?.ToString()),
    ("Track",       s.Track?.ToString(),      f.Track?.ToString()),
    ("TrackCount",  s.TrackCount?.ToString(), f.TrackCount?.ToString()),
    ("Disc",        s.Disc?.ToString(),       f.Disc?.ToString()),
    ("DiscCount",   s.DiscCount?.ToString(),  f.DiscCount?.ToString()),
    ("TagFormat",   s.TagFormat,              f.TagFormat),
    ("LyricsFlag",  s.LyricsPresent.ToString(), (f.Lyrics != null).ToString()),
    ("PicCount",    s.PictureCount.ToString(), f.PictureCount.ToString()),
    ("DurationMs",  s.DurationMs?.ToString(), f.DurationMs?.ToString()),
    ("SampleRate",  s.SampleRate?.ToString(), f.SampleRate?.ToString()),
    ("Channels",    s.Channels?.ToString(),   f.Channels?.ToString()),
    ("BitRate",     s.BitRate?.ToString(),    f.BitRate?.ToString()),
};

for (int round = 1; round <= 2; round++)
{
    Console.WriteLine($"--- 第 {round} 轮：扫描 + 选中读取 ---");
    var summary = svc.ReadTagsSummaryAsync(probePath).GetAwaiter().GetResult();
    var full = svc.ReadTagsAsync(probePath).GetAwaiter().GetResult();

    foreach (var (name, a, b) in Compare(summary, full))
    {
        // 时长允许毫秒级四舍五入差异
        bool ok = name == "DurationMs"
            ? Math.Abs((summary.DurationMs ?? 0) - (full.DurationMs ?? 0)) < 100
            : string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
        Check($"第{round}轮 {name} 一致", ok, ok ? a ?? "<空>" : $"扫描='{a ?? "<null>"}' ≠ 全量='{b ?? "<null>"}'");
    }
    Console.WriteLine();
}

// 关键字段值确认（针对本文件的已知正确值）
Console.WriteLine("--- 关键字段期望值确认 ---");
var s2 = svc.ReadTagsSummaryAsync(probePath).GetAwaiter().GetResult();
Check("扫描标题为 ID3v2 中文标题（非 DIIN 英文标题）", s2.Title == "大浪淘沙 (琵琶独奏)", s2.Title);
Check("扫描注释为空（ID3v2 无注释帧，而非 COMT 的 SACD 残留）", string.IsNullOrEmpty(s2.Comment), s2.Comment ?? "<空>");
Check("扫描音频属性保留（MediaInfo 提供时长/采样率）",
    s2.DurationMs > 10_000 && s2.SampleRate == 2822400 && s2.Channels == 2,
    $"dur={s2.DurationMs}ms rate={s2.SampleRate} ch={s2.Channels}");
Check("文件未被扫描修改（大小不变）", new FileInfo(probePath).Length == 196467536);

Console.WriteLine();
Console.WriteLine($"========== 汇总: {pass} 通过 / {fail} 失败 ==========");
return fail == 0 ? 0 : 1;

sealed class NullLogger : ILoggerService
{
    public void Debug(string _) { }
    public void Debug(string _, params object?[] __) { }
    public void Info(string _) { }
    public void Info(string _, params object?[] __) { }
    public void Warn(string _) { }
    public void Error(string _) { }
    public void Error(Exception _, string __) { }
    public void Error(Exception _, string __, params object?[] ___) { }
}
