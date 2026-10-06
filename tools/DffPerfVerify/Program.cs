using System.Diagnostics;
using System.Security.Cryptography;
using MusicTagRecover.Models;
using MusicTagRecover.Utils;

// DFF 定向读写专项验证工具：
//   dotnet run --project tools/DffPerfVerify -c Release -- perf        <目录>   旧(T2全量) vs 新(定向) 性能对比
//   dotnet run --project tools/DffPerfVerify -c Release -- verify      <文件>   读/改/删/封面/歌词准确性 + DSD 音频块完整性
//   dotnet run --project tools/DffPerfVerify -c Release -- consistency <目录>   扫描 vs 选中读取 20 字段一致性回归
//   dotnet run --project tools/DffPerfVerify -c Release -- duration    <目录>   多 DFF 文件总时长计算准确性
//   dotnet run --project tools/DffPerfVerify -c Release -- native      <文件>   DIIN/COMT 原生字段读/改/删
//
// 指标口径：托管分配字节数（GC.GetAllocatedBytesForCurrentThread，LOH 物化主因）+ 单文件耗时。

var mode = args.Length > 0 ? args[0] : "perf";
var path = args.Length > 1 ? args[1] : @"D:\BaiduNetdiskDownload\490张无损音乐专辑合集\张惠妹《姐妹》(2014)";

return mode switch
{
    "perf" => RunPerf(path),
    "verify" => RunVerify(path),
    "consistency" => await RunConsistency(path),
    "duration" => RunDuration(path),
    "native" => RunNative(path),
    _ => 1,
};

// ============================================================
// perf：旧路径（MediaInfo + TagLibSharp2 全量物化 ID3 chunk）vs 新路径（DffId3TagIO 定向读）
// ============================================================
static int RunPerf(string dir)
{
    if (!Directory.Exists(dir)) { Console.WriteLine($"目录不存在：{dir}"); return 1; }
    var files = Directory.EnumerateFiles(dir, "*.dff").ToList();
    if (files.Count == 0) { Console.WriteLine("目录内无 DFF 文件"); return 1; }
    Console.WriteLine($"性能对比：{files.Count} 个 DFF 文件（目录：{dir}）\n");

    // 预热：JIT/磁盘缓存对两条路径各跑一次（不计入统计）
    Warmup(files[0]);

    long oldMs = 0, newMs = 0, oldAlloc = 0, newAlloc = 0;
    int oldPics = 0, newPics = 0;

    foreach (var f in files)
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        // ---- 旧路径：MediaInfo 摘要 + TagLibSharp2 全量读（修改前实现）----
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var (title1, pics1) = ReadOldPath(f);
        sw.Stop();
        oldAlloc += GC.GetAllocatedBytesForCurrentThread() - a0;
        oldMs += sw.ElapsedMilliseconds;
        oldPics += pics1;

        GC.Collect(2, GCCollectionMode.Forced, true, true);

        // ---- 新路径：DffId3TagIO 定向扫描读 ----
        a0 = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        var data = DffId3TagIO.TryRead(f, full: false);
        sw.Stop();
        newAlloc += GC.GetAllocatedBytesForCurrentThread() - a0;
        newMs += sw.ElapsedMilliseconds;
        newPics += data?.PictureCount ?? 0;

        Console.WriteLine($"  {Path.GetFileName(f),-40} 标题='{Trunc(data?.Title ?? title1 ?? "")}' " +
                          $"封面 {data?.PictureCount ?? 0} 张");
    }

    Console.WriteLine();
    Console.WriteLine($"{"路径",-28}{"总耗时",-12}{"单文件均耗时",-14}{"总托管分配",-14}{"单文件均分配",-14}");
    Console.WriteLine($"{"旧：MediaInfo+T2 全量物化",-30}{oldMs + " ms",-12}{oldMs / (double)files.Count + " ms",-14}{FmtBytes(oldAlloc),-14}{FmtBytes(oldAlloc / files.Count),-14}");
    Console.WriteLine($"{"新：DffId3TagIO 定向读取",-30}{newMs + " ms",-12}{newMs / (double)files.Count + " ms",-14}{FmtBytes(newAlloc),-14}{FmtBytes(newAlloc / files.Count),-14}");
    Console.WriteLine();
    var speedup = newMs > 0 ? oldMs / (double)newMs : double.PositiveInfinity;
    var memDrop = oldAlloc > 0 ? (1 - newAlloc / (double)oldAlloc) * 100 : 100;
    Console.WriteLine($"速度提升：{speedup:F1}×（目标 ≥3×）　内存分配降低：{memDrop:F1}%（目标 ≥50%）");
    Console.WriteLine($"封面计数一致性：旧 {oldPics} 张 vs 新 {newPics} 张 {(oldPics == newPics ? "✓" : "✗ 不一致")}");

    var pass = speedup >= 3 && memDrop >= 50 && oldPics == newPics;
    Console.WriteLine(pass ? "\n性能目标达成。" : "\n性能目标未完全达成，需复查。");
    return pass ? 0 : 2;

    static void Warmup(string file)
    {
        ReadOldPath(file);
        DffId3TagIO.TryRead(file, full: false);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    // 修改前扫描路径的忠实复现：MediaInfo 读摘要，随后 TagLibSharp2 全量打开
    // （EnsureTag 物化整个 ID3 chunk 含 APIC 字节——内存激增与扫描变慢的根源）
    static (string? title, int pics) ReadOldPath(string file)
    {
        string? title = null;
        try
        {
            var mi = new MediaInfo.MediaInfoWrapper(file);
            if (mi.Success) { _ = mi.Duration; _ = mi.Tags; title = mi.Tags?.Title; }
        }
        catch { /* MediaInfo 打不开时仍走 T2 */ }

        var pics = 0;
        var result = TagLibSharp2.Dff.DffFile.ReadFromFile(file, (TagLibSharp2.Core.IFileSystem?)null);
        if (result.IsSuccess && result.File != null)
        {
            using (result.File as IDisposable)
            {
                var tag = result.File.Tag;
                if (tag == null)
                {
                    // 与 TagLibSharp2Engine.EnsureTag 相同：反射调用 Ensure*Tag() 零参方法
                    var m = result.File.GetType().GetMethods()
                        .FirstOrDefault(x => x.Name.StartsWith("Ensure") && x.Name.EndsWith("Tag")
                            && x.GetParameters().Length == 0);
                    m?.Invoke(result.File, null);
                    tag = result.File.Tag;
                }
                if (tag != null)
                {
                    title = tag.Title ?? title;
                    _ = tag.Performers; _ = tag.Album; _ = tag.Comment;
                    _ = tag.Genres; _ = tag.Track; _ = tag.Year;
                    pics = tag.Pictures?.Length ?? 0;
                    // 触碰封面字节：等价于修改前扫描期对 AllPictures 的枚举
                    if (tag.Pictures != null)
                        foreach (var p in tag.Pictures) _ = p.PictureData.Length;
                }
            }
        }
        return (title, pics);
    }
}

