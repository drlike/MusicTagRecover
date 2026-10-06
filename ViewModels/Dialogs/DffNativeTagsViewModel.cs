using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Services;
using MusicTagRecover.Utils;

namespace MusicTagRecover.ViewModels.Dialogs;

/// <summary>
/// DFF 原生 DSDIFF 标签（DIIN 子字段 / COMT 注释）管理窗口 ViewModel。
/// 支持单文件查看编辑与多文件批量执行；所有落盘操作经 <see cref="DffNativeTagService"/>，
/// 单文件异常不中断整批，成功/失败结果通过 <see cref="StatusText"/> 反馈。
/// </summary>
public sealed partial class DffNativeTagsViewModel : ObservableObject
{
    private readonly DffNativeTagService _service;
    private readonly ILoggerService _logger;

    /// <summary>本次打开的全部 DFF 文件路径。</summary>
    public IReadOnlyList<string> FilePaths { get; }

    /// <summary>操作成功的文件路径（窗口关闭后由主窗口同步刷新底层数据模型）。</summary>
    public HashSet<string> ChangedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>文件下拉项（值为完整路径，界面显示 <see cref="FileOption.Display"/>）。</summary>
    public ObservableCollection<FileOption> FileItems { get; } = new();

    [ObservableProperty] private FileOption? _selectedFileItem;
    [ObservableProperty] private bool _applyToAll;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<DiinFieldRowViewModel> DiinFields { get; } = new();
    public ObservableCollection<CommtCommentRowViewModel> CommtComments { get; } = new();

    /// <summary>当前实际操作的文件集合（批量模式=全部文件，单文件模式=当前文件）。</summary>
    private IReadOnlyList<string> TargetFiles
        => ApplyToAll ? FilePaths : new[] { SelectedFileItem!.FullPath };

    public bool HasMultipleFiles => FilePaths.Count > 1;

    public DffNativeTagsViewModel(IReadOnlyList<string> dffFilePaths, ILoggerService logger)
    {
        _logger = logger;
        _service = new DffNativeTagService(logger);
        FilePaths = dffFilePaths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (FilePaths.Count == 0)
            throw new ArgumentException("至少需要一个 DFF 文件", nameof(dffFilePaths));

        if (HasMultipleFiles)
            FileItems.Add(new FileOption
            {
                FullPath = FilePaths[0],
                Display = $"[批量] 全部 {FilePaths.Count} 个文件（批量操作）",
                IsBatch = true,
            });
        foreach (var p in FilePaths)
            FileItems.Add(new FileOption { FullPath = p, Display = Path.GetFileName(p) });
        _selectedFileItem = FileItems[0];
        ApplyToAll = HasMultipleFiles;
        ReloadCurrent();
    }

    /// <summary>批量模式预览用文件（第一个文件）。</summary>
    private string PreviewFilePath
        => ApplyToAll && HasMultipleFiles ? FilePaths[0] : SelectedFileItem!.FullPath;

    partial void OnSelectedFileItemChanged(FileOption? value)
    {
        ApplyToAll = value?.IsBatch ?? false;
        ReloadCurrent();
    }

    /// <summary>重新读取当前（单文件模式）文件的 DIIN/COMT 数据；批量模式展示第一个文件作为模板。</summary>
    private void ReloadCurrent()
    {
        DiinFields.Clear();
        CommtComments.Clear();
        var previewFile = PreviewFilePath;
        try
        {
            int idx = 0;
            foreach (var f in _service.ReadDiinFields(previewFile))
                DiinFields.Add(new DiinFieldRowViewModel(f.Id, f.Text));
            foreach (var c in _service.ReadCommtComments(previewFile))
                CommtComments.Add(new CommtCommentRowViewModel(idx++, c));
            StatusText = ApplyToAll
                ? $"批量模式：以下为首个文件的字段预览，修改/删除将应用到全部 {FilePaths.Count} 个文件"
                : $"已加载：{Path.GetFileName(previewFile)}（DIIN {DiinFields.Count} 项 / COMT {CommtComments.Count} 条）";
        }
        catch (Exception ex)
        {
            StatusText = "读取失败：" + ex.Message;
            _logger.Error(ex, "DFF 原生标签读取失败：{0}", previewFile);
        }
    }

