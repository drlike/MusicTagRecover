using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Models.Compare;
using MusicTagRecover.Services.Compare;

namespace MusicTagRecover.ViewModels.Compare;

/// <summary>
/// 文件比对窗口视图模型：目录选择、规则切换、后台扫描（取消/进度）、
/// 源文件回收站删除（目标文件保留）、空目录查找与清理。
/// 所有耗时操作均在后台线程执行，进度通过 Progress&lt;T&gt; 经 UI 线程回调。
/// </summary>
public partial class CompareViewModel : ObservableObject
{
    private readonly CompareScanService _scanService;
    private readonly CompareDatabaseService _db;

    /// <summary>当前扫描的取消令牌源（null 表示未在扫描）。</summary>
    private CancellationTokenSource? _scanCts;

    public CompareViewModel(CompareScanService scanService, CompareDatabaseService dbService)
    {
        _scanService = scanService;
        _db = dbService;
    }

    // ============================================================
    // 目录设置
    // ============================================================

    /// <summary>源目录集合（被比对、命中后其文件待删除的一方）。</summary>
    public ObservableCollection<string> SourceDirs { get; } = new();

    /// <summary>目标目录集合（命中后其文件需保留、仅作参考的一方）。</summary>
    public ObservableCollection<string> TargetDirs { get; } = new();

    /// <summary>结果列表 ListBox 当前选中的源目录。</summary>
    [ObservableProperty] private string? _selectedSourceDir;

    /// <summary>结果列表 ListBox 当前选中的目标目录。</summary>
    [ObservableProperty] private string? _selectedTargetDir;

    /// <summary>当前选中的匹配规则（三个 RadioButton 经 EnumToBoolConverter 绑定）。</summary>
    [ObservableProperty]
    private CompareMatchRule _selectedRule = CompareMatchRule.NameAndSize;

    [RelayCommand]
    private void AddSourceDir()
    {
        // PickFolders 允许多选；返回的 FolderPicker 与本类同程序集，可直接访问 SelectedPaths
        var picked = WpfUI.PickFolders("选择源目录（可多选）", true).SelectedPaths;
        foreach (var dir in picked)
            TryAddDir(dir, isSource: true);
    }

    [RelayCommand]
    private void AddTargetDir()
    {
        var picked = WpfUI.PickFolders("选择目标目录（可多选）", true).SelectedPaths;
        foreach (var dir in picked)
            TryAddDir(dir, isSource: false);
    }

    [RelayCommand]
    private void RemoveSourceDir()
    {
        if (SelectedSourceDir != null) SourceDirs.Remove(SelectedSourceDir);
    }

    [RelayCommand]
    private void RemoveTargetDir()
    {
        if (SelectedTargetDir != null) TargetDirs.Remove(SelectedTargetDir);
    }

    [RelayCommand]
    private void ClearSourceDirs() => SourceDirs.Clear();

    [RelayCommand]
    private void ClearTargetDirs() => TargetDirs.Clear();

    /// <summary>
    /// 校验并加入一个目录：
    /// 同列拒绝重复目录；源/目标两列之间拒绝互相重叠（含包含关系），命中弹 Warning 并拒绝。
    /// </summary>
    private void TryAddDir(string dir, bool isSource)
    {
        string norm;
        try { norm = CompareScanService.NormalizeDir(dir); }
        catch (Exception ex)
        {
            WpfUI.Warning("目录路径无效：\n" + dir + "\n\n" + ex.Message);
            return;
        }

        if (!Directory.Exists(norm))
        {
            WpfUI.Warning("目录不存在：\n" + norm);
            return;
        }

        var sameList = isSource ? SourceDirs : TargetDirs;
        var otherList = isSource ? TargetDirs : SourceDirs;

        // 同列重复（规整后忽略大小写）
        if (sameList.Any(d => string.Equals(
                CompareScanService.NormalizeDir(d), norm, StringComparison.OrdinalIgnoreCase)))
        {
            WpfUI.Warning("该目录已在列表中：\n" + norm);
            return;
        }

        // 源/目标互相重叠：IsSameOrUnder 双向各判一次
        foreach (var other in otherList)
        {
            var otherNorm = CompareScanService.NormalizeDir(other);
            if (CompareScanService.IsSameOrUnder(norm, otherNorm) ||
                CompareScanService.IsSameOrUnder(otherNorm, norm))
            {
                WpfUI.Warning(isSource
                    ? "源目录不能与目标目录相同或位于目标目录之内：\n" + norm
                    : "目标目录不能与源目录相同或位于源目录之内：\n" + norm);
                return;
            }
        }

        sameList.Add(norm);
    }

    // ============================================================
    // 扫描
    // ============================================================

    /// <summary>比对命中结果。</summary>
    public ObservableCollection<CompareMatchResult> Results { get; } = new();

