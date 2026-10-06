using MusicTagRecover.Interfaces;
using MusicTagRecover.Utils;

namespace MusicTagRecover.Services;

/// <summary>
/// 单个文件的原生字段操作结果。
/// </summary>
public sealed class DffNativeOpFailure
{
    public required string FilePath { get; init; }
    public required string Error { get; init; }
}

/// <summary>
/// 一次（可能跨多文件的）原生 DIIN/COMT 批量操作结果。
/// </summary>
public sealed class DffNativeBatchResult
{
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public IReadOnlyList<DffNativeOpFailure> Failures { get; init; } = Array.Empty<DffNativeOpFailure>();

    public bool AllSucceeded => Failures.Count == 0;

    public string FormatSummary(string action)
        => AllSucceeded
            ? $"{action}成功：{Succeeded}/{Total} 个文件"
            : $"{action}完成：成功 {Succeeded}/{Total}，失败 {Failures.Count}";
}

/// <summary>
/// DFF 原生 DSDIFF 标签（DIIN 子字段 / COMT 注释）业务编排服务。
/// 负责：输入校验（权限/格式/索引）、单文件与批量执行、逐文件异常捕获，
/// 失败明细随结果返回，UI 层据此给出成功/失败反馈。底层字节级操作见
/// <see cref="DffId3TagIO"/>（原子临时文件替换，不触碰 DSD 音频块）。
/// </summary>
public sealed class DffNativeTagService
{
    private readonly ILoggerService _logger;

    public DffNativeTagService(ILoggerService logger) => _logger = logger;

    // ---------------- 读取 ----------------

    /// <summary>读取 DIIN 全部文本子字段；文件不存在/无法解析时抛异常（由调用方决定提示方式）。</summary>
    public IReadOnlyList<DffDiinField> ReadDiinFields(string filePath)
    {
        EnsureExistingDff(filePath);
        return DffId3TagIO.ReadDiinFields(filePath);
    }

    /// <summary>读取 COMT 全部注释。</summary>
    public IReadOnlyList<DffCommtComment> ReadCommtComments(string filePath)
    {
        EnsureExistingDff(filePath);
        return DffId3TagIO.ReadCommtComments(filePath);
    }

    // ---------------- DIIN 单文件 ----------------

    /// <summary>修改/新增单个 DFF 的 DIIN 子字段。</summary>
    public bool WriteDiinField(string filePath, string fieldId, string text, out string? error)
    {
        try
        {
            EnsureExistingDff(filePath);
            ValidateDiinFieldId(fieldId);
            text ??= string.Empty;
            if (DffId3TagIO.TryWriteDiinField(filePath, fieldId, text, out error)) return true;
            _logger.Warn($"DIIN 字段写入失败：{filePath} [{fieldId}] {error}");
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.Error(ex, "DIIN 字段写入异常：{0} [{1}]", filePath, fieldId);
            return false;
        }
    }

    /// <summary>删除单个 DFF 的 DIIN 子字段（不存在视为成功——目标状态已达成）。</summary>
    public bool DeleteDiinField(string filePath, string fieldId, out string? error)
    {
        try
        {
            EnsureExistingDff(filePath);
            ValidateDiinFieldId(fieldId);
            if (DffId3TagIO.TryDeleteDiinField(filePath, fieldId, out error)) return true;
            _logger.Warn($"DIIN 字段删除失败：{filePath} [{fieldId}] {error}");
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.Error(ex, "DIIN 字段删除异常：{0} [{1}]", filePath, fieldId);
            return false;
        }
    }

    // ---------------- COMT 单文件 ----------------

    public bool AddCommtComment(string filePath, DffCommtComment comment, out string? error)
    {
        try
        {
            EnsureExistingDff(filePath);
            ValidateCommt(comment);
            if (DffId3TagIO.TryAddCommtComment(filePath, comment, out error)) return true;
            _logger.Warn($"COMT 注释追加失败：{filePath} {error}");
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.Error(ex, "COMT 注释追加异常：{0}", filePath);
            return false;
        }
    }