    // ================= DIIN =================

    [RelayCommand]
    private void AddDiin()
    {
        var (id, text) = Views.Dialogs.DffDiinFieldEditWindow.AskNew(PreviewFilePath);
        if (id == null) return;

        if (!ConfirmBatch($"确定为 {TargetCountText}写入/更新 DIIN 字段「{id}」？")) return;
        RunBatchOp($"DIIN 字段「{id}」写入",
            prog => _service.WriteDiinFieldBatch(TargetFiles, id, text, prog));
    }

    [RelayCommand]
    private void EditDiin(DiinFieldRowViewModel? row)
    {
        if (row == null) return;
        var (id, text) = Views.Dialogs.DffDiinFieldEditWindow.AskEdit(PreviewFilePath, row.FieldId, row.Text);
        if (id == null) return;

        // 批量模式下若用户在弹窗中改了字段 ID，则语义为"写新 ID"——旧 ID 在各文件中保留；
        // 单文件模式下 ID 被锁定，二者一致，此处直接按确认结果执行
        if (!ConfirmBatch($"确定修改 {TargetCountText}的 DIIN 字段「{row.FieldId}」{(id != row.FieldId ? "（新 ID：" + id + "）" : "")}？"))
            return;
        RunBatchOp($"DIIN 字段「{id}」修改",
            prog => _service.WriteDiinFieldBatch(TargetFiles, id, text, prog));
    }

    [RelayCommand]
    private void DeleteDiin(DiinFieldRowViewModel? row)
    {
        if (row == null) return;
        if (!WpfUI.Confirm(
                $"确定删除 {TargetCountText}的 DIIN 原生字段「{row.FieldId}」？\n" +
                "该字段关联的内容将被一并清除，此操作不可撤销（DSD 音频数据不受影响）。",
                "删除 DIIN 字段"))
            return;

        RunBatchOp($"DIIN 字段「{row.FieldId}」删除",
            prog => _service.DeleteDiinFieldBatch(TargetFiles, row.FieldId, prog));
    }

    // ================= COMT =================

    [RelayCommand]
    private void AddCommt()
    {
        var comment = Views.Dialogs.DffCommtEditWindow.AskNew(PreviewFilePath);
        if (comment == null) return;
        if (!ConfirmBatch($"确定向 {TargetCountText}追加一条 COMT 注释？")) return;
        RunBatchOp("COMT 注释追加",
            prog => _service.AddCommtCommentBatch(TargetFiles, comment, prog));
    }

    [RelayCommand]
    private void EditCommt(CommtCommentRowViewModel? row)
    {
        if (row == null) return;
        if (ApplyToAll && HasMultipleFiles)
        {
            WpfUI.Warning("批量模式下各文件注释索引可能不同，请切换到单文件模式后修改具体注释；\n" +
                          "批量场景可使用「追加」或「删除全部注释」。");
            return;
        }
        var comment = Views.Dialogs.DffCommtEditWindow.AskEdit(PreviewFilePath, row.Comment);
        if (comment == null) return;
        var targets = TargetFiles;
        RunBatchOp($"COMT 注释 #{row.Index} 修改",
            prog => _service.WriteCommtCommentBatch(targets, row.Index, comment, prog));
    }

    [RelayCommand]
    private void DeleteCommt(CommtCommentRowViewModel? row)
    {
        if (row == null) return;
        if (ApplyToAll && HasMultipleFiles)
        {
            if (WpfUI.YesNo(
                    $"批量模式下将删除全部 {FilePaths.Count} 个文件中的【所有】COMT 注释（无法按索引对齐）。\n确定继续？",
                    "批量删除 COMT 注释") != System.Windows.MessageBoxResult.Yes) return;
            RunBatchOp("COMT 全部注释清空",
                prog => _service.ClearAllCommtBatch(TargetFiles, prog));
            return;
        }
        if (!WpfUI.Confirm(
                $"确定删除 {Path.GetFileName(PreviewFilePath)} 的 COMT 注释 #{row.Index}？\n此操作不可撤销（DSD 音频数据不受影响）。",
                "删除 COMT 注释"))
            return;
        var targets = TargetFiles;
        RunBatchOp($"COMT 注释 #{row.Index} 删除",
            prog => _service.DeleteCommtCommentBatch(targets, row.Index, prog));
    }