    /// <summary>是否正在扫描（驱动控件禁用与进度区显示）。</summary>
    [ObservableProperty] private bool _isScanning;

    /// <summary>非扫描状态（供 IsEnabled 直接绑定取反）。</summary>
    public bool IsNotScanning => !IsScanning;

    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progressMaximum = 100;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;

    // 状态栏统计
    [ObservableProperty] private int _sourceFileCount;
    [ObservableProperty] private int _targetFileCount;
    [ObservableProperty] private int _cacheHits;
    [ObservableProperty] private int _excludedCount;
    [ObservableProperty] private string _excludedText = "";

    partial void OnIsScanningChanged(bool value)
        => OnPropertyChanged(nameof(IsNotScanning));

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (IsScanning) return;
        if (SourceDirs.Count == 0)
        {
            WpfUI.Warning("请至少添加一个源目录。");
            return;
        }
        if (TargetDirs.Count == 0)
        {
            WpfUI.Warning("请至少添加一个目标目录。");
            return;
        }

        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        Results.Clear();
        SourceFileCount = TargetFileCount = CacheHits = ExcludedCount = 0;
        ExcludedText = "";
        ProgressText = "准备扫描...";
        IsProgressIndeterminate = true;

        // Progress<T> 在 UI 线程构造 → 回调自动经捕获的 SynchronizationContext（Dispatcher）封送回 UI 线程
        var progress = new Progress<CompareScanService.ScanProgress>(OnScanProgress);

        var request = new CompareScanService.ScanRequest
        {
            // ScanRequest 内为字段，对象初始化器同样可用
            SourceDirs = SourceDirs.ToList(),
            TargetDirs = TargetDirs.ToList(),
            Rule = SelectedRule,
        };

        try
        {
            var outcome = await _scanService.RunScanAsync(request, progress, _scanCts.Token);

            foreach (var r in outcome.Results)
                Results.Add(r);

            SourceFileCount = outcome.SourceFileCount;
            TargetFileCount = outcome.TargetFileCount;
            CacheHits = outcome.CacheHits;
            ExcludedCount = outcome.ExcludedTargets.Count;
            ExcludedText = outcome.ExcludedTargets.Count > 0
                ? "被排重的目标目录：\n" + string.Join("\n", outcome.ExcludedTargets)
                : "";

            ProgressText = $"比对完成，命中 {outcome.Results.Count} 项。";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "已取消扫描。";
        }
        catch (Exception ex)
        {
            ProgressText = "扫描失败。";
            WpfUI.Warning("扫描过程中发生错误：\n" + ex.Message);
        }
        finally
        {
            IsScanning = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
        ProgressText = "正在取消...";
    }

    /// <summary>扫描进度回调（已在 UI 线程执行）。</summary>
    private void OnScanProgress(CompareScanService.ScanProgress p)
    {
        if (p.Total > 0)
        {
            // 富化阶段（MD5/标签）有明确总数：确定模式进度条
            IsProgressIndeterminate = false;
            ProgressMaximum = p.Total;
            ProgressValue = p.Current;
            ProgressText = $"{p.Phase}（{p.Current}/{p.Total}）";
        }
        else
        {
            // 枚举阶段无总数：不确定模式
            IsProgressIndeterminate = true;
            ProgressText = p.Current > 0 ? $"{p.Phase} 已处理 {p.Current} 个文件" : p.Phase;
        }
    }

    // ============================================================
    // 删除源文件（回收站；目标文件保留）
    // ============================================================

