using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Models.Compare;
using MusicTagRecover.Services.Compare;

namespace MusicTagRecover.ViewModels.Compare;

/// <summary>
/// 删除历史窗口视图模型：compare_delete_log 的条件查询、CSV 导出、
/// 以及依据日志中的原路径从系统回收站还原（$I/$R 解析由 RecycleBinService 完成）。
/// </summary>
public partial class DeleteHistoryViewModel : ObservableObject
{
    private readonly CompareDatabaseService _db;

    public DeleteHistoryViewModel(CompareDatabaseService dbService)
    {
        _db = dbService;
    }

    // ============================================================
    // 筛选条件
    // ============================================================

    /// <summary>起始日期（当天 00:00:00，服务层拼装）。</summary>
    [ObservableProperty] private DateTime? _fromDate;

    /// <summary>截止日期（当天 23:59:59，服务层拼装）。</summary>
    [ObservableProperty] private DateTime? _toDate;

    /// <summary>文件名关键字（服务层只有路径关键字参数，此条件在内存中按文件名过滤）。</summary>
    [ObservableProperty] private string _fileNameKeyword = "";

    /// <summary>路径关键字（透传服务层 LIKE 过滤）。</summary>
    [ObservableProperty] private string _pathKeyword = "";

    [ObservableProperty] private bool _isBusy;

    /// <summary>查询结果。</summary>
    public ObservableCollection<CompareDeleteLogRecord> Logs { get; } = new();

    [RelayCommand]
    public async Task QueryLogsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var from = FromDate;
            var to = ToDate;
            var pathKw = string.IsNullOrWhiteSpace(PathKeyword) ? null : PathKeyword.Trim();
            var nameKw = FileNameKeyword?.Trim() ?? "";

            var list = await Task.Run(() => _db.QueryLogs(from, to, pathKw, opType: null));

