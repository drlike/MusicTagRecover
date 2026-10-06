using System.Diagnostics;
using MusicTagRecover.Services;

// 目录扫描基准测试工具：
//   dotnet run --project tools/ScanBench -c Release -- "F:\新建文件夹" [模式]
// 模式：all(默认) | current（当前生产路径）| mediainfo（MediaInfo 基准）| verify（双引擎数据一致性抽查）
//
// 指标：完成时间、吞吐（文件/秒、MB/秒）、工作集峰值/均值增量、托管内存峰值增量。
// 采样线程每 100ms 记录一次工作集与托管堆，全程在进程内测量，结果即应用真实表现。

var dir = args.Length > 0 ? args[0] : @"F:\新建文件夹";
var mode = args.Length > 1 ? args[1] : "all";

if (mode is "api")
{
    // 低层绑定类成员：评估实例复用（Open/Close/Dispose）能力
    var t = typeof(MediaInfo.MediaInfo);
    foreach (var c in t.GetConstructors()) Console.WriteLine($"ctor({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
    foreach (var m in t.GetMethods().Where(m => m.DeclaringType == t && !m.IsSpecialName).OrderBy(m => m.Name))
        Console.WriteLine($"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
    Console.WriteLine($"IDisposable: {typeof(IDisposable).IsAssignableFrom(t)}");
    return 0;
}

if (mode is "api2")
{
    // 反射查看 MediaInfoWrapper / AudioTags 可用成员（供扫描映射设计）
    static void Dump(Type t)
    {
        Console.WriteLine($"== {t.Name} ==");
        foreach (var p in t.GetProperties()) Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
        foreach (var m in t.GetMethods().Where(m => m.DeclaringType == t && !m.IsSpecialName))
            Console.WriteLine($"  meth {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
        Console.WriteLine($"  IDisposable: {typeof(IDisposable).IsAssignableFrom(t)}");
        Console.WriteLine();
    }
    Dump(typeof(MediaInfo.MediaInfoWrapper));
    var tagsProp = typeof(MediaInfo.MediaInfoWrapper).GetProperty("Tags");
    if (tagsProp != null) Dump(tagsProp.PropertyType);

    // 用真实 FLAC 验证：Covers 内容、Text 中的 Lyrics/Cover 行、关键字段取值
    var sample = Directory.EnumerateFiles(@"F:\新建文件夹", "*.flac",
        new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).FirstOrDefault();
    if (sample != null)
    {
        Console.WriteLine($"== 样本 {Path.GetFileName(sample)} ==");
        var mi = new MediaInfo.MediaInfoWrapper(sample);
        Console.WriteLine($"Success={mi.Success} Duration={mi.Duration} AudioRate={mi.AudioRate} " +
                          $"SampleRate={mi.AudioSampleRate} Channels={mi.AudioChannels} Format='{mi.Format}' Version='{mi.Version}'");
        var t = mi.Tags;
        if (t != null)
        {
            Console.WriteLine($"Tags: Title='{t.Title}' Track='{t.Track}' Artist='{t.Artist}' Album='{t.Album}' " +
                              $"AlbumArtist='{t.AlbumArtist}' Composer='{t.Composer}' Lyricist='{t.Lyricist}' " +
                              $"Genre='{t.Genre}' Comment='{(t.Comment?.Length > 30 ? t.Comment[..30] : t.Comment)}' " +
                              $"TrackPos={t.TrackPosition} TotalTracks={t.TotalTracks} Disc={t.DiscNumber} " +
                              $"Recorded={t.RecordedDate} Released={t.ReleasedDate}");
            var covers = t.Covers?.ToList();
            Console.WriteLine($"Covers: count={covers?.Count ?? -1}");
            if (covers is { Count: > 0 })
            {
                Dump(covers[0].GetType());
                foreach (var p in covers[0].GetType().GetProperties())
                    Console.WriteLine($"  cover.{p.Name} = {p.GetValue(covers[0])}");
            }
        }
        var txt = mi.Text ?? "";
        Console.WriteLine($"Text 长度={txt.Length}");
        foreach (var line in txt.Split('\n').Where(l =>
                     l.Contains("Lyrics", StringComparison.OrdinalIgnoreCase) ||
                     l.Contains("Cover", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"  TEXT> {line.Trim()}");
    }
    return 0;
}

if (!Directory.Exists(dir))
{
    Console.WriteLine($"目录不存在：{dir}");
    return 1;
}

var sw0 = Stopwatch.StartNew();
var allFiles = Directory.EnumerateFiles(dir, "*.*", new EnumerationOptions
{
    RecurseSubdirectories = true,
    IgnoreInaccessible = true,
}).ToList();
var enumMs = sw0.ElapsedMilliseconds;
var totalBytes = allFiles.Sum(f => new FileInfo(f).Length);
Console.WriteLine($"目录：{dir}");
Console.WriteLine($"文件：{allFiles.Count} 个，共 {totalBytes / 1048576.0:N0} MB（枚举耗时 {enumMs} ms）");
Console.WriteLine($"CPU 逻辑核数：{Environment.ProcessorCount}，扫描并行度：{FileScannerService.ScanParallelism}");
Console.WriteLine();

var settings = new SettingsService(Path.Combine(Path.GetTempPath(), "scanbench-settings.db"));
var logger = new LoggerService(settings);
var tagService = new TagService(logger);
var scanner = new FileScannerService(tagService);

if (mode is "all" or "current")
{
    await RunPhaseAsync("当前实现（TagLibSharp2 轻量扫描）", async progress =>
    {
        var files = await scanner.ScanDirectoryAsync(dir, true, progress);
        return files.Count;
    });
}

if (mode is "all" or "mediainfo")
{
    await RunPhaseAsync("MediaInfo 基准（仅媒体信息，不含标签文本）", progress =>
    {
        var done = 0;
        return Task.Run(() =>
        {
            Parallel.ForEach(allFiles,
                new ParallelOptions { MaxDegreeOfParallelism = FileScannerService.ScanParallelism },
                path =>
                {
                    try
                    {
                        var mi = new MediaInfo.MediaInfoWrapper(path);
                        // 触碰属性确保真实解析
                        if (mi.Success) { _ = mi.Duration; _ = mi.Tags; }
                    }
                    catch { /* 单文件失败忽略 */ }
                    progress?.Report(Interlocked.Increment(ref done));
                });
            return done;
        });
    });
}

if (mode is "verify")
{
    // 先用 TagLib 全量（急切解析）读第一个文件：给出封面块的权威事实（数量/字节数/MimeType）
    var firstAudio = allFiles.First(f => scanner.IsSupportedFile(f));
    using (var tf = TagLib.File.Create(firstAudio))
    {
        var pics = tf.Tag.Pictures;
        Console.WriteLine($"权威解析 {Path.GetFileName(firstAudio)}：Pictures={pics?.Length ?? 0}");
        if (pics is { Length: > 0 })
            Console.WriteLine($"  首图 MimeType={pics[0].MimeType}，数据 {pics[0].Data.Count / 1024.0:N0} KB，Type={pics[0].Type}");
        Console.WriteLine($"  BitRate={tf.Properties.AudioBitrate}，Duration={tf.Properties.Duration}");
    }
    Console.WriteLine();

    // 抽查前 40 个音频文件：轻量路径（新引擎） vs 全量路径（TagLibSharp2）关键字段一致性
    var audioFiles = allFiles.Where(f => scanner.IsSupportedFile(f)).Take(40).ToList();
    Console.WriteLine($"抽查 {audioFiles.Count} 个文件：");
    Console.WriteLine($"{"字段",-14}{"轻量路径",-40}{"全量路径",-40}一致");
    var mismatches = 0;
    foreach (var f in audioFiles)
    {
        var s = await tagService.ReadTagsSummaryAsync(f);
        var full = await tagService.ReadTagsAsync(f);
        mismatches += Cmp(f, "Title", s.Title, full.Title);
        mismatches += Cmp(f, "Artist", s.Artist, full.Artist);
        mismatches += Cmp(f, "Album", s.Album, full.Album);
        // 时长允许 ±2ms 的引擎舍入差
        mismatches += CmpDuration(f, s.DurationMs, full.DurationMs);
        // 码率允许 ±2kbps 取整差（MediaInfo 四舍五入 vs TagLib 截断）
        mismatches += CmpBitRate(f, s.BitRate, full.BitRate);
        mismatches += Cmp(f, "PicCount", s.PictureCount.ToString(), full.PictureCount.ToString());
        mismatches += Cmp(f, "LyricsPresent", s.LyricsPresent.ToString(), (!string.IsNullOrEmpty(full.Lyrics)).ToString());
        mismatches += Cmp(f, "CoverMime", s.CoverArtMimeType, full.CoverArtMimeType);
    }
    Console.WriteLine(mismatches == 0 ? "全部一致。" : $"发现 {mismatches} 处不一致。");
    return mismatches == 0 ? 0 : 2;

    int CmpBitRate(string file, int? a, int? b)
    {
        if (a == null || b == null || Math.Abs(a.Value - b.Value) <= 2) return 0;
        Console.WriteLine($"  {Path.GetFileName(file)} [BitRate]: 轻量='{a}' vs 全量='{b}'");
        return 1;
    }
    int CmpDuration(string file, long? a, long? b)
    {
        if (a == null || b == null || Math.Abs(a.Value - b.Value) <= 2) return 0;
        Console.WriteLine($"  {Path.GetFileName(file)} [Duration]: 轻量='{a}' vs 全量='{b}'");
        return 1;
    }
    int Cmp(string file, string field, string? a, string? b)
    {
        var normA = string.IsNullOrEmpty(a) ? "" : a;
        var normB = string.IsNullOrEmpty(b) ? "" : b;
        var ok = normA == normB;
        if (!ok)
            Console.WriteLine($"  {Path.GetFileName(file)} [{field}]: 轻量='{Trunc(normA)}' vs 全量='{Trunc(normB)}'");
        return ok ? 0 : 1;
    }
    static string Trunc(string s) => s.Length > 36 ? s[..36] + "…" : s;
}

Console.WriteLine("完成。");
return 0;

// ---------------- 测量骨架 ----------------

static async Task RunPhaseAsync(string name, Func<IProgress<int>?, Task<int>> body)
{
    // 让上一阶段的内存充分回落，保证阶段间互不污染
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    await Task.Delay(500);

    using var proc = Process.GetCurrentProcess();
    proc.Refresh();
    var wsBase = proc.WorkingSet64;
    var managedBase = GC.GetTotalMemory(true);

    var sampling = true;
    long wsPeak = wsBase, managedPeak = managedBase;
    long wsSum = 0; var samples = 0;
    var sampler = Task.Run(async () =>
    {
        while (sampling)
        {
            proc.Refresh();
            var ws = proc.WorkingSet64;
            var mg = GC.GetTotalMemory(false);
            if (ws > wsPeak) wsPeak = ws;
            if (mg > managedPeak) managedPeak = mg;
            wsSum += ws; samples++;
            await Task.Delay(100);
        }
    });

    var progress = new Progress<int>(n =>
    {
        if (n % 250 == 0) Console.Write($"\r  进度 {n}…   ");
    });

    var sw = Stopwatch.StartNew();
    var count = await body(progress);
    sw.Stop();
    sampling = false;
    await sampler;
    Console.Write("\r                \r");

    var wsAvg = samples > 0 ? wsSum / samples : wsBase;
    Console.WriteLine($"【{name}】");
    Console.WriteLine($"  完成时间        : {sw.Elapsed.TotalSeconds:F2} s（{count} 个文件）");
    Console.WriteLine($"  读取速度        : {count / sw.Elapsed.TotalSeconds:F1} 文件/秒");
    Console.WriteLine($"  工作集峰值增量  : {(wsPeak - wsBase) / 1048576.0:N0} MB（基线 {wsBase / 1048576.0:N0} → 峰值 {wsPeak / 1048576.0:N0} MB）");
    Console.WriteLine($"  工作集均值增量  : {(wsAvg - wsBase) / 1048576.0:N0} MB");
    Console.WriteLine($"  托管堆峰值增量  : {(managedPeak - managedBase) / 1048576.0:N0} MB");
    Console.WriteLine($"  Gen0/1/2 GC     : {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}（进程累计）");
    Console.WriteLine();
}