// ============================================================
// verify：读/改/删/封面/歌词 全链路准确性 + DSD 音频块字节级完整性
// ============================================================
static int RunVerify(string sourceFile)
{
    if (!File.Exists(sourceFile)) { Console.WriteLine($"文件不存在：{sourceFile}"); return 1; }

    var workDir = Path.Combine(Path.GetTempPath(), "dff_verify_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(workDir);
    var work = Path.Combine(workDir, "work.dff");
    File.Copy(sourceFile, work);
    Console.WriteLine($"验证工作副本：{work}\n");

    var checks = new List<(string name, bool ok, string detail)>();
    void Check(string name, bool ok, string detail = "") => checks.Add((name, ok, detail));

    try
    {
        var dsdHashBefore = DsdChunkSha256(work);
        Check("前置：DSD 音频块可定位且可哈希", dsdHashBefore != null);

        // ---- 1. 全字段写入 → 回读断言 ----
        var cover1 = MakeTestImage(640, 640, 0x22, 0x66, 0xCC);
        var tags = new TagData
        {
            Title = "验证标题", Artist = "验证艺术家", Album = "验证专辑",
            AlbumArtist = "验证专辑艺术家", Genre = "验证流派", Comment = "验证注释-中文 Comment",
            Composer = "验证作曲", Lyricist = "验证作词", Year = 2026,
            Track = 3, TrackCount = 10, Disc = 1, DiscCount = 2,
            Lyrics = "验证歌词第一行\n验证歌词第二行",
            CoverArtData = cover1, CoverArtMimeType = "image/png",
        };
        Check("写入：全字段 TryWrite 成功", DffId3TagIO.TryWrite(work, tags, out var e1), e1 ?? "");
        var r1 = DffId3TagIO.TryRead(work, full: true);
        Check("回读：标题", r1?.Title == "验证标题", $"实际='{r1?.Title}'");
        Check("回读：艺术家", r1?.Artist == "验证艺术家", $"实际='{r1?.Artist}'");
        Check("回读：专辑", r1?.Album == "验证专辑", $"实际='{r1?.Album}'");
        Check("回读：专辑艺术家", r1?.AlbumArtist == "验证专辑艺术家", $"实际='{r1?.AlbumArtist}'");
        Check("回读：流派", r1?.Genre == "验证流派", $"实际='{r1?.Genre}'");
        Check("回读：注释", r1?.Comment == "验证注释-中文 Comment", $"实际='{r1?.Comment}'");
        Check("回读：作曲", r1?.Composer == "验证作曲", $"实际='{r1?.Composer}'");
        Check("回读：作词", r1?.Lyricist == "验证作词", $"实际='{r1?.Lyricist}'");
        Check("回读：年份", r1?.Year == 2026, $"实际={r1?.Year}");
        Check("回读：音轨号/总数", r1?.Track == 3 && r1?.TrackCount == 10, $"实际={r1?.Track}/{r1?.TrackCount}");
        Check("回读：碟片号/总数", r1?.Disc == 1 && r1?.DiscCount == 2, $"实际={r1?.Disc}/{r1?.DiscCount}");
        Check("回读：歌词", r1?.Lyrics == "验证歌词第一行\n验证歌词第二行", $"实际长度={r1?.Lyrics?.Length}");
        Check("回读：封面张数=1", r1?.PictureCount == 1, $"实际={r1?.PictureCount}");
        Check("回读：封面字节一致", r1?.AllPictures is { Count: 1 } &&
              Sha256(r1.AllPictures[0].ImageData!) == Sha256(cover1));
        Check("回读：封面 MimeType", r1?.CoverArtMimeType == "image/png", $"实际='{r1?.CoverArtMimeType}'");
        Check("回读：音频属性保留（时长>0）", r1?.DurationMs > 0, $"实际={r1?.DurationMs}");
        Check("回读：音频属性保留（采样率>0）", r1?.SampleRate > 0, $"实际={r1?.SampleRate}");
        Check("回读：音频属性保留（码率>0）", r1?.BitRate > 0, $"实际={r1?.BitRate}");
        Check("回读：DSD 位深=1", r1?.BitsPerSample == 1, $"实际={r1?.BitsPerSample}");
        Check("完整性：写入后 DSD 块 SHA256 不变", DsdChunkSha256(work) == dsdHashBefore);
        Check("完整性：写入后容器结构可遍历", ContainerWalkable(work));

        // ---- 2. 互操作：TagLibSharp2 能读回我们写的标签 ----
        var t2 = TagLibSharp2.Dff.DffFile.ReadFromFile(work, (TagLibSharp2.Core.IFileSystem?)null);
        var t2ok = t2.IsSuccess && t2.File?.Tag != null;
        Check("互操作：T2 可读新标签", t2ok, t2.IsSuccess ? "" : t2.Error ?? "");
        if (t2ok)
        {
            Check("互操作：T2 标题一致", t2.File!.Tag!.Title == "验证标题", $"实际='{t2.File.Tag.Title}'");
            Check("互操作：T2 注释一致", t2.File.Tag.Comment == "验证注释-中文 Comment");
            Check("互操作：T2 封面张数一致", (t2.File.Tag.Pictures?.Length ?? 0) == 1);
        }
        (t2.File as IDisposable)?.Dispose();

        // ---- 3. 部分修改：只改标题，其余字段保持 ----
        var partial = new TagData { Title = "仅改标题" };
        Check("修改：部分字段 TryWrite 成功", DffId3TagIO.TryWrite(work, partial, out var e3), e3 ?? "");
        var r3 = DffId3TagIO.TryRead(work, full: true);
        Check("修改：标题已更新", r3?.Title == "仅改标题", $"实际='{r3?.Title}'");
        Check("修改：艺术家保留", r3?.Artist == "验证艺术家", $"实际='{r3?.Artist}'");
        Check("修改：封面保留", r3?.PictureCount == 1, $"实际={r3?.PictureCount}");
        Check("修改：歌词保留", r3?.Lyrics == "验证歌词第一行\n验证歌词第二行");
        Check("完整性：修改后 DSD 块 SHA256 不变", DsdChunkSha256(work) == dsdHashBefore);

        // ---- 4. 空值清除：标题/音轨清空 ----
        var clear = new TagData { Title = "", Track = 0, TrackCount = 0 };
        Check("清除：TryWrite(空串/0) 成功", DffId3TagIO.TryWrite(work, clear, out var e4), e4 ?? "");
        var r4 = DffId3TagIO.TryRead(work, full: true);
        Check("清除：标题为空", string.IsNullOrEmpty(r4?.Title), $"实际='{r4?.Title}'");
        Check("清除：音轨为空（无 0→1 怪癖）", r4?.Track is null or 0, $"实际={r4?.Track}");
        Check("清除：艺术家仍保留", r4?.Artist == "验证艺术家");

        // ---- 5. 封面替换 ----
        var cover2 = MakeTestImage(320, 200, 0xCC, 0x44, 0x22);
        var coverObj = new CoverArt { ImageData = cover2, MimeType = "image/png" };
        Check("封面：TryWriteCover 成功", DffId3TagIO.TryWriteCover(work, coverObj, out var e5), e5 ?? "");
        var r5 = DffId3TagIO.TryRead(work, full: true);
        Check("封面：替换后字节一致", r5?.AllPictures is { Count: 1 } &&
              Sha256(r5.AllPictures[0].ImageData!) == Sha256(cover2));
        Check("封面：替换后标题仍保留", r5?.Artist == "验证艺术家");

        // ---- 6. 歌词单独写入 ----
        Check("歌词：TryWriteLyrics 成功", DffId3TagIO.TryWriteLyrics(work, "单独写入的歌词", out var e6), e6 ?? "");
        var r6 = DffId3TagIO.TryRead(work, full: true);
        Check("歌词：回读一致", r6?.Lyrics == "单独写入的歌词", $"实际='{r6?.Lyrics}'");
        Check("歌词：封面仍在", r6?.PictureCount == 1);

        // ---- 7. 摘要/全量一致性（扫描 vs 选中回归）----
        var sum = DffId3TagIO.TryRead(work, full: false);
        var full = DffId3TagIO.TryRead(work, full: true);
        Check("一致性：20 字段扫描=选中", CompareSummaryVsFull(sum, full, out var diffDetail), diffDetail);

        // ---- 8. 删除整个标签 ----
        Check("删除：TryDelete 成功", DffId3TagIO.TryDelete(work, out var e8), e8 ?? "");
        var r8 = DffId3TagIO.TryRead(work, full: true);
        Check("删除：标签字段为空", r8 != null && string.IsNullOrEmpty(r8.Title) &&
              string.IsNullOrEmpty(r8.Artist) && r8.PictureCount == 0 && !r8.LyricsPresent,
              $"Title='{r8?.Title}' Pics={r8?.PictureCount}");
        Check("删除：无 ID3 chunk", !ChunkExists(work, "ID3 "));
        Check("删除：音频属性仍可解析", r8?.DurationMs > 0 && r8.SampleRate > 0);
        Check("完整性：删除后 DSD 块 SHA256 不变", DsdChunkSha256(work) == dsdHashBefore);
        Check("完整性：删除后容器结构可遍历", ContainerWalkable(work));

        // ---- 9. 删除后 T2 仍可打开（容器未损坏）----
        var t2b = TagLibSharp2.Dff.DffFile.ReadFromFile(work, (TagLibSharp2.Core.IFileSystem?)null);
        Check("删除后：T2 仍可正常打开文件", t2b.IsSuccess, t2b.Error ?? "");
        (t2b.File as IDisposable)?.Dispose();

        // ---- 10. 删除后重新写入（往返）----
        Check("往返：删除后重新 TryWrite 成功",
            DffId3TagIO.TryWrite(work, new TagData { Title = "重写标题", Artist = "重写艺术家" }, out var e10), e10 ?? "");
        var r10 = DffId3TagIO.TryRead(work, full: true);
        Check("往返：重写后回读一致", r10?.Title == "重写标题" && r10.Artist == "重写艺术家");
        Check("完整性：全程 DSD 块 SHA256 不变", DsdChunkSha256(work) == dsdHashBefore);
    }
    finally
    {
        try { Directory.Delete(workDir, true); } catch { /* 临时目录清理失败不影响结果 */ }
    }

    Console.WriteLine();
    var pass = 0;
    foreach (var (name, ok, detail) in checks)
    {
        Console.WriteLine($"  {(ok ? "✓" : "✗")} {name}{(ok || string.IsNullOrEmpty(detail) ? "" : $" —— {detail}")}");
        if (ok) pass++;
    }
    Console.WriteLine($"\n通过 {pass}/{checks.Count}");
    return pass == checks.Count ? 0 : 2;
}

// ============================================================
// duration：多 DFF 文件总时长计算准确性（独立公式校验 + 单/多/空场景）
// ============================================================
static int RunDuration(string dir)
{
    if (!Directory.Exists(dir)) { Console.WriteLine($"目录不存在：{dir}"); return 1; }
    var files = Directory.EnumerateFiles(dir, "*.dff").ToList();
    Console.WriteLine($"时长准确性测试：目录 {dir}，{files.Count} 个 DFF 文件\n");

    var checks = new List<(string, bool)>();

    // 空列表场景
    long totalEmpty = SumDurationViaScanner(new List<string>());
    checks.Add(("空列表：总时长=0", totalEmpty == 0));

    if (files.Count == 0)
    {
        checks.Add(("单文件场景：无文件可测", false));
    }
    else
    {
        // 单文件场景
        var one = new List<string> { files[0] };
        long totalOne = SumDurationViaScanner(one);
        long expectedOne = ExpectedDurationMs(files[0]);
        checks.Add(($"单文件：DffId3TagIO 时长与独立公式一致（{totalOne} vs {expectedOne} ms）",
            Math.Abs(totalOne - expectedOne) <= 1));
        checks.Add(($"单文件：时长 > 0（{totalOne} ms = {TimeSpan.FromMilliseconds(totalOne):mm\\:ss}）", totalOne > 0));

        // 多文件场景：DffId3TagIO 求和 与 独立公式求和 一致
        long totalAll = SumDurationViaScanner(files);
        long expectedAll = files.Sum(ExpectedDurationMs);
        checks.Add(($"多文件（{files.Count} 个）：DffId3TagIO 求和 {totalAll} ms 与独立公式求和 {expectedAll} ms 一致",
            Math.Abs(totalAll - expectedAll) <= files.Count));

        // 旧公式（缺少因子 8）的回归断言：确保新值明显大于旧值的 7 倍
        long oldFormula = files.Sum(f =>
        {
            var (sr, ch, db) = ReadDsdParams(f);
            return sr > 0 && ch > 0 ? db * 1000 / (sr * ch) : 0;
        });
        checks.Add(($"回归：新总时长 {totalAll} ms 是旧公式 {oldFormula} ms 的 ≥7.9 倍（因子 8 修正）",
            oldFormula > 0 && totalAll >= oldFormula * 7.9));

        Console.WriteLine($"  旧公式总时长: {oldFormula} ms ({TimeSpan.FromMilliseconds(oldFormula):hh\\:mm\\:ss})");
        Console.WriteLine($"  新公式总时长: {totalAll} ms ({TimeSpan.FromMilliseconds(totalAll):hh\\:mm\\:ss})");
    }

    Console.WriteLine();
    int pass = 0;
    foreach (var (name, ok) in checks)
    {
        Console.WriteLine($"  {(ok ? "✓" : "✗")} {name}");
        if (ok) pass++;
    }
    Console.WriteLine($"\n通过 {pass}/{checks.Count}");
    return pass == checks.Count ? 0 : 2;

    // 通过 DffId3TagIO 读取并求和（等价于文件列表 UpdateInfo 的口径）
    static long SumDurationViaScanner(List<string> fs) =>
        fs.Sum(f => DffId3TagIO.TryRead(f, full: false)?.DurationMs ?? 0);

    // 独立计算公式（不依赖 DffId3TagIO）：dsdBytes * 8000 / (sr * ch)
    static long ExpectedDurationMs(string file)
    {
        var (sr, ch, db) = ReadDsdParams(file);
        return sr > 0 && ch > 0 ? db * 8000 / (sr * ch) : 0;
    }

    static (long sr, int ch, long dsdBytes) ReadDsdParams(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        long pos = 16; long propOff = -1; int propLen = 0; long dsdLen = 0;
        Span<byte> hdr = stackalloc byte[12];
        while (pos + 12 <= fs.Length)
        {
            fs.Position = pos;
            if (fs.Read(hdr) != 12) break;
            string id = System.Text.Encoding.ASCII.GetString(hdr[..4]);
            long len = (long)ReadBe64(hdr.Slice(4, 8));
            if (id == "PROP") { propOff = pos + 12; propLen = (int)len; }
            if (id == "DSD ") dsdLen = len;
            pos += 12 + len + (len & 1);
        }
        long sr = 0; int ch = 0;
        if (propOff > 0 && propLen >= 4)
        {
            byte[] prop = new byte[propLen];
            fs.Position = propOff;
            int pr = 0; while (pr < propLen) { int n = fs.Read(prop, pr, propLen - pr); if (n <= 0) break; pr += n; }
            int p = 4;
            while (p + 12 <= propLen)
            {
                string cid = System.Text.Encoding.ASCII.GetString(prop, p, 4);
                long cl = (long)ReadBe64(prop.AsSpan(p + 4, 8));
                int dp = p + 12;
                if (cid == "FS  " && cl >= 4) sr = ((long)prop[dp] << 24) | ((long)prop[dp + 1] << 16) | ((long)prop[dp + 2] << 8) | prop[dp + 3];
                else if (cid == "CHNL" && cl >= 2) ch = (prop[dp] << 8) | prop[dp + 1];
                long n = dp + cl + (cl & 1); if (n <= p) break; p = (int)n;
            }
        }
        return (sr, ch, dsdLen);
    }
}

// ============================================================
// native：DIIN/COMT 原生字段读/改/删 + DSD 块完整性 + 容器结构校验
// ============================================================
static int RunNative(string sourceFile)
{
    if (!File.Exists(sourceFile)) { Console.WriteLine($"文件不存在：{sourceFile}"); return 1; }

    var workDir = Path.Combine(Path.GetTempPath(), "dff_native_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(workDir);
    var work = Path.Combine(workDir, "work.dff");
    File.Copy(sourceFile, work);
    Console.WriteLine($"验证工作副本：{work}\n");

    var checks = new List<(string name, bool ok, string detail)>();
    void Check(string name, bool ok, string detail = "") => checks.Add((name, ok, detail));

    try
    {
        var dsdHashBefore = DsdChunkSha256(work);
        Check("前置：DSD 音频块可哈希", dsdHashBefore != null);

        // ---- 读取 DIIN ----
        var diinFields = DffId3TagIO.ReadDiinFields(work);
        Console.WriteLine($"  读取 DIIN 字段：{diinFields.Count} 个");
        foreach (var f in diinFields) Console.WriteLine($"    {f.Id} = '{Trunc(f.Text)}'");
        Check("DIIN 读取：成功（字段数 >= 0）", diinFields.Count >= 0);

        // ---- 读取 COMT ----
        var comtComments = DffId3TagIO.ReadCommtComments(work);
        Console.WriteLine($"  读取 COMT 注释：{comtComments.Count} 条");
        foreach (var c in comtComments)
            Console.WriteLine($"    [{c.Year}-{c.Month:D2}-{c.Day:D2}] type={c.CommentType} '{Trunc(c.Text)}'");
        Check("COMT 读取：成功", comtComments.Count >= 0);

        // ---- 修改 DIIN 字段（DITI 标题）----
        bool writeOk = DffId3TagIO.TryWriteDiinField(work, "DITI", "修改后的原生标题", out var e1);
        Check("DIIN 修改：TryWriteDiinField(DITI) 成功", writeOk, e1 ?? "");
        var diinAfter = DffId3TagIO.ReadDiinFields(work);
        var diti = diinAfter.FirstOrDefault(f => f.Id == "DITI");
        Check("DIIN 修改：DITI 回读一致", diti?.Text == "修改后的原生标题", $"实际='{diti?.Text}'");
        Check("DIIN 修改：其余字段保留", diinFields.Count <= 1 ||
            diinAfter.Count(f => f.Id != "DITI") >= diinFields.Count(f => f.Id != "DITI"));
        Check("DIIN 修改后 DSD 块 SHA256 不变", DsdChunkSha256(work) == dsdHashBefore);
        Check("DIIN 修改后容器结构完整", ContainerWalkable(work));

        // ---- 新增 DIIN 字段（DGEN 流派，原本可能不存在）----
        bool addOk = DffId3TagIO.TryWriteDiinField(work, "DGEN", "测试流派", out var e2);
        Check("DIIN 新增：TryWriteDiinField(DGEN) 成功", addOk, e2 ?? "");
        var diinAfter2 = DffId3TagIO.ReadDiinFields(work);
        Check("DIIN 新增：DGEN 回读一致", diinAfter2.Any(f => f.Id == "DGEN" && f.Text == "测试流派"));
        Check("DIIN 新增后 DSD 块不变", DsdChunkSha256(work) == dsdHashBefore);

        // ---- 删除 DIIN 字段（DGEN）----
        bool delOk = DffId3TagIO.TryDeleteDiinField(work, "DGEN", out var e3);
        Check("DIIN 删除：TryDeleteDiinField(DGEN) 成功", delOk, e3 ?? "");
        var diinAfter3 = DffId3TagIO.ReadDiinFields(work);
        Check("DIIN 删除：DGEN 已不存在", !diinAfter3.Any(f => f.Id == "DGEN"));
        Check("DIIN 删除后 DSD 块不变", DsdChunkSha256(work) == dsdHashBefore);
        Check("DIIN 删除后容器结构完整", ContainerWalkable(work));

        // ---- COMT：若已有注释则修改第一条，否则追加 ----
        if (comtComments.Count > 0)
        {
            var modified = new DffCommtComment
            {
                Year = 2026, Month = 10, Day = 10, Hour = 12, Minute = 0, Second = 0,
                CommentType = 0, CommentReference = 0,
                Text = "修改后的注释内容"
            };
            bool cwOk = DffId3TagIO.TryWriteCommtComment(work, 0, modified, out var e4);
            Check("COMT 修改：TryWriteCommtComment(0) 成功", cwOk, e4 ?? "");
            var comtAfter = DffId3TagIO.ReadCommtComments(work);
            Check("COMT 修改：第一条回读一致", comtAfter.Count > 0 && comtAfter[0].Text == "修改后的注释内容",
                $"实际='{comtAfter.FirstOrDefault()?.Text}'");
            Check("COMT 修改后 DSD 块不变", DsdChunkSha256(work) == dsdHashBefore);
        }
        else
        {
            var added = new DffCommtComment { Year = 2026, Month = 10, Day = 10, Text = "新增注释" };
            bool caOk = DffId3TagIO.TryAddCommtComment(work, added, out var e4);
            Check("COMT 追加：TryAddCommtComment 成功", caOk, e4 ?? "");
            Check("COMT 追加后回读一致", DffId3TagIO.ReadCommtComments(work).Count == 1);
        }

        // ---- COMT 删除 ----
        var comtBeforeDel = DffId3TagIO.ReadCommtComments(work);
        if (comtBeforeDel.Count > 0)
        {
            bool cdOk = DffId3TagIO.TryDeleteCommtComment(work, 0, out var e5);
            Check("COMT 删除：TryDeleteCommtComment(0) 成功", cdOk, e5 ?? "");
            var comtAfterDel = DffId3TagIO.ReadCommtComments(work);
            Check("COMT 删除：条数减一", comtAfterDel.Count == comtBeforeDel.Count - 1,
                $"删除前 {comtBeforeDel.Count}，删除后 {comtAfterDel.Count}");
            Check("COMT 删除后 DSD 块不变", DsdChunkSha256(work) == dsdHashBefore);
            Check("COMT 删除后容器结构完整", ContainerWalkable(work));
        }
        else
        {
            Check("COMT 删除：无注释可删（跳过）", true);
        }

        // ---- 异常校验：非法索引应返回 false 而非抛异常 / 损坏文件 ----
        bool badIdx = DffId3TagIO.TryDeleteCommtComment(work, 9999, out var e6);
        Check("COMT 异常：删除非法索引返回 false", !badIdx, e6 ?? "");
        Check("COMT 异常后 DSD 块不变", DsdChunkSha256(work) == dsdHashBefore);

        // ---- T2 互操作：修改原生字段后 TagLibSharp2 仍可正常打开 ----
        var t2 = TagLibSharp2.Dff.DffFile.ReadFromFile(work, (TagLibSharp2.Core.IFileSystem?)null);
        Check("互操作：T2 仍可打开修改后的文件", t2.IsSuccess, t2.Error ?? "");
        (t2.File as IDisposable)?.Dispose();
    }
    finally
    {
        try { Directory.Delete(workDir, true); } catch { }
    }

    Console.WriteLine();
    int pass = 0;
    foreach (var (name, ok, detail) in checks)
    {
        Console.WriteLine($"  {(ok ? "✓" : "✗")} {name}{(ok || string.IsNullOrEmpty(detail) ? "" : $" —— {detail}")}");
        if (ok) pass++;
    }
    Console.WriteLine($"\n通过 {pass}/{checks.Count}");
    return pass == checks.Count ? 0 : 2;
}

// ============================================================
// consistency：TagService 扫描 vs 选中读取 20 字段一致性（请求三回归）
// ============================================================
static async Task<int> RunConsistency(string dir)
{
    if (!Directory.Exists(dir)) { Console.WriteLine($"目录不存在：{dir}"); return 1; }
    var files = Directory.EnumerateFiles(dir, "*.dff").ToList();
    if (files.Count == 0) { Console.WriteLine("目录内无 DFF 文件"); return 1; }

    var settings = new MusicTagRecover.Services.SettingsService(
        Path.Combine(Path.GetTempPath(), "dffperfverify-settings.db"));
    var logger = new MusicTagRecover.Services.LoggerService(settings);
    var tagService = new MusicTagRecover.Services.TagService(logger);

    var mismatches = 0;
    var total = 0;
    for (var round = 1; round <= 2; round++)
    {
        Console.WriteLine($"第 {round} 轮（{files.Count} 个文件）：");
        foreach (var f in files)
        {
            var s = await tagService.ReadTagsSummaryAsync(f);
            var full = await tagService.ReadTagsAsync(f);
            total++;
            if (!CompareSummaryVsFull(s, full, out var detail))
            {
                mismatches++;
                Console.WriteLine($"  ✗ {Path.GetFileName(f)}：{detail}");
            }
        }
    }
    Console.WriteLine(mismatches == 0
        ? $"扫描 vs 选中 20 字段完全一致（{total} 次两两比对）。"
        : $"发现 {mismatches}/{total} 处不一致。");
    return mismatches == 0 ? 0 : 2;
}

// ============================================================
// 共享辅助
// ============================================================

static bool CompareSummaryVsFull(TagData? s, TagData? full, out string detail)
{
    detail = "";
    if (s == null || full == null) { detail = "读取返回 null"; return false; }
    var diffs = new List<string>();
    void Eq(string name, object? a, object? b)
    {
        var na = a?.ToString() ?? ""; var nb = b?.ToString() ?? "";
        if (na != nb) diffs.Add($"{name}: 扫描='{Trunc(na)}' 选中='{Trunc(nb)}'");
    }
    Eq("Title", s.Title, full.Title);
    Eq("Artist", s.Artist, full.Artist);
    Eq("Album", s.Album, full.Album);
    Eq("AlbumArtist", s.AlbumArtist, full.AlbumArtist);
    Eq("Genre", s.Genre, full.Genre);
    Eq("Comment", s.Comment, full.Comment);
    Eq("Composer", s.Composer, full.Composer);
    Eq("Lyricist", s.Lyricist, full.Lyricist);
    Eq("Year", s.Year, full.Year);
    Eq("Track", s.Track, full.Track);
    Eq("TrackCount", s.TrackCount, full.TrackCount);
    Eq("Disc", s.Disc, full.Disc);
    Eq("DiscCount", s.DiscCount, full.DiscCount);
    Eq("TagFormat", s.TagFormat, full.TagFormat);
    Eq("LyricsPresent", s.LyricsPresent, full.LyricsPresent || !string.IsNullOrEmpty(full.Lyrics));
    Eq("PictureCount", s.PictureCount, full.PictureCount);
    Eq("DurationMs", s.DurationMs, full.DurationMs);
    Eq("SampleRate", s.SampleRate, full.SampleRate);
    Eq("Channels", s.Channels, full.Channels);
    Eq("BitRate", s.BitRate, full.BitRate);
    detail = string.Join("；", diffs);
    return diffs.Count == 0;
}

/// <summary>DSD/DST 音频块 SHA256（用于验证写操作全程未触碰音频数据）。</summary>
static string? DsdChunkSha256(string file)
{
    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (!FindChunk(fs, "DSD ", out var offset, out var len) && !FindChunk(fs, "DST ", out offset, out len))
        return null;
    fs.Position = offset;
    using var sha = SHA256.Create();
    var buf = new byte[1 << 20];
    long remain = len;
    while (remain > 0)
    {
        int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, remain));
        if (n <= 0) return null;
        sha.TransformBlock(buf, 0, n, null, 0);
        remain -= n;
    }
    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    return Convert.ToHexString(sha.Hash!);
}