    public bool WriteCommtComment(string filePath, int index, DffCommtComment comment, out string? error)
    {
        try
        {
            EnsureExistingDff(filePath);
            ValidateCommt(comment);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "注释索引不能为负数");
            if (DffId3TagIO.TryWriteCommtComment(filePath, index, comment, out error)) return true;
            _logger.Warn($"COMT 注释修改失败：{filePath} [{index}] {error}");
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.Error(ex, "COMT 注释修改异常：{0} [{1}]", filePath, index);
            return false;
        }
    }

    public bool DeleteCommtComment(string filePath, int index, out string? error)
    {
        try
        {
            EnsureExistingDff(filePath);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "注释索引不能为负数");
            if (DffId3TagIO.TryDeleteCommtComment(filePath, index, out error)) return true;
            _logger.Warn($"COMT 注释删除失败：{filePath} [{index}] {error}");
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.Error(ex, "COMT 注释删除异常：{0} [{1}]", filePath, index);
            return false;
        }
    }

    // ---------------- 批量 ----------------

    /// <summary>批量修改/新增 DIIN 子字段（相同字段 ID 与内容写入每个文件）。</summary>
    public DffNativeBatchResult WriteDiinFieldBatch(IReadOnlyList<string> filePaths, string fieldId, string text,
        IProgress<int>? progress = null)
    {
        ValidateDiinFieldId(fieldId);
        return RunBatch(filePaths, progress, (p, i) =>
            WriteDiinField(p, fieldId, text, out var err)
                ? null
                : new DffNativeOpFailure { FilePath = p, Error = err ?? "未知错误" });
    }

    /// <summary>批量删除 DIIN 子字段。</summary>
    public DffNativeBatchResult DeleteDiinFieldBatch(IReadOnlyList<string> filePaths, string fieldId,
        IProgress<int>? progress = null)
    {
        ValidateDiinFieldId(fieldId);
        return RunBatch(filePaths, progress, (p, i) =>
            DeleteDiinField(p, fieldId, out var err)
                ? null
                : new DffNativeOpFailure { FilePath = p, Error = err ?? "未知错误" });
    }

    /// <summary>批量追加相同 COMT 注释。</summary>
    public DffNativeBatchResult AddCommtCommentBatch(IReadOnlyList<string> filePaths, DffCommtComment comment,
        IProgress<int>? progress = null)
    {
        ValidateCommt(comment);
        return RunBatch(filePaths, progress, (p, i) =>
            AddCommtComment(p, comment, out var err)
                ? null
                : new DffNativeOpFailure { FilePath = p, Error = err ?? "未知错误" });
    }

    /// <summary>
    /// 批量修改 COMT 注释：修改每个文件中同索引的注释；
    /// 某文件注释条数不足时记为失败（不影响其他文件）。
    /// </summary>
    public DffNativeBatchResult WriteCommtCommentBatch(IReadOnlyList<string> filePaths, int index,
        DffCommtComment comment, IProgress<int>? progress = null)
    {
        ValidateCommt(comment);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "注释索引不能为负数");
        return RunBatch(filePaths, progress, (p, i) =>
            WriteCommtComment(p, index, comment, out var err)
                ? null
                : new DffNativeOpFailure { FilePath = p, Error = err ?? "未知错误" });
    }

    /// <summary>批量删除 COMT 注释（同索引）；某文件条数不足时记为失败。</summary>
    public DffNativeBatchResult DeleteCommtCommentBatch(IReadOnlyList<string> filePaths, int index,
        IProgress<int>? progress = null)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "注释索引不能为负数");
        return RunBatch(filePaths, progress, (p, i) =>
            DeleteCommtComment(p, index, out var err)
                ? null
                : new DffNativeOpFailure { FilePath = p, Error = err ?? "未知错误" });
    }

    /// <summary>批量清空每个文件的全部 COMT 注释（删除整个 COMT chunk）。</summary>
    public DffNativeBatchResult ClearAllCommtBatch(IReadOnlyList<string> filePaths,
        IProgress<int>? progress = null)
    {
        return RunBatch(filePaths, progress, (p, i) =>
        {
            var comments = DffId3TagIO.ReadCommtComments(p);
            // 从最后一条向前删除，避免索引位移
            for (int idx = comments.Count - 1; idx >= 0; idx--)
            {
                if (!DffId3TagIO.TryDeleteCommtComment(p, idx, out var err))
                    return new DffNativeOpFailure { FilePath = p, Error = err ?? "未知错误" };
            }
            return null;
        });
    }

    // ---------------- 校验与通用执行 ----------------

    private static DffNativeBatchResult RunBatch(IReadOnlyList<string> filePaths,
        IProgress<int>? progress, Func<string, int, DffNativeOpFailure?> action)
    {
        var failures = new List<DffNativeOpFailure>();
        for (int i = 0; i < filePaths.Count; i++)
        {
            DffNativeOpFailure? failure = null;
            try
            {
                failure = action(filePaths[i], i);
            }
            catch (Exception ex)
            {
                failure = new DffNativeOpFailure { FilePath = filePaths[i], Error = ex.Message };
            }
            if (failure != null) failures.Add(failure);
            progress?.Report(i + 1);
        }
        return new DffNativeBatchResult
        {
            Total = filePaths.Count,
            Succeeded = filePaths.Count - failures.Count,
            Failures = failures,
        };
    }

    private static void EnsureExistingDff(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("文件路径不能为空", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("文件不存在或已被移动/删除", filePath);
        if (!filePath.EndsWith(".dff", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("仅支持 DFF（DSDIFF）格式文件", nameof(filePath));
    }

    /// <summary>DIIN 子字段 ID：恰好 4 个可打印 ASCII 字符（DSDIFF chunk ID 规范）。</summary>
    public static void ValidateDiinFieldId(string fieldId)
    {
        if (string.IsNullOrWhiteSpace(fieldId) || fieldId.Length != 4)
            throw new ArgumentException("DIIN 字段 ID 必须为 4 个字符（如 DITI、DIAR）", nameof(fieldId));
        foreach (char c in fieldId)
            if (c < 0x20 || c > 0x7E)
                throw new ArgumentException("DIIN 字段 ID 只能包含可打印 ASCII 字符", nameof(fieldId));
    }

    private static void ValidateCommt(DffCommtComment comment)
    {
        if (comment == null) throw new ArgumentNullException(nameof(comment));
        comment.Text ??= string.Empty;
        if (comment.Year > 9999) throw new ArgumentOutOfRangeException(nameof(comment), "年份范围为 0-9999");
        if (comment.Month > 12) throw new ArgumentOutOfRangeException(nameof(comment), "月份范围为 0-12");
        if (comment.Day > 31) throw new ArgumentOutOfRangeException(nameof(comment), "日期范围为 0-31");
        if (comment.Hour > 23 || comment.Minute > 59 || comment.Second > 60)
            throw new ArgumentOutOfRangeException(nameof(comment), "时间范围不合法");
    }
}