            // 文件名关键字：服务层无独立参数，在内存中按「原路径的文件名部分包含」过滤
            if (nameKw.Length > 0)
            {
                list = list
                    .Where(r => Path.GetFileName(r.Path)
                        .Contains(nameKw, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            Logs.Clear();
            foreach (var r in list)
                Logs.Add(r);
        }
        catch (Exception ex)
        {
            WpfUI.Warning("查询删除历史失败：\n" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ResetFilter()
    {
        FromDate = null;
        ToDate = null;
        FileNameKeyword = "";
        PathKeyword = "";
    }

    // ============================================================
    // 从回收站还原
    // ============================================================

    /// <summary>
    /// 还原选中日志对应的原路径：
    /// RecycleBinService.FindRecycleEntries 解析各盘 $Recycle.Bin 的 $I 元数据定位 $R 实体，
    /// RestoreEntry 移回原路径（被占用时自动加「_已恢复」后缀），随后恢复 DB 状态并补一条 restore 日志。
    /// 由视图代码隐藏传入 DataGrid 多选结果。
    /// </summary>
    public async Task RestoreSelectedAsync(IList<CompareDeleteLogRecord> selectedRows)
    {
        if (selectedRows == null || selectedRows.Count == 0)
        {
            WpfUI.Warning("请先选择要还原的记录。");
            return;
        }

        // restore 类型的日志本身是还原操作，不再重复还原
        var rows = selectedRows
            .Where(r => !string.Equals(r.OpType, "restore", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rows.Count == 0)
        {
            WpfUI.Warning("所选记录均为「还原」类型记录，无需再次还原。");
            return;
        }

        var answer = WpfUI.YesNoCancel(
            $"将尝试从系统回收站还原 {rows.Count} 项到原路径（原路径被占用时会自动改名）。\n\n" +
            "「是」= 开始还原；「否」/「取消」= 中止。\n\n确认继续？");
        if (answer != MessageBoxResult.Yes) return;

        var failures = new List<string>();
        var restored = new List<string>();

        foreach (var row in rows)
        {
            try
            {
                // 先查回收站条目，便于区分「回收站已无此项」与「移动失败」两类原因
                var entries = await Task.Run(() => RecycleBinService.FindRecycleEntries(row.Path));
                if (entries.Count == 0)
                {
                    failures.Add(row.Path + " —— 回收站中未找到（可能已被清空或已还原）");
                    continue;
                }

                var entry = entries[0]; // 服务已按删除时间倒序，取最近一次
                string restoredTo = row.Path;
                string? error = null;
                var ok = await Task.Run(() =>
                {
                    try
                    {
                        return RecycleBinService.RestoreEntry(entry, out restoredTo);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        return false;
                    }
                });

                if (!ok)
                {
                    failures.Add(row.Path + " —— 还原失败（" +
                                  (string.IsNullOrEmpty(error) ? "文件可能被占用或无权限移动" : error) + "）");
                    continue;
                }

                // 还原成功：DB 状态恢复为正常，并写 restore 日志
                _db.SetStatusByPath(row.Path, CompareFileStatus.Normal, null);
                _db.LogDelete("restore", row.Path,
                    "从回收站还原 → " + restoredTo +
                    (string.Equals(restoredTo, row.Path, StringComparison.OrdinalIgnoreCase)
                        ? "" : "（原路径被占用，已改名）"));
                restored.Add(row.Path);
            }
            catch (Exception ex)
            {
                failures.Add(row.Path + " —— " + ex.Message);
            }
        }

        if (restored.Count > 0)
            await QueryLogsAsync();

        if (failures.Count > 0)
        {
            WpfUI.Warning("以下项目还原失败：\n\n" +
                          string.Join("\n", failures.Take(20)) +
                          (failures.Count > 20 ? $"\n…等共 {failures.Count} 项" : ""));
        }
        else if (restored.Count > 0)
        {
            WpfUI.Info($"成功还原 {restored.Count} 项。");
        }
    }

    // ============================================================
    // 导出 CSV（UTF-8 BOM，字段转义）
    // ============================================================

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Logs.Count == 0)
        {
            WpfUI.Warning("当前没有可导出的记录，请先查询。");
            return;
        }

        var path = WpfUI.SaveFile(
            "CSV 文件|*.csv",
            fileName: "删除历史_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv",
            title: "导出删除历史");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("删除时间,操作人,操作类型,原路径,备注");
            foreach (var r in Logs)
            {
                sb.Append(CsvEscape(r.OpTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                  .Append(CsvEscape(r.Operator)).Append(',')
                  .Append(CsvEscape(OpTypeText(r.OpType))).Append(',')
                  .Append(CsvEscape(r.Path)).Append(',')
                  .Append(CsvEscape(r.Detail)).AppendLine();
            }

            // 手写 UTF-8 BOM，避免 Excel 打开中文乱码
            var content = sb.ToString();
            await Task.Run(() => File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)));

            WpfUI.Info($"已导出 {Logs.Count} 条记录到：\n" + path);
        }
        catch (Exception ex)
        {
            WpfUI.Warning("导出 CSV 失败：\n" + ex.Message);
        }
    }

    /// <summary>CSV 字段转义：含逗号、引号、换行时用双引号包裹，内部引号双写。</summary>
    private static string CsvEscape(string? value)
    {
        value ??= "";
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    /// <summary>操作类型代码 → 中文展示（op_type: file/folder/empty_dir/restore）。</summary>
    public static string OpTypeText(string? opType) => opType switch
    {
        "file" => "删除文件",
        "folder" => "删除文件夹",
        "empty_dir" => "删除空目录",
        "restore" => "还原",
        _ => string.IsNullOrEmpty(opType) ? "" : opType,
    };
}

/// <summary>删除历史 DataGrid 用：op_type 代码 → 中文名。</summary>
public sealed class OpTypeDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => DeleteHistoryViewModel.OpTypeText(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