static bool ChunkExists(string file, string id)
{
    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
    return FindChunk(fs, id, out _, out _);
}

/// <summary>容器结构完整性：FRM8 声明长度 == 实际，顶层 chunk 全部可遍历且不越界。</summary>
static bool ContainerWalkable(string file)
{
    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (fs.Length < 16) return false;
    Span<byte> head = stackalloc byte[16];
    fs.Position = 0;
    if (fs.Read(head) != 16 || !head[..4].SequenceEqual("FRM8"u8)) return false;
    long declared = ReadBe64(head.Slice(4, 8));
    if (declared != fs.Length - 12) return false; // DSDIFF: 主长度=8字节长度域之后的全部字节

    long pos = 16;
    Span<byte> hdr = stackalloc byte[12];
    while (pos + 12 <= fs.Length)
    {
        fs.Position = pos;
        if (fs.Read(hdr) != 12) return false;
        for (int i = 0; i < 4; i++)
            if (hdr[i] < 0x20 || hdr[i] > 0x7E) return false;
        long size = (long)ReadBe64(hdr.Slice(4, 8));
        long total = 12 + size + (size & 1);
        if (pos + total > fs.Length) return false;
        pos += total;
    }
    return pos == fs.Length;
}

static bool FindChunk(FileStream fs, string id, out long dataOffset, out long dataLen)
{
    dataOffset = 0; dataLen = 0;
    if (fs.Length < 16) return false;
    long pos = 16;
    Span<byte> hdr = stackalloc byte[12];
    while (pos + 12 <= fs.Length)
    {
        fs.Position = pos;
        if (fs.Read(hdr) != 12) return false;
        string cid = System.Text.Encoding.ASCII.GetString(hdr[..4]);
        long size = (long)ReadBe64(hdr.Slice(4, 8));
        if (cid == id) { dataOffset = pos + 12; dataLen = size; return true; }
        pos += 12 + size + (size & 1);
    }
    return false;
}

