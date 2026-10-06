using System.Security.Cryptography;
using MusicTagRecover.Models.Compare;

namespace MusicTagRecover.Services.Compare;

/// <summary>
/// 文件比对扫描匹配服务（核心逻辑，独立于主窗口功能链路）。
///   - 手动迭代 DFS 枚举（非 Directory.EnumerateFiles 递归），可控跳过被排重的源子树
///   - 全路径全局去重，避免重叠目录重复扫描
///   - SQLite 缓存命中（路径+大小+修改时间均未变）直接复用 MD5/标签 → 二次扫描大幅提速
///   - 扩展字段（MD5/标签）按所选规则懒计算，规则1 零额外 IO
///   - 三种匹配规则输出 6 类核心信息结果行
/// </summary>
public class CompareScanService
{
    /// <summary>富化（MD5/标签读取）并发度：IO 密集，4 路并行已接近磁盘吞吐且内存峰值低</summary>
    private const int EnrichConcurrency = 4;

    private readonly CompareDatabaseService _db;

    public CompareScanService(CompareDatabaseService db) => _db = db;

    // ============ 音视频扩展名（规则3 仅针对这些格式读内嵌标签） ============
    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".ogg", ".wma", ".wav", ".aiff", ".aif",
        ".ape", ".wv", ".mpc", ".opus", ".aac", ".dsf", ".dff"
    };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts"
    };

    // ============================================================
    // 输入/输出模型
    // ============================================================

    /// <summary>一次比对请求。</summary>
    public class ScanRequest
    {
        public List<string> SourceDirs = new();
        public List<string> TargetDirs = new();
        public CompareMatchRule Rule = CompareMatchRule.NameAndSize;
    }

    /// <summary>扫描进度（阶段 + 已处理/总数）。</summary>
    public class ScanProgress
    {
        public string Phase = "";
        public int Current;
        public int Total;
    }

    /// <summary>一次比对的完整产出。</summary>
    public class ScanOutcome
    {
        /// <summary>命中结果（每对 源→目标 一行）</summary>
        public List<CompareMatchResult> Results = new();
        public int SourceFileCount;
        public int TargetFileCount;
        /// <summary>缓存命中数（二次扫描提速依据）</summary>
        public int CacheHits;
        /// <summary>扫描时被自动排重（从目标范围剔除）的目标目录</summary>
        public List<string> ExcludedTargets = new();
    }

    // ============================================================
    // 主流程
    // ============================================================

    /// <summary>
    /// 执行完整比对：枚举 → 缓存命中/懒计算扩展字段 → 按规则匹配 → 元数据回写数据库。
    /// 取消令牌触发时抛 OperationCanceledException。
    /// </summary>
    public async Task<ScanOutcome> RunScanAsync(
        ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var outcome = new ScanOutcome();
        var now = DateTime.Now.Ticks;

        // ---- 1) 计算有效目标目录：排除源目录自身及源子树（自动排重） ----
        var effectiveTargets = ComputeEffectiveTargets(request.SourceDirs, request.TargetDirs, outcome.ExcludedTargets);

        // ---- 2) 枚举源/目标文件元数据（后台执行，UI 不卡） ----
        progress?.Report(new ScanProgress { Phase = "正在枚举文件..." });
        var sourceRecords = new List<CompareFileRecord>();
        var targetRecords = new List<CompareFileRecord>();
        await Task.Run(() =>
        {
            // 数据库缓存一次性载入内存（仅正常状态记录），判定键=路径+大小+修改时间
            var cache = _db.LoadAllNormal();

            // 全路径去重集合（Windows 路径大小写不敏感）；源先入集 → 同文件不会既是源又是目标
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 预规整源目录路径（后续每目录比对无需再走 GetFullPath）
            var excludedSubtrees = request.SourceDirs
                .Select(NormalizeDir)
                .ToList();

            foreach (var dir in request.SourceDirs)
                EnumerateDir(dir, dir, sourceRecords, seen, cache, excludedSubtrees, isTarget: false,
                    now, ct, () => progress?.Report(new ScanProgress { Phase = "正在枚举源目录...", Current = sourceRecords.Count }));

            foreach (var dir in effectiveTargets)
                EnumerateDir(dir, dir, targetRecords, seen, cache, excludedSubtrees, isTarget: true,
                    now, ct, () => progress?.Report(new ScanProgress { Phase = "正在枚举目标目录...", Current = targetRecords.Count }));
        }, ct);

        outcome.SourceFileCount = sourceRecords.Count;
        outcome.TargetFileCount = targetRecords.Count;

        ct.ThrowIfCancellationRequested();

        // ---- 3) 按规则懒计算扩展字段（MD5 / 音视频标签），并行 4 路 ----
        await EnrichAsync(request.Rule, sourceRecords, targetRecords, progress, ct);

        // ---- 4) 按规则匹配 ----
        progress?.Report(new ScanProgress { Phase = "正在匹配..." });
        Match(request.Rule, sourceRecords, targetRecords, outcome.Results);
        ct.ThrowIfCancellationRequested();

        // ---- 5) 元数据回写数据库（含本次新计算的 MD5/标签，供下次扫描复用） ----
        progress?.Report(new ScanProgress { Phase = "正在保存缓存..." });
        var all = new List<CompareFileRecord>(sourceRecords.Count + targetRecords.Count);
        all.AddRange(sourceRecords);
        all.AddRange(targetRecords);
        await Task.Run(() => _db.UpsertFiles(all), ct);

        return outcome;
    }

    // ============================================================
    // 排重与枚举
    // ============================================================

    /// <summary>
    /// 计算有效目标目录集合：
    /// 目标目录本身等于某源目录、位于某源目录之内 → 整体剔除并记入 excluded；
    /// 其余目标目录在枚举时再跳过位于其下的源子树（excludedSubtrees）。
    /// </summary>
    public static List<string> ComputeEffectiveTargets(
        List<string> sources, List<string> targets, List<string> excluded)
    {
        var effective = new List<string>();
        foreach (var t in targets)
        {
            var inside = false;
            foreach (var s in sources)
            {
                if (IsSameOrUnder(t, s)) { inside = true; break; }
            }
            if (inside) excluded.Add(t);
            else effective.Add(t);
        }
        return effective;
    }

    /// <summary>path 是否等于或位于 parent 之内（大小写不敏感）。供表单添加目录时排重校验复用。</summary>
    public static bool IsSameOrUnder(string path, string parent)
    {
        var p = NormalizeDir(path);
        var q = NormalizeDir(parent);
        if (string.Equals(p, q, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(q + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>规整目录路径：转全路径、去尾部分隔符。</summary>
    public static string NormalizeDir(string dir)
    {
        try { return Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }

    /// <summary>
    /// 手动迭代 DFS 枚举一个根目录下所有文件 → CompareFileRecord。
    /// isTarget=true 时跳过源子树；全局 seen 集合防止重叠目录重复记录。
    /// 命中数据库缓存（路径+大小+修改时间一致）直接复用 MD5/标签。
    /// </summary>
    private void EnumerateDir(
        string root, string rootKey, List<CompareFileRecord> output,
        HashSet<string> seen, Dictionary<string, CompareFileRecord> cache,
        List<string> excludedSubtrees, bool isTarget, long now,
        CancellationToken ct, Action tick)
    {
        var rootNorm = NormalizeDir(root);
        var stack = new Stack<string>();
        stack.Push(rootNorm);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            // 目标枚举时跳过源目录子树（自动排重的第二层兜底）
            // dir 与 excludedSubtrees 均已规整，直接字符串比对避免逐目录 GetFullPath
            if (isTarget)
            {
                var skip = false;
                foreach (var ex in excludedSubtrees)
                {
                    if (string.Equals(dir, ex, StringComparison.OrdinalIgnoreCase) ||
                        dir.StartsWith(ex + "\\", StringComparison.OrdinalIgnoreCase))
                    { skip = true; break; }
                }
                if (skip) continue;
            }

            // 文件
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            var sinceReport = 0;
            foreach (var f in files)
            {
                if (!seen.Add(f)) continue;

                CompareFileRecord rec;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Exists)
                        rec = BuildRecord(fi, rootKey, cache, now);
                    else continue;
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                output.Add(rec);

                // 进度节流：每 256 个文件上报一次，避免 UI 消息洪泛
                if (++sinceReport >= 256)
                {
                    sinceReport = 0;
                    tick();
                }
            }

            // 子目录入栈
            var subs = Array.Empty<string>();
            try { subs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            foreach (var sub in subs) stack.Push(sub);
        }
    }

    /// <summary>由 FileInfo 构建记录，并尝试从缓存复用 MD5/标签。</summary>
    private static CompareFileRecord BuildRecord(
        FileInfo fi, string rootKey,
        Dictionary<string, CompareFileRecord> cache, long now)
    {
        var ext = fi.Extension.ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(fi.Name);
        var isMedia = AudioExts.Contains(ext) || VideoExts.Contains(ext);

        // 缓存命中：路径+大小+修改时间均未变化 → 直接复用 MD5/标题/艺术家
        if (cache.TryGetValue(fi.FullName, out var cached) &&
            cached.Size == fi.Length &&
            cached.MtimeTicks == fi.LastWriteTimeUtc.Ticks)
        {
            cached.RootDir = rootKey;
            cached.Dir = fi.DirectoryName ?? "";
            cached.Name = name;
            cached.Ext = ext;
            cached.Size = fi.Length;
            cached.MtimeTicks = fi.LastWriteTimeUtc.Ticks;
            cached.IsMedia = isMedia;
            cached.Status = CompareFileStatus.Normal;
            cached.DeletedAtTicks = null;
            cached.ScannedAtTicks = now;
            return cached;
        }

        return new CompareFileRecord
        {
            FullPath = fi.FullName,
            RootDir = rootKey,
            Dir = fi.DirectoryName ?? "",
            Name = name,
            Ext = ext,
            Size = fi.Length,
            MtimeTicks = fi.LastWriteTimeUtc.Ticks,
            IsMedia = isMedia,
            Status = CompareFileStatus.Normal,
            ScannedAtTicks = now,
        };
    }

    // ============================================================
    // 扩展字段懒计算（按规则）
    // ============================================================

    /// <summary>按所选规则并行计算扩展字段；命中缓存的记录跳过，实现二次扫描提速。</summary>
    private async Task EnrichAsync(
        CompareMatchRule rule,
        List<CompareFileRecord> sources, List<CompareFileRecord> targets,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        List<CompareFileRecord> pending;
        if (rule == CompareMatchRule.Md5)
        {
            // 规则2：源+目标全部计算 MD5（无缓存的才算）
            pending = new List<CompareFileRecord>(sources.Count + targets.Count);
            pending.AddRange(sources.Where(r => r.Md5 == null));
            pending.AddRange(targets.Where(r => r.Md5 == null));
            if (pending.Count == 0) return;

            var total = pending.Count;
            var done = 0;
            progress?.Report(new ScanProgress { Phase = "正在计算 MD5...", Current = 0, Total = total });
            await Task.Run(() =>
            {
                Parallel.ForEach(pending,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = EnrichConcurrency,
                        CancellationToken = ct
                    },
                    rec =>
                    {
                        rec.Md5 = ComputeMd5(rec.FullPath);
                        var d = Interlocked.Increment(ref done);
                        if (d % 64 == 0 || d == total)
                            progress?.Report(new ScanProgress { Phase = "正在计算 MD5...", Current = d, Total = total });
                    });
            }, ct);
        }
        else if (rule == CompareMatchRule.MediaTag)
        {
            // 规则3：仅音视频文件读取内嵌标签
            pending = new List<CompareFileRecord>();
            pending.AddRange(sources.Where(r => r.IsMedia && (r.Title == null || r.Artist == null)));
            pending.AddRange(targets.Where(r => r.IsMedia && (r.Title == null || r.Artist == null)));
            if (pending.Count == 0) return;

            var total = pending.Count;
            var done = 0;
            progress?.Report(new ScanProgress { Phase = "正在读取音视频标签...", Current = 0, Total = total });
            await Task.Run(() =>
            {
                Parallel.ForEach(pending,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = EnrichConcurrency,
                        CancellationToken = ct
                    },
                    rec =>
                    {
                        ReadMediaTags(rec);
                        var d = Interlocked.Increment(ref done);
                        if (d % 64 == 0 || d == total)
                            progress?.Report(new ScanProgress { Phase = "正在读取音视频标签...", Current = d, Total = total });
                    });
            }, ct);
        }
        // 规则1：基础属性已在枚举阶段就绪，无需额外 IO
    }

    /// <summary>流式计算文件 MD5（8KB 缓冲，控制内存）。</summary>
    private static string? ComputeMd5(string path)
    {
        try
        {
            using var md5 = MD5.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 8192, FileOptions.SequentialScan);
            var hash = md5.ComputeHash(stream);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// 读取音视频内嵌标签的标题/艺术家（单文件失败不影响整体）：
    /// 音频走 TagLibSharp2（失败回退 TagLib），视频走 MediaInfo。
    /// </summary>
    private static void ReadMediaTags(CompareFileRecord rec)
    {
        var ext = Path.GetExtension(rec.FullPath);
        try
        {
            if (VideoExts.Contains(ext))
            {
#if NET6_0_OR_GREATER || NETFRAMEWORK
                var mi = new MediaInfo.MediaInfoWrapper(rec.FullPath);
                if (mi.Success)
                {
                    rec.Title = mi.Tags?.Title;
                    rec.Artist = mi.Tags?.Artist;
                }
#endif
                return;
            }

            // 音频：TagLibSharp2 按扩展名强类型读取（APE/FLAC/MP3/M4A/OGG 等）
            if (TagLibSharp2Engine.IsSupported(ext))
            {
                TagLibSharp2.Core.IMediaFile? media = null;
                try
                {
                    media = TagLibSharp2Engine.ReadFile(rec.FullPath);
                    if (media?.Tag is { } tag)
                    {
                        rec.Title = tag.Title;
                        rec.Artist = !string.IsNullOrEmpty(tag.Artist)
                            ? tag.Artist
                            : tag.Performers?.FirstOrDefault();
                        return;
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

            using var tf = TagLib.File.Create(rec.FullPath);
            rec.Title = tf.Tag?.Title;
            var performers = tf.Tag?.FirstPerformer;
            if (string.IsNullOrEmpty(performers)) performers = tf.Tag?.JoinedPerformers;
            rec.Artist = performers;
        }
        catch (Exception)
        {
            // 损坏/不支持的格式：保留 null，不参与规则3匹配
        }
    }

    // ============================================================
    // 匹配
    // ============================================================

    /// <summary>
    /// 目标侧建匹配键字典，源侧逐条命中 → 输出结果行。
    /// 多目标命中同一源时每个目标各输出一行。
    /// </summary>
    private static void Match(
        CompareMatchRule rule,
        List<CompareFileRecord> sources, List<CompareFileRecord> targets,
        List<CompareMatchResult> results)
    {
        // 目标字典：匹配键 → 命中的目标记录列表（携带完整元数据，结果行需展示目标文件名/大小）
        var targetMap = new Dictionary<string, List<CompareFileRecord>>(StringComparer.Ordinal);
        foreach (var t in targets)
        {
            var key = BuildKey(rule, t);
            if (key == null) continue;
            if (!targetMap.TryGetValue(key, out var list))
                targetMap[key] = list = new List<CompareFileRecord>();
            list.Add(t);
        }

        var ruleName = RuleDisplayName(rule);
        foreach (var s in sources)
        {
            var key = BuildKey(rule, s);
            if (key == null) continue;
            if (!targetMap.TryGetValue(key, out var hits)) continue;

            // 语义：源文件=待删除方，目标文件=保留方；一个源命中多个目标时每个目标各输出一行
            foreach (var t in hits)
            {
                results.Add(new CompareMatchResult
                {
                    SourceDir = s.Dir,
                    SourceName = s.FileName,
                    SourceSize = s.Size,
                    SourcePath = s.FullPath,
                    TargetDir = t.Dir,
                    TargetName = t.FileName,
                    TargetSize = t.Size,
                    TargetPath = t.FullPath,
                    RuleName = ruleName,
                    LastModified = s.ModifiedTime,
                });
            }
        }
    }

    /// <summary>按规则构建匹配键；不满足规则前置条件返回 null（不参与匹配）。</summary>
    private static string? BuildKey(CompareMatchRule rule, CompareFileRecord r)
    {
        switch (rule)
        {
            case CompareMatchRule.NameAndSize:
                // 文件名+扩展名完全一致（Windows 路径大小写不敏感，键统一小写）
                return r.Name.ToLowerInvariant() + "|" + r.Ext + "|" + r.Size;

            case CompareMatchRule.Md5:
                return r.Md5 == null ? null : r.Md5.ToLowerInvariant();

            case CompareMatchRule.MediaTag:
                // 标题+艺术家完全匹配；任一为空的文件不参与（避免海量空值误配）
                if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.Artist))
                    return null;
                return r.Title.Trim().ToLowerInvariant() + "\u0001" + r.Artist.Trim().ToLowerInvariant();

            default:
                return null;
        }
    }

    /// <summary>规则展示名（写入结果行的「匹配规则」列）。</summary>
    public static string RuleDisplayName(CompareMatchRule rule) => rule switch
    {
        CompareMatchRule.NameAndSize => "基础属性(文件名+大小)",
        CompareMatchRule.Md5 => "MD5哈希",
        CompareMatchRule.MediaTag => "音视频标签(标题+艺术家)",
        _ => "未知"
    };
}