    // ================= 通用 =================

    private string TargetCountText => ApplyToAll ? $"全部 {FilePaths.Count} 个文件" : "该文件";

    private bool ConfirmBatch(string message)
        => !ApplyToAll || WpfUI.YesNo(message + $"\n\n将批量应用到 {FilePaths.Count} 个文件，是否继续？",
            "批量操作确认") == System.Windows.MessageBoxResult.Yes;

    private void RunBatchOp(string action, Func<IProgress<int>?, DffNativeBatchResult> op)
    {
        IsBusy = true;
        StatusText = action + "中…";
        try
        {
            var progress = new Progress<int>();
            DffNativeBatchResult result;
            try
            {
                result = op(progress);
            }
            catch (Exception ex)
            {
                StatusText = action + "失败：" + ex.Message;
                WpfUI.Error(action + "失败：\n" + ex.Message);
                return;
            }

            foreach (var f in TargetFiles)
            {
                if (result.Failures.All(x => !string.Equals(x.FilePath, f, StringComparison.OrdinalIgnoreCase)))
                    ChangedFiles.Add(f);
            }
            ReloadCurrent();

            if (result.AllSucceeded)
            {
                StatusText = result.FormatSummary(action);
                WpfUI.Info(result.FormatSummary(action) + "。");
            }
            else
            {
                StatusText = result.FormatSummary(action);
                var detail = string.Join("\n", result.Failures.Take(10)
                    .Select(x => $"• {Path.GetFileName(x.FilePath)}：{x.Error}"));
                if (result.Failures.Count > 10) detail += $"\n…另有 {result.Failures.Count - 10} 个失败项";
                WpfUI.Warning($"{result.FormatSummary(action)}\n\n失败明细：\n{detail}");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>文件下拉项：承载完整路径与界面显示文本，批量项以 <see cref="IsBatch"/> 区分。</summary>
public sealed class FileOption
{
    public required string FullPath { get; init; }
    public required string Display { get; init; }
    public bool IsBatch { get; init; }
}

/// <summary>DIIN 字段行。</summary>
public sealed partial class DiinFieldRowViewModel : ObservableObject
{
    public string FieldId { get; }
    [ObservableProperty] private string _text;
    public string DisplayName => DescribeDiinId(FieldId);

    public DiinFieldRowViewModel(string fieldId, string text)
    {
        FieldId = fieldId;
        _text = text ?? "";
    }

    /// <summary>常见 DIIN 子 chunk ID 的中文名映射。</summary>
    public static string DescribeDiinId(string id) => id switch
    {
        "DITI" => "标题 (DITI)",
        "DIAR" => "艺术家 (DIAR)",
        "DGEN" => "流派 (DGEN)",
        "DINA" => "乐队 (DINA)",
        "DARE" => "录音师 (DARE)",
        "DPRS" => "制作人 (DPRS)",
        "DENG" => "工程师 (DENG)",
        "DORD" => "乐团/标签 (DORD)",
        "DSRC" => "来源 (DSRC)",
        "DCOU" => "录制国家/地区 (DCOU)",
        "DLAH" => "厂牌 (DLAH)",
        _ => id,
    };
}

/// <summary>COMT 注释行。</summary>
public sealed partial class CommtCommentRowViewModel : ObservableObject
{
    public int Index { get; }
    public DffCommtComment Comment { get; }
    public string DateTimeText =>
        $"{Comment.Year:0000}-{Comment.Month:D2}-{Comment.Day:D2} {Comment.Hour:D2}:{Comment.Minute:D2}:{Comment.Second:D2}";
    public string TypeText => $"类型 {Comment.CommentType} / 引用 {Comment.CommentReference}";
    [ObservableProperty] private string _text;

    public CommtCommentRowViewModel(int index, DffCommtComment comment)
    {
        Index = index;
        Comment = comment;
        _text = comment.Text ?? "";
    }
}