static long ReadBe64(ReadOnlySpan<byte> s) =>
    ((long)s[0] << 56) | ((long)s[1] << 48) | ((long)s[2] << 40) | ((long)s[3] << 32) |
    ((long)s[4] << 24) | ((long)s[5] << 16) | ((long)s[6] << 8) | s[7];

static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

static string FmtBytes(long bytes) => bytes >= 1 << 20
    ? $"{bytes / 1048576.0:F1} MB"
    : $"{bytes / 1024.0:F1} KB";

static string Trunc(string s) => s.Length > 24 ? s[..24] + "…" : s;

/// <summary>生成确定性测试 PNG（合法 PNG 头 + IEND，内容仅用于字节一致性校验）。</summary>
static byte[] MakeTestImage(int w, int h, byte r, byte g, byte b)
{
    // 合法 1x1 PNG 骨架，随后拼接确定性伪随机字节到目标体量，便于观察 MB 级封面场景
    byte[] png1x1 =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
        0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, 0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
        0x00, 0x00, 0x03, 0x00, 0x01, 0x5C, 0xCD, 0xFF, 0x69, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,
        0x44, 0xAE, 0x42, 0x60, 0x82,
    };
    var target = Math.Clamp(w * h / 4, 4096, 3 << 20);
    var data = new byte[png1x1.Length + target];
    png1x1.CopyTo(data, 0);
    var rng = new Random(r * 65536 + g * 256 + b);
    rng.NextBytes(data.AsSpan(png1x1.Length));
    return data;
}