    /// <summary>
    /// 将结果列表中选中行对应的源文件移入回收站（目标文件保留不动）。
    /// 删除前 YesNoCancel 确认；成功后把 compare_files 状态置为 用户删除(1) 并写操作日志。
    /// 由视图代码隐藏传入 DataGrid 的多选结果（DataGrid.SelectedItems 无法直接绑定）。
    /// </summary>
    public async Task DeleteSourcesAsync(IList<CompareMatchResult> selectedRows)
    {
        if (selectedRows == null || selectedRows.Count == 0)
        {
            WpfUI.Warning("请先在结果列表中选择要删除的源文件行。");
            return;
        }

        // 同一源路径可能被多行引用（命中多个目标）：先去重
        var paths = selectedRows
            .Where(r => !string.IsNullOrWhiteSpace(r.SourcePath))
            .Select(r => r.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
        {
            WpfUI.Warning("所选行没有可删除的源路径。");
            return;
        }

        var answer = WpfUI.YesNoCancel(
            $"即将把 {paths.Count} 个源文件移入系统回收站（可在「删除历史」中还原），目标文件将原样保留。\n\n" +
            "「是」= 全部移入回收站；「否」/「取消」= 中止操作。\n\n确认继续？");
        if (answer != MessageBoxResult.Yes) return;

        var deleted = new List<string>();
        var failures = new List<string>();

        foreach (var path in paths)
        {
            bool ok;
            try
            {
                // SHFileOperation 为同步调用，放后台线程避免卡 UI
                ok = await Task.Run(() => RecycleBinService.DeleteToRecycleBin(path));
            }
            catch (Exception ex)
            {
                failures.Add(path + " —— " + ex.Message);
                continue;
            }

            if (!ok)
            {
                failures.Add(path + " —— 回收站操作失败（文件可能不存在或被占用）");
                continue;
            }

            deleted.Add(path);
            try
            {
                // 删除仅做状态标记，不物理抹除记录；日志留存命中的保留目标路径供追溯
                _db.SetStatusByPath(path, CompareFileStatus.UserDeleted, DateTime.Now.Ticks);
                var targetRefs = selectedRows
                    .Where(r => string.Equals(r.SourcePath, path, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.TargetPath)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                _db.LogDelete("file", path,
                    "比对模块移入回收站；保留的目标参考: " + string.Join(" | ", targetRefs));
            }
            catch (Exception ex)
            {
                // 文件已进回收站，但状态/日志落库失败：仍从界面移除，仅提示
                failures.Add(path + "（已入回收站，但状态记录失败：" + ex.Message + "）");
            }
        }

        // 从结果列表移除所有指向已删源的行（目标保留方不做任何改动）
        foreach (var row in Results
                     .Where(r => deleted.Contains(r.SourcePath, StringComparer.OrdinalIgnoreCase))
                     .ToList())
        {
            Results.Remove(row);
        }

        if (failures.Count > 0)
        {
            WpfUI.Warning("以下文件处理失败：\n\n" +
                          string.Join("\n", failures.Take(20)) +
                          (failures.Count > 20 ? $"\n…等共 {failures.Count} 项" : ""));
        }
    }

    // ============================================================
    // 空目录查找 / 清理（在本窗口临时面板展示，不新增窗口文件）
    // ============================================================

    /// <summary>查找到的空目录临时列表。</summary>
    public ObservableCollection<string> EmptyDirs { get; } = new();

    [ObservableProperty] private bool _showEmptyPanel;
    [ObservableProperty] private string _emptyPanelTitle = "";

    [RelayCommand]
    private async Task FindEmptyDirsAsync()
    {
        if (SourceDirs.Count == 0)
        {
            WpfUI.Warning("请先添加源目录，空目录查找在源目录范围内进行。");
            return;
        }

        ShowEmptyPanel = true;
        EmptyDirs.Clear();
        EmptyPanelTitle = "正在查找空目录...";

        try
        {
            var sources = SourceDirs.ToList();
            var found = await Task.Run(() =>
            {
                var all = new List<string>();
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var src in sources)
                {
                    foreach (var d in RecycleBinService.FindEmptyDirectories(src))
                    {
                        if (set.Add(d)) all.Add(d);
                    }
                }
                return all;
            });

            foreach (var d in found)
                EmptyDirs.Add(d);
            EmptyPanelTitle = $"源目录范围内找到 {found.Count} 个空目录（自深而浅排列）";
        }
        catch (Exception ex)
        {
            EmptyPanelTitle = "查找空目录失败";
            WpfUI.Warning("查找空目录时发生错误：\n" + ex.Message);
        }
    }

    /// <summary>删除选中的空目录（回收站），由视图代码隐藏传入 ListBox 多选结果。</summary>
    public async Task DeleteEmptyDirsAsync(IList<string> selectedDirs)
    {
        if (selectedDirs == null || selectedDirs.Count == 0)
        {
            WpfUI.Warning("请先在空目录列表中选择要删除的目录。");
            return;
        }

        var dirs = selectedDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var answer = WpfUI.YesNoCancel(
            $"即将把 {dirs.Count} 个空目录移入系统回收站。\n\n" +
            "「是」= 全部移入回收站；「否」/「取消」= 中止。\n\n确认继续？");
        if (answer != MessageBoxResult.Yes) return;

        var failures = new List<string>();
        foreach (var dir in dirs)
        {
            bool ok;
            try { ok = await Task.Run(() => RecycleBinService.DeleteToRecycleBin(dir)); }
            catch (Exception ex)
            {
                failures.Add(dir + " —— " + ex.Message);
                continue;
            }

            if (ok)
            {
                try { _db.LogDelete("empty_dir", dir, "比对模块清理空目录"); }
                catch (Exception ex)
                {
                    failures.Add(dir + "（已入回收站，但日志写入失败：" + ex.Message + "）");
                }
                EmptyDirs.Remove(dir);
            }
            else
            {
                failures.Add(dir + " —— 删除失败（目录可能非空或被占用）");
            }
        }

        if (failures.Count > 0)
            WpfUI.Warning("以下空目录处理失败：\n\n" + string.Join("\n", failures.Take(20)));
    }

    [RelayCommand]
    private void CloseEmptyPanel() => ShowEmptyPanel = false;
}
