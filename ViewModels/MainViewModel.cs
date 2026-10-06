using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.Services;
using MusicTagRecover.Services.Compare;
using MusicTagRecover.ViewModels.Compare;
using MusicTagRecover.ViewModels.Dialogs;
using MusicTagRecover.Views.Compare;
using MusicTagRecover.Views.Dialogs;
using MusicTagRecover.Win32;

namespace MusicTagRecover.ViewModels;

/// <summary>扫描目录持久化项</summary>
public class SourceDirState
{
    public string Path { get; set; } = "";
    public bool IncludeSubDir { get; set; } = true;
}

/// <summary>文件列表持久化结构（兼容旧版：旧 JSON 为纯路径数组）</summary>
public class ListViewState
{
    public List<string> Files { get; set; } = new();
    public List<SourceDirState> Dirs { get; set; } = new();
}

/// <summary>
/// 主窗口视图模型 — 1:1 承接原 MainForm + TagEditPanel 的全部业务编排。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IFileScannerService _scanner;
    private readonly ITagService _tagService;
    private readonly ILyricService _lyricService;
    private readonly ICoverService _coverService;
    private readonly AutoMatchService _autoMatch;
    private readonly FilenameRelationService _filenameRelation;
    private readonly ITagHistoryService _history;
    private readonly ILoggerService _logger;
    private readonly IImageCache _imageCache;
    private readonly CompareScanService _compareScan;
    private readonly CompareDatabaseService _compareDb;
    private readonly DirectoryHistoryService _dirHistory;

    // ===== 列表数据 =====
    public ObservableCollection<FileRowVm> Rows { get; } = new();
    private readonly List<MusicFile> _files = new();
    private readonly List<SourceDirState> _sourceDirs = new();
    private readonly HashSet<string> _hiddenDirs = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<string> HiddenDirs { get; } = new();

    /// <summary>类型过滤候选项（特殊项 + 扩展名）</summary>
    public ObservableCollection<string> FilterTypes { get; } = new() { "全部" };

    private string _sortField = "FileName";
    private bool _sortAscending = true;
    private bool _isLoadingSelection;
    /// <summary>
    /// 最近一次 DataGrid 选中行快照。标签面板（TagEditControl）内触发的命令
    /// 无法拿到 DataGrid.SelectedItems（以 null 调用），此时回退到该快照，
    /// 避免“明明选中了一个文件却提示只能选择一个文件”的误报。
    /// </summary>
    private List<FileRowVm> _currentSelectedRows = new();

    /// <summary>
    /// ApplyView 重建 Rows 期间为 true：重建会先清空集合导致 DataGrid 选区瞬间变空，
    /// 该“伪空选”不得联动清空标签面板。重建后通过 <see cref="SelectionRestoreRequired"/>
    /// 让视图层按路径恢复选区。
    /// </summary>
    private bool _isRebuildingRows;

    /// <summary>Rows 重建完成后触发：参数为重建前选中文件的路径（保持选中顺序），视图层据此恢复选区。</summary>
    public event Action<IReadOnlyList<string>>? SelectionRestoreRequired;

    /// <summary>
    /// 最近一个为标签面板补加载过封面/歌词等重资源的文件。
    /// 选中切换到其它文件后即释放其重资源，避免逐个浏览文件时内存只涨不降。
    /// </summary>
    private MusicFile? _lastDetailFile;

    // ===== 标签编辑面板 =====
    public TagEditViewModel TagEdit { get; } = new();

    // ===== 底部状态栏 =====
    [ObservableProperty] private string _filterKeyword = "";
    [ObservableProperty] private string _selectedFilterType = "全部";
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _infoText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;

    /// <summary>列设置（主窗口 code-behind 据此构建 DataGrid 列）</summary>
    public List<ColumnHeaderInfo> ColumnSettings { get; private set; } = new();

    private CompareWindow? _compareWindow;
    private DeleteHistoryWindow? _deleteHistoryWindow;

    public MainViewModel(
        ISettingsService settings,
        IFileScannerService scanner,
        ITagService tagService,
        ILyricService lyricService,
        ICoverService coverService,
        WebSearchService webSearch,
        AutoMatchService autoMatch,
        FilenameRelationService filenameRelation,
        ITagHistoryService history,
        ILoggerService logger,
        IImageCache imageCache,
        CompareScanService compareScan,
        CompareDatabaseService compareDb,
        DirectoryHistoryService dirHistory)
    {
        _settings = settings;
        _scanner = scanner;
        _tagService = tagService;
        _lyricService = lyricService;
        _coverService = coverService;
        _autoMatch = autoMatch;
        _filenameRelation = filenameRelation;
        _history = history;
        _logger = logger;
        _imageCache = imageCache;
        _compareScan = compareScan;
        _compareDb = compareDb;
        _dirHistory = dirHistory;

        WireTagEditEvents();
        ColumnSettings = LoadColumnSettings();
        LoadSortSetting();
        FilterKeyword = _settings.FilterListViewKeyword ?? "";
        SelectedFilterType = string.IsNullOrEmpty(_settings.FilterListViewType)
            ? "全部" : _settings.FilterListViewType!;
    }

    // ============================================================
    // 启动 / 拖放 / 命令行文件
    // ============================================================

    public async Task InitializeAsync(IReadOnlyList<string> args)
    {
        var incoming = args.Where(File.Exists).ToList();
        if (incoming.Count > 0)
        {
            await LoadPathsAsync(incoming);
            return;
        }

        var state = LoadListViewState();
        if (state.Files.Count > 0 || state.Dirs.Count > 0)
        {
            await LoadStateAsync(state);
        }
    }

    /// <summary>拖入文件/文件夹或 WM_COPYDATA 传入时调用：追加进列表。</summary>
    public async Task LoadPathsAsync(IEnumerable<string> paths)
    {
        var state = CurrentState();
        bool added = false;
        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                if (_scanner.IsSupportedFile(p) && state.Files.All(f =>
                        !string.Equals(f, p, StringComparison.OrdinalIgnoreCase)))
                {
                    state.Files.Add(p);
                    added = true;
                }
            }
            else if (Directory.Exists(p))
            {
                state.Dirs.Add(new SourceDirState { Path = p, IncludeSubDir = _settings.IncludeSubDir });
                added = true;
            }
        }
        if (added) await LoadStateAsync(state);
    }

    private ListViewState CurrentState() => new()
    {
        Files = _files.Where(f => _sourceDirs.All(d => !f.FilePath.StartsWith(
                    d.Path.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.FilePath).ToList(),
        Dirs = _sourceDirs.ToList(),
    };

    private async Task LoadStateAsync(ListViewState state)
    {
        await RunBusyAsync("正在枚举文件…", true, async () =>
        {
            // 1) 后台枚举（目录扫描 + 独立文件）
            var found = await Task.Run(() => EnumeratePaths(state));
            var files = found
                .GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            StatusText = $"正在读取标签…（0/{files.Count}）";

            // 2) 有界并行读标签（并行度按 CPU 核数自适应，轻量读取：只取元数据 + 封面计数/歌词标志，不加载封面字节与歌词正文）
            //    Parallel.ForEachAsync 按需拉取工作项，避免一次性物化上千个 Task/闭包带来的额外堆分配
            int done = 0;
            await Parallel.ForEachAsync(files,
                new ParallelOptions { MaxDegreeOfParallelism = FileScannerService.ScanParallelism },
                async (file, _) =>
                {
                    try { var tags = await _tagService.ReadTagsSummaryAsync(file.FilePath); ApplyTags(file, tags); }
                    catch { return; }
                    var n = Interlocked.Increment(ref done);
                    if (n % 200 == 0 || n == files.Count)
                        StatusText = $"正在读取标签…（{n}/{files.Count}）";
                });

            // 3) 提交到 UI：先断开旧行（连带标签面板内旧封面/歌词），再替换数据源
            Rows.Clear();
            TagEdit.Clear();
            _currentSelectedRows = new List<FileRowVm>();
            _lastDetailFile = null;
            // 主动释放旧目录每个文件的封面字节/歌词（多数位于 LOH），随后强制压缩式 GC，
            // 使切换到小文件夹后工作集快速回落，而不是等待运行时惰性回收
            foreach (var old in _files) old.ReleaseDetails();
            _files.Clear();
            _files.AddRange(files);
            _sourceDirs.Clear();
            _sourceDirs.AddRange(state.Dirs);
            RebuildFilterTypes();
            ApplyView();
            SaveListViewState();
            StatusText = $"已加载 {_files.Count} 个文件";

            RequestMemoryTrim();
        });
    }

    /// <summary>
    /// 目录切换/大批重资源释放后：后台执行一次压缩式完整 GC，
    /// 并把进程工作集交还给 OS，使任务管理器中内存占用迅速回落。
    /// 仅在切目录等离散时机调用，避免频繁 GC 影响交互流畅度。
    /// </summary>
    private static void RequestMemoryTrim()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300);
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                NativeMethods.TrimWorkingSet();
            }
            catch { /* 内存回收仅为优化，失败可忽略 */ }
        });
    }

    private List<MusicFile> EnumeratePaths(ListViewState state)
    {
        var result = new List<MusicFile>();
        foreach (var file in state.Files)
        {
            var mf = _scanner.AddFile(file);
            if (mf != null) result.Add(mf);
        }
        foreach (var dir in state.Dirs)
        {
            try
            {
                var enumerationOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = dir.IncludeSubDir,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System,
                };
                foreach (var path in Directory.EnumerateFiles(dir.Path, "*.*", enumerationOptions))
                {
                    if (_scanner.IsSupportedFile(path))
                    {
                        var mf = _scanner.AddFile(path);
                        if (mf != null) result.Add(mf);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"枚举目录失败：{dir.Path} - {ex.Message}");
            }
        }
        return result;
    }

    [RelayCommand]
    private async Task ChangeWorkingDirectory()
    {
        var picker = new FolderPicker
        {
            Title = "选择音乐文件夹",
            AllowMultiSelect = true,
            IncludeSubDirectories = _settings.IncludeSubDir,
        };
        if (!picker.ShowDialog(Application.Current.MainWindow)) return;
        _settings.IncludeSubDir = picker.IncludeSubDirectoriesChecked;
        var state = CurrentState();
        foreach (var dir in picker.SelectedPaths)
        {
            if (state.Dirs.All(d => !string.Equals(d.Path, dir, StringComparison.OrdinalIgnoreCase)))
                state.Dirs.Add(new SourceDirState { Path = dir, IncludeSubDir = picker.IncludeSubDirectoriesChecked });
        }
        await LoadStateAsync(state);
    }

    [RelayCommand]
    private async Task Refresh()
    {
        if (_sourceDirs.Count == 0 && _files.Count == 0) return;
        await LoadStateAsync(CurrentState());
    }

    /// <summary>
    /// 文件菜单「添加目录」：选择一个本地目录写入目录历史库，
    /// 并把当前工作目录切换为该新目录（列表仅加载该目录）。
    /// </summary>
    [RelayCommand]
    private async Task AddDirectory()
    {
        var picker = new FolderPicker
        {
            Title = "选择要添加的本地目录",
            AllowMultiSelect = false,
            IncludeSubDirectories = _settings.IncludeSubDir,
        };
        if (picker.ShowDialog(Application.Current.MainWindow) != true) return;

        var dir = picker.SelectedPaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        var includeSub = picker.IncludeSubDirectoriesChecked;
        _settings.IncludeSubDir = includeSub;
        _dirHistory.Add(dir, includeSub);
        _dirHistory.Touch(new[] { dir });
        await SwitchToDirectories(new List<SourceDirState>
        {
            new() { Path = dir, IncludeSubDir = includeSub }
        });
    }

    /// <summary>
    /// 文件菜单「管理目录」：读取库中全部历史目录，多选确认后
    /// 将当前工作目录切换为选中目录；同时支持删除历史记录。
    /// </summary>
    [RelayCommand]
    private async Task ManageDirectories()
    {
        var win = new DirectoryManageWindow(_dirHistory)
        {
            Owner = Application.Current.MainWindow
        };
        if (win.ShowDialog() != true || win.Result.Count == 0) return;

        var dirs = win.Result
            .Select(e => new SourceDirState { Path = e.Path, IncludeSubDir = e.IncludeSubDir })
            .ToList();
        _dirHistory.Touch(dirs.Select(d => d.Path));
        await SwitchToDirectories(dirs);
    }

    /// <summary>以指定目录集合作为唯一工作来源重新加载（散落文件与旧目录全部替换）。</summary>
    private async Task SwitchToDirectories(List<SourceDirState> dirs)
    {
        _settings.IncludeSubDir = dirs.Count > 0 && dirs.All(d => d.IncludeSubDir);
        await LoadStateAsync(new ListViewState { Dirs = dirs });
    }

    // ============================================================
    // 过滤 / 排序 / 视图
    // ============================================================

    partial void OnFilterKeywordChanged(string value)
    {
        _settings.FilterListViewKeyword = value;
        ApplyView();
    }

    partial void OnSelectedFilterTypeChanged(string value)
    {
        _settings.FilterListViewType = value == "全部" ? null : value;
        ApplyView();
    }

    /// <summary>重新应用过滤+排序并填充 Rows（保留当前选择由视图层处理）。</summary>
    public void ApplyView()
    {
        IEnumerable<MusicFile> view = _files;

        if (_hiddenDirs.Count > 0)
            view = view.Where(f => !_hiddenDirs.Contains(f.Directory));

        var type = SelectedFilterType ?? "全部";
        if (type.StartsWith("有封面")) view = view.Where(f => f.HasCoverArt);
        else if (type.StartsWith("无封面")) view = view.Where(f => !f.HasCoverArt);
        else if (type.StartsWith("有歌词")) view = view.Where(f => f.HasLyrics);
        else if (type.StartsWith("无歌词")) view = view.Where(f => !f.HasLyrics);
        else if (type.StartsWith("已修改")) view = view.Where(f => f.IsModified);
        else if (type != "全部" && type.StartsWith('.'))
            view = view.Where(f => f.Extension.Equals(type, StringComparison.OrdinalIgnoreCase));

        var sorted = _scanner.SortFiles(view, _sortField, _sortAscending);

        // 记录重建前选区；重建期间屏蔽选区联动，避免面板被“伪空选”清空（保存标签后的典型场景）
        var selectedPaths = _currentSelectedRows
            .Where(r => r.File != null)
            .Select(r => r.File.FilePath)
            .ToList();

        _isRebuildingRows = true;
        try
        {
            Rows.Clear();
            foreach (var f in sorted) Rows.Add(new FileRowVm(f));
        }
        finally
        {
            _isRebuildingRows = false;
        }
        UpdateInfo(sorted);

        if (selectedPaths.Count > 0)
            SelectionRestoreRequired?.Invoke(selectedPaths);
    }

    private void RebuildFilterTypes()
    {
        FilterTypes.Clear();
        foreach (var t in new[]
        {
            "全部", "有封面", "无封面", "有歌词", "无歌词", "已修改"
        }) FilterTypes.Add(t);
        foreach (var ext in _files.Select(f => f.Extension).Distinct()
                     .OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
            FilterTypes.Add(ext);
    }

    private void UpdateInfo(IReadOnlyCollection<MusicFile> visibleFiles)
    {
        // 单次遍历统计，避免原来 Rows.Any 的 O(n²)（万级文件时明显卡顿）
        long totalMs = 0;
        long totalSize = 0;
        foreach (var f in visibleFiles)
        {
            totalMs += (long)f.Duration.TotalMilliseconds;
            totalSize += f.FileSize;
        }
        var dur = TimeSpan.FromMilliseconds(totalMs);
        InfoText = $"{visibleFiles.Count} 项 | {FormatDuration(dur)} | {FileSizeConverter.FormatSize(totalSize)}";
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}小时{t.Minutes}分{t.Seconds}秒"
        : t.Minutes > 0 ? $"{t.Minutes}分{t.Seconds}秒" : $"{t.Seconds}秒";

    /// <summary>列头点击排序。</summary>
    public void SortBy(string columnName)
    {
        if (!ColumnHeaderInfo.SortFields.TryGetValue(columnName, out var field)) return;
        if (_sortField == field) _sortAscending = !_sortAscending;
        else { _sortField = field; _sortAscending = true; }
        _settings.SortSetting = $"{_sortField}|{_sortAscending}";
        ApplyView();
    }

    public (string Field, bool Ascending) CurrentSort => (_sortField, _sortAscending);

    private void LoadSortSetting()
    {
        var s = _settings.SortSetting;
        if (string.IsNullOrEmpty(s)) return;
        var parts = s.Split('|');
        if (parts.Length >= 1 && !string.IsNullOrEmpty(parts[0])) _sortField = parts[0];
        if (parts.Length >= 2 && bool.TryParse(parts[1], out var asc)) _sortAscending = asc;
    }

    // ============================================================
    // 选中联动标签面板
    // ============================================================

    /// <summary>DataGrid SelectionChanged 时由视图调用。</summary>
    public async Task OnSelectionChanged(IList selectedItems)
    {
        // 重建行集合时的“伪空选”不联动面板（视图随后会恢复真实选区）
        if (_isLoadingSelection || _isRebuildingRows) return;
        _isLoadingSelection = true;
        try
        {
            var rows = selectedItems.Cast<FileRowVm>().ToList();
            _currentSelectedRows = rows;
            if (rows.Count == 0)
            {
                ReleasePreviousDetail(null);
                TagEdit.Clear();
            }
            else if (rows.Count == 1)
            {
                var file = rows[0].File;
                await RunBusyAsync("正在读取标签…", false, async () =>
                {
                    // 按需从磁盘补读封面/歌词等重资源（扫描阶段只保留了元数据与计数）
                    await EnsureDetailsLoadedAsync(file);
                    rows[0].Refresh();
                    TagEdit.LoadSingle(file);
                    // 面板已持有封面的独立副本，此后释放上一个浏览文件的重资源
                    ReleasePreviousDetail(file);
                    _lastDetailFile = file;
                });
            }
            else
            {
                ReleasePreviousDetail(null);
                TagEdit.LoadKeepMode();
            }
            UpdateSelectionStatus(rows.Count);
        }
        finally { _isLoadingSelection = false; }
    }

    private DateTime _lastDetailTrimAt = DateTime.MinValue;

    /// <summary>
    /// 释放上一个浏览文件的封面字节/歌词（面板持有自己的副本，不受影响）。
    /// 连续浏览时做节流（≥15 秒一次）的后台 Gen2 压缩回收，避免内存只涨不降。
    /// </summary>
    private void ReleasePreviousDetail(MusicFile? keep)
    {
        if (_lastDetailFile == null || _lastDetailFile == keep) return;
        _lastDetailFile.ReleaseDetails();
        _lastDetailFile = null;

        if (DateTime.UtcNow - _lastDetailTrimAt > TimeSpan.FromSeconds(15))
        {
            _lastDetailTrimAt = DateTime.UtcNow;
            _ = Task.Run(() =>
            {
                try { GC.Collect(2, GCCollectionMode.Forced, false, true); }
                catch { /* 忽略 */ }
            });
        }
    }

    private void UpdateSelectionStatus(int count)
    {
        StatusText = count == 0 ? "就绪" : $"已选择 {count} 项";
    }

    private List<FileRowVm> GetSelected(IList? selectedItems)
    {
        // 非 null（菜单/工具栏/快捷键传入 DataGrid.SelectedItems）以传入为准，
        // 空列表即“未选择”；null（标签面板内部命令）回退到最近一次选中快照。
        if (selectedItems != null)
            return selectedItems.Cast<FileRowVm>().ToList();
        return _currentSelectedRows
            .Where(r => Rows.Contains(r))
            .ToList();
    }

    // ============================================================
    // 标签保存 / 清除 / 重读
    // ============================================================

    [RelayCommand]
    private async Task Save(IList? selectedItems)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0)
        {
            WpfUI.Warning("请先选择要保存标签的文件。");
            return;
        }

        var data = TagEdit.BuildTagData();
        var coverChanged = TagEdit.CoverChanged;
        await RunBusyAsync("正在保存标签…", true, async () =>
        {
            int ok = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var file = row.File;
                var isPanelRow = rows.Count == 1;
                try
                {
                    // 历史快照与“追加封面”合并需要原始封面/歌词：按需补读（单文件即面板当前文件）
                    var needDetails = isPanelRow || coverChanged || file.HasCoverArt || file.HasLyrics;
                    if (needDetails) await EnsureDetailsLoadedAsync(file);

                    var perFile = CloneTagData(data);
                    // 多选 + 封面追加模式：保留原封面再追加
                    if (rows.Count > 1 && coverChanged && !TagEdit.OverwriteExisting
                        && file.AllPictures is { Count: > 0 })
                    {
                        perFile.AllPictures = file.AllPictures
                            .Where(p => p.HasImage)
                            .Concat(TagEdit.EditedPictures).ToList();
                    }

                    _history.TryAddHistory(file.FilePath, file);
                    if (await _tagService.WriteTagsAsync(file.FilePath, perFile,
                            _settings.SaveTagsKeepUpdateTime))
                    {
                        file.IsModified = false;
                        // 面板当前文件全量重读（含封面）；批量中其余文件只做轻量刷新，避免整批封面常驻内存
                        if (isPanelRow)
                        {
                            var full = await _tagService.ReadTagsAsync(file.FilePath);
                            ApplyTags(file, full);
                        }
                        else
                        {
                            var summary = await _tagService.ReadTagsSummaryAsync(file.FilePath);
                            ApplyTags(file, summary);
                        }
                        row.Refresh();
                        ok++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "保存标签失败：{0}", file.FilePath);
                }
            }
            TagEdit.ResetModifiedFlags();
            if (rows.Count == 1)
            {
                _lastDetailFile = rows[0].File;
                TagEdit.LoadSingle(rows[0].File);
            }
            ApplyView();
            StatusText = $"保存完成：{ok}/{rows.Count}";
        });
    }

    private static TagData CloneTagData(TagData src) => new()
    {
        Title = src.Title, Artist = src.Artist, Album = src.Album, Year = src.Year,
        Track = src.Track, TrackCount = src.TrackCount, Genre = src.Genre, Comment = src.Comment,
        AlbumArtist = src.AlbumArtist, Composer = src.Composer, Lyricist = src.Lyricist,
        Disc = src.Disc, DiscCount = src.DiscCount, Lyrics = src.Lyrics,
        CoverArtData = src.CoverArtData, CoverArtMimeType = src.CoverArtMimeType,
        CoverArtType = src.CoverArtType,
        AllPictures = src.AllPictures?.Select(p => new CoverArt
        {
            ImageData = p.ImageData,
            MimeType = p.MimeType,
            Width = p.Width,
            Height = p.Height,
            PictureType = p.PictureType,
        }).ToList(),
    };

    [RelayCommand]
    private async Task ClearTags(IList? selectedItems)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0) return;
        if (WpfUI.YesNo($"确定清除 {rows.Count} 个文件的全部标签？此操作可在标签历史中恢复部分内容。")
            != MessageBoxResult.Yes) return;

        await RunBusyAsync("正在清除标签…", true, async () =>
        {
            int ok = 0;
            foreach (var row in rows)
            {
                try
                {
                    // 历史快照需要原始封面/歌词：按需补读（峰值仅一个文件的重资源）
                    await EnsureDetailsLoadedAsync(row.File);
                    _history.TryAddHistory(row.File.FilePath, row.File);
                    if (await _tagService.ClearTagsAsync(row.File.FilePath))
                    {
                        var tags = await _tagService.ReadTagsSummaryAsync(row.File.FilePath);
                        ApplyTags(row.File, tags);
                        row.Refresh();
                        ok++;
                    }
                }
                catch (Exception ex) { _logger.Error(ex, "清除标签失败：{0}", row.File.FilePath); }
            }
            ApplyView();
            TagEdit.Clear();
            _lastDetailFile = null;
            StatusText = $"已清除 {ok}/{rows.Count} 个文件的标签";
        });
    }

    [RelayCommand]
    private async Task ReadTags(IList? selectedItems)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0) return;
        await RunBusyAsync("正在重读标签…", true, async () =>
        {
            // 单选：全量读取供面板编辑；多选：仅轻量刷新，避免大批封面字节常驻内存
            if (rows.Count == 1)
            {
                var file = rows[0].File;
                try
                {
                    var tags = await _tagService.ReadTagsAsync(file.FilePath);
                    ApplyTags(file, tags);
                }
                catch (Exception ex) { _logger.Warn($"重读失败：{file.FilePath} - {ex.Message}"); }
                rows[0].Refresh();
                _lastDetailFile = file;
                TagEdit.LoadSingle(file);
            }
            else
            {
                foreach (var row in rows)
                {
                    try
                    {
                        var tags = await _tagService.ReadTagsSummaryAsync(row.File.FilePath);
                        ApplyTags(row.File, tags);
                        row.Refresh();
                    }
                    catch (Exception ex) { _logger.Warn($"重读失败：{row.File.FilePath} - {ex.Message}"); }
                }
            }
            StatusText = "标签已从磁盘重新读取";
        });
    }

    // ============================================================
    // 删除 / 重命名
    // ============================================================

    /// <summary>Delete：仅从列表移除。</summary>
    public void RemoveRows(IList selectedItems)
    {
        var rows = GetSelected(selectedItems);
        foreach (var row in rows)
        {
            _files.Remove(row.File);
            Rows.Remove(row);
        }
        SaveListViewState();
        ApplyView();
    }

    /// <summary>Shift+Delete：移入回收站。</summary>
    [RelayCommand]
    private async Task DeleteFiles(IList? selectedItems)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0) return;
        if (WpfUI.YesNo($"确定将 {rows.Count} 个文件移入回收站？") != MessageBoxResult.Yes) return;
        await RunBusyAsync("正在删除文件…", true, async () =>
        {
            int ok = await _scanner.DeleteFilesAsync(rows.Select(r => r.File));
            foreach (var row in rows)
            {
                _files.Remove(row.File);
                Rows.Remove(row);
                _history.DeleteHistoryByFilePath(row.File.FilePath);
            }
            SaveListViewState();
            TagEdit.Clear();
            StatusText = $"已移入回收站：{ok}/{rows.Count}";
        });
    }

    /// <summary>F2 内联重命名提交；newFileName 可不含扩展名（自动补）。</summary>
    public async Task<bool> CommitRenameAsync(FileRowVm row, string newFileName)
    {
        newFileName = newFileName.Trim();
        if (string.IsNullOrEmpty(newFileName)) return false;
        if (Path.GetExtension(newFileName).Length == 0) newFileName += row.File.Extension;
        if (string.Equals(newFileName, row.File.FileName, StringComparison.Ordinal)) return false;

        var dir = row.File.Directory;
        var newPath = Path.Combine(dir, newFileName);
        if (File.Exists(newPath))
        {
            WpfUI.Warning("目标文件已存在：" + newFileName);
            return false;
        }
        try
        {
            File.Move(row.File.FilePath, newPath);
            row.File.FilePath = newPath;
            row.Refresh();
            SaveListViewState();
            return true;
        }
        catch (Exception ex)
        {
            WpfUI.Warning("重命名失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 右键菜单「编码修正」：对单个文件的全部文本字段批量修复乱码。
    /// 结果先写回右侧标签编辑面板（标记为已修改），用户点「保存修改」后落盘。
    /// </summary>
    [RelayCommand]
    private void EncodingFixSelected(IList? selectedItems)
    {
        var row = RequireSingleRow(selectedItems);
        if (row == null) return;
        if (!File.Exists(row.File.FilePath))
        {
            WpfUI.Warning("文件不存在或已被移动/删除：\n" + row.File.FilePath);
            return;
        }

        // 以面板当前内容为准（面板在单选时已载入该文件标签）；仅收集非空文本字段
        var fields = TagEditViewModel.FieldDefs
            .Select(fd => TagEdit.GetField(fd.Key)!)
            .Where(f => !string.IsNullOrWhiteSpace(f.Text))
            .Select(f => new KeyValuePair<string, string>(f.Label, f.Text))
            .ToList();
        if (fields.Count == 0)
        {
            WpfUI.Warning("该文件没有可修正的文本标签字段。");
            return;
        }

        var win = new EncodingFixWindow(fields)
        {
            Owner = Application.Current.MainWindow
        };
        if (win.ShowDialog() != true || !win.Applied) return;

        // 中文标签名 → 字段 key 映射后写回面板
        foreach (var (label, fixedText) in win.FixedFields)
        {
            var key = TagEditViewModel.FieldDefs
                .FirstOrDefault(fd => fd.Label == label).Key;
            if (key != null) TagEdit.GetField(key)!.Text = fixedText;
        }
        StatusText = "编码修正已应用到标签面板，请点击「保存修改」写入文件";
    }

    /// <summary>
    /// 右键菜单「DFF 原生标签（DIIN/COMT）」：管理 DSDIFF 原生字段，支持多文件批量
    /// 修改/删除。窗口关闭后对发生变更的文件重读摘要，同步刷新列表与标签面板（底层数据模型）。
    /// </summary>
    [RelayCommand]
    private async Task OpenDffNativeTags(IList? selectedItems)
    {
        var rows = GetSelected(selectedItems);
        var dffRows = rows.Where(r => r.File.Extension == ".dff").ToList();
        if (dffRows.Count == 0)
        {
            WpfUI.Warning("请先选择一个或多个 DFF 文件，再进行原生标签（DIIN/COMT）管理。");
            return;
        }

        var skipped = rows.Count - dffRows.Count;
        var paths = dffRows.Select(r => r.File.FilePath).ToList();

        HashSet<string> changed;
        try
        {
            changed = DffNativeTagsWindow.Show(paths, _logger);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开 DFF 原生标签管理窗口失败");
            WpfUI.Error("打开原生标签管理窗口失败：" + ex.Message);
            return;
        }

        if (changed.Count == 0)
        {
            if (skipped > 0) StatusText = $"已跳过 {skipped} 个非 DFF 文件，未发生变更";
            return;
        }

        await RunBusyAsync("正在刷新标签数据…", true, async () =>
        {
            foreach (var row in dffRows)
            {
                if (!changed.Contains(row.File.FilePath)) continue;
                try
                {
                    var tags = await _tagService.ReadTagsSummaryAsync(row.File.FilePath);
                    ApplyTags(row.File, tags);
                    row.Refresh();
                }
                catch (Exception ex)
                {
                    _logger.Warn($"原生标签变更后刷新失败：{row.File.FilePath} - {ex.Message}");
                }
            }
            // 单文件操作时同步右侧编辑面板，保证面板与磁盘一致
            if (dffRows.Count == 1)
            {
                _lastDetailFile = dffRows[0].File;
                TagEdit.LoadSingle(dffRows[0].File);
            }
            ApplyView();
            StatusText = $"DFF 原生标签已更新：{changed.Count} 个文件" +
                         (skipped > 0 ? $"（已跳过 {skipped} 个非 DFF 文件）" : "");
        });
    }

    /// <summary>右键菜单「打开文件目录」：资源管理器定位并选中文件（多选时定位首个）。</summary>
    [RelayCommand]
    private void OpenFileLocation(IList? selectedItems)
    {
        var row = GetSelected(selectedItems).FirstOrDefault();
        if (row == null)
        {
            WpfUI.Warning("请先选择一个文件。");
            return;
        }
        var path = row.File.FilePath;
        if (!File.Exists(path))
        {
            // 文件已删除时退而打开其所在目录
            var dir = row.File.Directory;
            if (Directory.Exists(dir))
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
                catch (Exception ex) { WpfUI.Warning("打开目录失败：" + ex.Message); }
            }
            else WpfUI.Warning("文件和所在目录都不存在：\n" + path);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WpfUI.Warning("打开文件目录失败：" + ex.Message);
        }
    }

    // ============================================================
    // 目录显隐
    // ============================================================

    public void HideSelectedDirectories(IList selectedItems)
    {
        foreach (var dir in GetSelected(selectedItems).Select(r => r.File.Directory).Distinct())
        {
            if (_hiddenDirs.Add(dir) && !HiddenDirs.Contains(dir)) HiddenDirs.Add(dir);
        }
        ApplyView();
    }

    [RelayCommand]
    private void RestoreDirectory(string path)
    {
        _hiddenDirs.Remove(path);
        HiddenDirs.Remove(path);
        ApplyView();
    }

    // ============================================================
    // 标签源搜索（单文件）
    // ============================================================

    private FileRowVm? RequireSingleRow(IList? selectedItems)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count != 1)
        {
            WpfUI.Warning("该操作需要且只能选择一个文件。");
            return null;
        }
        return rows[0];
    }

    [RelayCommand]
    private async Task SearchPicture(IList? selectedItems)
        => await OpenSearchAsync(selectedItems, source: null);

    /// <summary>图片源子菜单（7 源）</summary>
    public async Task OpenPictureSearch(IList? selectedItems, string source)
    {
        var row = RequireSingleRow(selectedItems);
        if (row == null) return;
        await RunBusyAsync(null, false, async () =>
        {
            var win = new PictureSearchWindow(row.File, source, _coverService, _settings, _imageCache)
            {
                Owner = Application.Current.MainWindow
            };
            if (win.ShowDialog() == true && win.SelectedCover != null)
                TagEdit.AddPictures(new[] { win.SelectedCover }, TagEdit.OverwriteExisting);
        });
    }

    private Task OpenSearchAsync(IList? selectedItems, string? source)
        => OpenPictureSearch(selectedItems, source ?? FirstEnabledSource(TagSourceCategory.Picture));

    /// <summary>歌词源（3 源）</summary>
    public async Task OpenLyricsSearch(IList? selectedItems, string source)
    {
        var row = RequireSingleRow(selectedItems);
        if (row == null) return;
        await RunBusyAsync(null, false, async () =>
        {
            var win = new LyricsSearchWindow(row.File, source, _lyricService, _tagService, _settings)
            {
                Owner = Application.Current.MainWindow
            };
            if (win.ShowDialog() == true && win.Saved)
            {
                // 歌词已在窗口内直接写入标签，这里仅同步列表行与编辑面板
                row.Refresh();
                TagEdit.LoadLyricsExternal(row.File.Lyrics ?? "");
            }
        });
    }

    /// <summary>组合标签搜索（图片源键）</summary>
    public async Task OpenTagSearch(IList? selectedItems, string source)
    {
        var row = RequireSingleRow(selectedItems);
        if (row == null) return;
        await RunBusyAsync(null, false, async () =>
        {
            var win = new TagSearchWindow(row.File, source, _coverService, _lyricService,
                _imageCache, _settings) { Owner = Application.Current.MainWindow };
            if (win.ShowDialog() != true || win.SelectedResult == null) return;

            var r = win.SelectedResult;
            SetFieldIfPresent("title", r.Title);
            SetFieldIfPresent("artist", r.Artist);
            SetFieldIfPresent("album", r.Album);
            SetFieldIfPresent("year", r.Year);
            SetFieldIfPresent("genre", r.Genre);
            foreach (var pair in r.ExtraFields)
            {
                var key = pair.Key.ToLowerInvariant() switch
                {
                    "title" => "title", "artist" => "artist", "album" => "album",
                    "year" => "year", "genre" => "genre", "albumartist" => "albumartist",
                    "composer" => "composer", "lyricist" => "lyricist",
                    "track" or "tracknumber" => "track", "disc" or "discnumber" => "disc",
                    _ => null
                };
                if (key != null) SetFieldIfPresent(key, pair.Value);
            }
            if (!string.IsNullOrEmpty(r.CoverTempPath))
            {
                var bytes = _imageCache.ReadHistory(r.CoverTempPath!);
                if (bytes != null)
                    TagEdit.AddPictures(new[] { new CoverArt { ImageData = bytes } },
                        TagEdit.OverwriteExisting);
            }
        });
    }

    private void SetFieldIfPresent(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var field = TagEdit.GetField(key);
        if (field != null && !field.IsModified) field.Text = value.Trim();
    }

    private string FirstEnabledSource(TagSourceCategory category)
    {
        var json = category switch
        {
            TagSourceCategory.Picture => _settings.PictureInfo_SourceItemList,
            TagSourceCategory.Lyrics => _settings.LyricInfo_SourceItemList,
            _ => _settings.CombTagsInfo_SourceItemList,
        };
        var items = TagSourceCatalog.Load(json, category);
        return items.FirstOrDefault(i => i.Enabled)?.Key ?? "netease";
    }

    [RelayCommand]
    private async Task EditLyrics(IList? selectedItems)
    {
        var row = RequireSingleRow(selectedItems);
        if (row == null) return;
        await RunBusyAsync(null, false, async () =>
        {
            var win = new LyricsEditWindow(row.File, _lyricService, _tagService, _settings)
            {
                Owner = Application.Current.MainWindow
            };
            if (win.ShowDialog() == true && win.Saved)
            {
                var text = await _tagService.ReadLyricsAsync(row.File.FilePath);
                row.File.Lyrics = text;
                row.File.HasLyrics = !string.IsNullOrEmpty(text);
                row.Refresh();
                TagEdit.LoadLyricsExternal(text ?? "");
            }
        });
    }

    [RelayCommand]
    private void OpenTagHistory(IList? selectedItems)
    {
        var row = RequireSingleRow(selectedItems);
        if (row == null) return;
        var records = _history.GetHistory(row.File.FilePath);
        var win = new TagHistoryWindow(_history, row.File.FilePath, records)
        {
            Owner = Application.Current.MainWindow
        };
        if (win.ShowDialog() == true && win.SelectedRecord != null)
            RestoreHistoryRecord(row, win.SelectedRecord, win.SelectedCoverData);
    }

    private async void RestoreHistoryRecord(FileRowVm row, TagHistoryRecord record, byte[]? cover)
    {
        await RunBusyAsync("正在恢复历史标签…", false, async () =>
        {
            var data = new TagData
            {
                Title = record.Title,
                Artist = record.Artist,
                Album = record.Album,
                Year = uint.TryParse(record.Year, out var y) ? y : null,
                Genre = record.Genre,
                AlbumArtist = record.AlbumArtist,
                Composer = record.Composer,
                Lyricist = record.Lyricist,
                Comment = record.Comment,
                Lyrics = record.Lyrics,
            };
            var (t, tc) = ParseTrackLike(record.TrackStr);
            data.Track = t; data.TrackCount = tc;
            var (d, dc) = ParseTrackLike(record.DiscStr);
            data.Disc = d; data.DiscCount = dc;
            if (cover != null)
                data.AllPictures = new List<CoverArt> { new() { ImageData = cover } };

            _history.TryAddHistory(row.File.FilePath, row.File);
            if (await _tagService.WriteTagsAsync(row.File.FilePath, data, _settings.SaveTagsKeepUpdateTime))
            {
                var tags = await _tagService.ReadTagsAsync(row.File.FilePath);
                ApplyTags(row.File, tags);
                row.Refresh();
                _lastDetailFile = row.File;
                TagEdit.LoadSingle(row.File);
            }
        });
    }

    private static (uint?, uint?) ParseTrackLike(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var slash = text.IndexOf('/');
        uint? Parse(string s) => uint.TryParse(s.Trim(), out var v) ? v : null;
        return slash < 0 ? (Parse(text), null) : (Parse(text[..slash]), Parse(text[(slash + 1)..]));
    }

    // ============================================================
    // 批量：歌词操作
    // ============================================================

    [RelayCommand]
    private async Task LyricsReformatTimeline(IList? selectedItems)
        => await BatchTransformLyrics(selectedItems, l => _lyricService.ReformatTimetag(l));

    [RelayCommand]
    private async Task LyricsRemoveTimeline(IList? selectedItems)
        => await BatchTransformLyrics(selectedItems, l =>
            Regex.Replace(l, @"\[\d{2}:\d{2}\.\d{2,3}\]", ""));

    [RelayCommand]
    private async Task LyricsRemoveBlankLines(IList? selectedItems)
        => await BatchTransformLyrics(selectedItems, l =>
            Regex.Replace(l, @"^[ \t]*\r?\n", "", RegexOptions.Multiline));

    [RelayCommand]
    private async Task LyricsRemoveHeadTags(IList? selectedItems)
        => await BatchTransformLyrics(selectedItems, l =>
            Regex.Replace(l, @"^[ \t]*(?:ti|ar|al|by|offset)\s*:.*\r?\n", "",
                RegexOptions.Multiline | RegexOptions.IgnoreCase));

    private async Task BatchTransformLyrics(IList? selectedItems, Func<string, string> transform)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0) return;
        await RunBusyAsync("正在批量处理歌词…", true, async () =>
        {
            int ok = 0;
            foreach (var row in rows)
            {
                // 轻量扫描时歌词正文不在内存：歌词操作只需读取歌词文本（不加载封面字节）
                var loadedLyricsHere = false;
                if (!row.File.DetailsLoaded && row.File.Lyrics == null)
                {
                    var lyric = await _tagService.ReadLyricsAsync(row.File.FilePath);
                    row.File.Lyrics = lyric;
                    row.File.HasLyrics = !string.IsNullOrEmpty(lyric);
                    loadedLyricsHere = true;
                }
                if (string.IsNullOrEmpty(row.File.Lyrics))
                {
                    if (loadedLyricsHere) row.File.Lyrics = null;
                    continue;
                }
                try
                {
                    var updated = transform(row.File.Lyrics!);
                    _history.TryAddHistory(row.File.FilePath, row.File);
                    if (await _tagService.WriteLyricsAsync(row.File.FilePath, updated))
                    {
                        row.File.Lyrics = updated;
                        row.File.HasLyrics = true;
                        row.Refresh();
                        ok++;
                    }
                }
                catch (Exception ex) { _logger.Warn($"歌词处理失败 {row.File.FilePath}: {ex.Message}"); }
                // 仅临时补读的文件：丢弃歌词文本，保持内存轻量（磁盘已写入，需要时再读）
                if (loadedLyricsHere && row.File != _lastDetailFile) row.File.Lyrics = null;
            }
            StatusText = $"歌词处理完成：{ok}/{rows.Count}";
        });
    }

    [RelayCommand]
    private async Task SaveLrc(IList? selectedItems)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0) return;
        var dir = _settings.SaveLrcDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            var picker = WpfUI.PickFolders("选择 LRC 保存目录", false);
            if (picker.SelectedPaths.Count == 0) return;
            dir = picker.SelectedPaths[0];
            _settings.SaveLrcDirectory = dir;
        }
        await RunBusyAsync("正在另存 LRC…", true, async () =>
        {
            int ok = 0;
            var saveConfig = new LyricInfo.SaveConfig
            {
                SaveDirectory = dir!,
                FileDefaultEncoding = _settings.SaveLrcFileDefaultEncoding ?? "utf-8",
                FilenameFormat = _settings.SaveLrcFilenameFormat ?? "{artist} - {title}.lrc",
            };
            foreach (var row in rows)
            {
                // 导出不需要封面：优先用已加载歌词，否则只读歌词文本
                string? lyric = row.File.Lyrics;
                if (string.IsNullOrEmpty(lyric) && row.File.HasLyrics)
                    lyric = await _tagService.ReadLyricsAsync(row.File.FilePath);
                if (string.IsNullOrEmpty(lyric)) continue;
                var info = new LyricInfo { OriginalLyric = lyric };
                var path = await _lyricService.SaveLrcFileAsync(dir!, row.File, info, saveConfig);
                if (path != null) ok++;
            }
            WpfUI.Info($"已保存 {ok}/{rows.Count} 个 LRC 文件。");
        });
    }

    [RelayCommand]
    private async Task ImportLrc(IList? selectedItems)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0) return;
        var files = WpfUI.OpenFiles("歌词文件|*.lrc;*.txt|所有文件|*.*", "选择 LRC 文件");
        if (files.Count == 0) return;
        await RunBusyAsync("正在导入歌词…", true, async () =>
        {
            int ok = 0;
            var byBase = files.ToDictionary(
                f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (!byBase.TryGetValue(Path.GetFileNameWithoutExtension(row.File.FilePath), out var lrcPath))
                    continue;
                try
                {
                    var text = await File.ReadAllTextAsync(lrcPath);
                    _history.TryAddHistory(row.File.FilePath, row.File);
                    if (await _tagService.WriteLyricsAsync(row.File.FilePath, text))
                    {
                        row.File.Lyrics = text;
                        row.File.HasLyrics = true;
                        row.Refresh();
                        ok++;
                    }
                }
                catch (Exception ex) { _logger.Warn($"导入歌词失败 {lrcPath}: {ex.Message}"); }
            }
            StatusText = $"歌词导入完成：{ok}/{rows.Count}";
        });
    }

    // ============================================================
    // 批量：提取封面 / 简繁转换
    // ============================================================

    [RelayCommand]
    private async Task ExtractCovers(IList? selectedItems)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0) return;
        var picker = WpfUI.PickFolders("选择封面导出目录", false, false);
        if (picker.SelectedPaths.Count == 0) return;
        var dir = picker.SelectedPaths[0];
        await RunBusyAsync("正在提取封面…", true, async () =>
        {
            foreach (var row in rows)
            {
                // 封面只在此时按需读取，导出完立即释放，避免整批封面常驻内存
                await EnsureDetailsLoadedAsync(row.File);
                try
                {
                    var pictures = row.File.AllPictures?
                        .Where(p => p.HasImage).ToList() ?? new List<CoverArt>();
                    if (pictures.Count == 0 && row.File.CoverArtData is { Length: > 0 })
                        pictures.Add(new CoverArt
                        {
                            ImageData = row.File.CoverArtData,
                            MimeType = row.File.CoverArtMimeType ?? "image/jpeg"
                        });
                    for (int i = 0; i < pictures.Count; i++)
                    {
                        var pic = pictures[i];
                        var ext = pic.MimeType?.Contains("png") == true ? "png" : "jpg";
                        var suffix = pictures.Count > 1 ? $" ({i + 1})" : "";
                        var target = Path.Combine(dir,
                            Path.GetFileNameWithoutExtension(row.File.FilePath) + suffix + "." + ext);
                        target = EnsureUnique(target);
                        await File.WriteAllBytesAsync(target, pic.ImageData!);
                    }
                }
                catch (Exception ex) { _logger.Warn($"提取封面失败 {row.File.FilePath}: {ex.Message}"); }
                if (row.File != _lastDetailFile) row.File.ReleaseDetails();
            }
        });
        WpfUI.Info("封面提取完成。");
    }

    private static string EnsureUnique(string path)
    {
        if (!File.Exists(path)) return path;
        for (int n = 1; ; n++)
        {
            var candidate = Path.Combine(Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path) + $"({n})" + Path.GetExtension(path));
            if (!File.Exists(candidate)) return candidate;
        }
    }

    [RelayCommand]
    private Task ConvertTagsToTraditional(IList? selectedItems)
        => ConvertTags(selectedItems, ChineseUtils.ChineseConverter.SimplifiedToTraditional);

    [RelayCommand]
    private Task ConvertTagsToSimplified(IList? selectedItems)
        => ConvertTags(selectedItems, ChineseUtils.ChineseConverter.TraditionalToSimplified);

    private async Task ConvertTags(IList? selectedItems, Func<string, string> convert)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0) return;
        await RunBusyAsync("正在转换简繁…", true, async () =>
        {
            int ok = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var f = row.File;
                var isPanelRow = rows.Count == 1;
                // 歌词转换与历史封面快照需要重资源：逐行按需加载，峰值仅一份
                if (f.HasLyrics || f.HasCoverArt) await EnsureDetailsLoadedAsync(f);
                var data = new TagData
                {
                    Title = convert(f.Title),
                    Artist = convert(f.Artist),
                    Album = convert(f.Album),
                    AlbumArtist = convert(f.AlbumArtist),
                    Genre = convert(f.Genre),
                    Composer = convert(f.Composer),
                    Lyricist = convert(f.Lyricist),
                    Comment = convert(f.Comment),
                };
                if (!string.IsNullOrEmpty(f.Lyrics)) data.Lyrics = convert(f.Lyrics!);
                _history.TryAddHistory(f.FilePath, f);
                try
                {
                    if (await _tagService.WriteTagsAsync(f.FilePath, data, _settings.SaveTagsKeepUpdateTime))
                    {
                        if (isPanelRow)
                        {
                            var full = await _tagService.ReadTagsAsync(f.FilePath);
                            ApplyTags(f, full);
                        }
                        else
                        {
                            var summary = await _tagService.ReadTagsSummaryAsync(f.FilePath);
                            ApplyTags(f, summary);
                        }
                        row.Refresh();
                        ok++;
                    }
                }
                catch (Exception ex) { _logger.Warn($"简繁转换失败 {f.FilePath}: {ex.Message}"); }
                if (!isPanelRow) f.ReleaseDetails();
            }
            if (rows.Count == 1)
            {
                _lastDetailFile = rows[0].File;
                TagEdit.LoadSingle(rows[0].File);
            }
            StatusText = $"简繁转换完成：{ok}/{rows.Count}";
        });
    }

    [RelayCommand]
    private Task ConvertFilenamesToTraditional(IList? selectedItems)
        => ConvertFilenames(selectedItems, ChineseUtils.ChineseConverter.SimplifiedToTraditional);

    [RelayCommand]
    private Task ConvertFilenamesToSimplified(IList? selectedItems)
        => ConvertFilenames(selectedItems, ChineseUtils.ChineseConverter.TraditionalToSimplified);

    private async Task ConvertFilenames(IList? selectedItems, Func<string, string> convert)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0) return;
        await RunBusyAsync("正在转换文件名…", true, async () =>
        {
            int ok = 0;
            foreach (var row in rows)
            {
                try
                {
                    var f = row.File;
                    var name = Path.GetFileNameWithoutExtension(f.FilePath);
                    var converted = convert(name);
                    if (converted == name) continue;
                    var newPath = Path.Combine(f.Directory, converted + f.Extension);
                    if (File.Exists(newPath)) continue;
                    File.Move(f.FilePath, newPath);
                    f.FilePath = newPath;
                    row.Refresh();
                    ok++;
                }
                catch (Exception ex) { _logger.Warn($"文件名转换失败：{ex.Message}"); }
            }
            SaveListViewState();
            StatusText = $"文件名转换完成：{ok}/{rows.Count}";
        });
    }

    // ============================================================
    // 批量：自动匹配 / 文件名关系
    // ============================================================

    [RelayCommand]
    private async Task AutoMatch(IList? selectedItems)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0) return;

        var optionWin = new AutoMatchTagsWindow(_settings) { Owner = Application.Current.MainWindow };
        if (optionWin.ShowDialog() != true) return;
        var options = optionWin.Options;

        bool overwriteReadOnly = false;
        if (rows.Any(r => (File.GetAttributes(r.File.FilePath) & FileAttributes.ReadOnly) != 0))
        {
            var answer = WpfUI.YesNoCancel("选中文件中包含只读文件，是否去除只读属性并写入？\n" +
                "“是”=覆盖只读文件，“否”=跳过只读文件，“取消”=中止。");
            if (answer == MessageBoxResult.Cancel) return;
            overwriteReadOnly = answer == MessageBoxResult.Yes;
        }

        var cts = new CancellationTokenSource();
        var progressWin = new AutoMatchProgressWindow(rows.Count, cts)
        {
            Owner = Application.Current.MainWindow
        };
        progressWin.Show();
        AutoMatchBatchResult result;
        try
        {
            result = await _autoMatch.ExecuteAsync(rows.Select(r => r.File), options,
                new Progress<int>(n => progressWin.SetProgress(n)),
                new Progress<string>(name => progressWin.SetFile(name)),
                overwriteReadOnly, cts.Token);
        }
        catch (OperationCanceledException)
        {
            progressWin.Close();
            WpfUI.Warning("自动匹配已取消。");
            return;
        }
        finally { progressWin.Close(); }

        // 批量写入后只做轻量刷新（元数据 + 封面计数），避免整批文件封面常驻内存；
        // 单选时再全量补读供标签面板显示
        if (rows.Count == 1)
        {
            var only = rows[0].File;
            try
            {
                var full = await _tagService.ReadTagsAsync(only.FilePath);
                ApplyTags(only, full);
                rows[0].Refresh();
            }
            catch { /* 忽略单个文件重读失败 */ }
            _lastDetailFile = only;
            TagEdit.LoadSingle(only);
        }
        else
        {
            foreach (var row in rows)
            {
                try
                {
                    var tags = await _tagService.ReadTagsSummaryAsync(row.File.FilePath);
                    ApplyTags(row.File, tags);
                    row.Refresh();
                }
                catch { /* 忽略单个文件重读失败 */ }
            }
        }
        ApplyView();
        WpfUI.Info($"自动匹配完成：命中 {result.MatchedCount}，写入 {result.WrittenCount}，" +
            $"跳过 {result.SkippedCount}，错误 {result.ErrorCount}。");
    }

    [RelayCommand]
    private async Task FilenameRelation(IList? selectedItems)
    {
        var rows = GetSelected(selectedItems);
        if (rows.Count == 0) return;
        var win = new FilenameRelationWindow(_settings, null)
        {
            Owner = Application.Current.MainWindow
        };
        if (win.ShowDialog() != true || win.Options == null) return;

        bool overwriteReadOnly = false;
        if (win.Options.Mode == FilenameRelationMode.ChangeTags &&
            rows.Any(r => (File.GetAttributes(r.File.FilePath) & FileAttributes.ReadOnly) != 0))
        {
            var answer = WpfUI.YesNoCancel("选中文件中包含只读文件，是否去除只读属性并写入？\n" +
                "“是”=覆盖，“否”=跳过，“取消”=中止。");
            if (answer == MessageBoxResult.Cancel) return;
            overwriteReadOnly = answer == MessageBoxResult.Yes;
        }

        FilenameRelationResult result = new();
        await RunBusyAsync("正在处理文件名关系…", true, async () =>
        {
            result = await _filenameRelation.ExecuteAsync(rows.Select(r => r.File).ToList(),
                win.Options!, overwriteReadOnly);
        });

        await RereadRowsAsync(rows);
        SaveListViewState();
        ApplyView();
        if (result.ErrorCount > 0 || result.SkippedCount > 0)
            WpfUI.Warning($"处理完成：变更 {result.ChangedCount}，跳过 {result.SkippedCount}，" +
                $"错误 {result.ErrorCount}。\n" + string.Join("\n", result.Errors.Take(10)));
        else
            StatusText = $"文件名关系处理完成：变更 {result.ChangedCount} 项";
    }

    private async Task RereadRowsAsync(List<FileRowVm> rows)
    {
        // 轻量刷新：只更新元数据与封面计数，不把整批文件的封面字节/歌词读入内存
        foreach (var row in rows)
        {
            try
            {
                var tags = await _tagService.ReadTagsSummaryAsync(row.File.FilePath);
                ApplyTags(row.File, tags);
                row.Refresh();
            }
            catch { /* 忽略 */ }
        }
    }

    // ============================================================
    // 工具 / 帮助
    // ============================================================

    [RelayCommand]
    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings, _imageCache, _history)
        {
            Owner = Application.Current.MainWindow
        };
        win.ShowDialog();
    }

    [RelayCommand]
    private void OpenAbout()
        => new AboutWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private void OpenCompare()
    {
        if (_compareWindow != null)
        {
            if (_compareWindow.WindowState == WindowState.Minimized)
                _compareWindow.WindowState = WindowState.Normal;
            _compareWindow.Activate();
            return;
        }
        _compareWindow = new CompareWindow(_compareScan, _compareDb)
        {
            Owner = Application.Current.MainWindow
        };
        _compareWindow.Closed += (_, _) => _compareWindow = null;
        _compareWindow.Show();
    }

    [RelayCommand]
    private void OpenDeleteHistory()
    {
        if (_deleteHistoryWindow != null)
        {
            _deleteHistoryWindow.Activate();
            return;
        }
        _deleteHistoryWindow = new DeleteHistoryWindow(_compareDb)
        {
            Owner = Application.Current.MainWindow
        };
        _deleteHistoryWindow.Closed += (_, _) => _deleteHistoryWindow = null;
        _deleteHistoryWindow.Show();
    }

    // ============================================================
    // 列设置
    // ============================================================

    [RelayCommand]
    private void ConfigureColumns()
    {
        var win = new ColumnSelectWindow(ColumnSettings)
        {
            Owner = Application.Current.MainWindow
        };
        if (win.ShowDialog() == true)
        {
            ColumnSettings = win.Result.ToList();
            SaveColumnSettings();
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>列设置变化（列选择对话框确认、列宽/顺序调整后）时通知视图重建。</summary>
    public event EventHandler? ColumnsChanged;

    private List<ColumnHeaderInfo> LoadColumnSettings()
    {
        var defaults = ColumnHeaderInfo.CreateDefaults();
        var json = _settings.ListviewColumnHeader;
        if (string.IsNullOrWhiteSpace(json)) return defaults;
        try
        {
            var saved = JsonSerializer.Deserialize<List<ColumnHeaderInfo>>(json);
            if (saved == null) return defaults;
            // 与全部可用列对齐：新增列补默认，废弃列移除
            var byName = saved.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var merged = defaults.Select(d => byName.TryGetValue(d.Name, out var s) ? s : d).ToList();
            return merged.OrderBy(c => c.DisplayIndex).ToList();
        }
        catch { return defaults; }
    }

    private void SaveColumnSettings()
        => _settings.ListviewColumnHeader = JsonSerializer.Serialize(ColumnSettings);

    // ============================================================
    // 面板事件接线（封面/编码/歌词弹窗）
    // ============================================================

    private void WireTagEditEvents()
    {
        TagEdit.EncodingFixRequested += OnEncodingFix;
        TagEdit.LyricsEncodingFixRequested += OnLyricsEncodingFix;
        TagEdit.LyricsEditRequested += () => EditLyricsCommand.Execute(null);
        TagEdit.OnlineCoverSearchRequested += () => SearchPictureCommand.Execute(null);
        TagEdit.AddCoverFromDiskRequested += OnAddCoverFromDisk;
        TagEdit.ExtractCoverRequested += OnExtractCurrentCover;
        TagEdit.OpenCoverExternalRequested += OnOpenCoverExternal;
        TagEdit.CompressCoverRequested += OnCompressCover;
    }

    private void OnEncodingFix(TagFieldVm field)
    {
        var win = new EncodingFixWindow(field.Label, field.Text, null)
        {
            Owner = Application.Current.MainWindow
        };
        if (win.ShowDialog() == true && win.Applied)
            field.Text = win.FixedText;
    }

    private void OnLyricsEncodingFix()
    {
        var win = new EncodingFixWindow("歌词", TagEdit.LyricsText, null)
        {
            Owner = Application.Current.MainWindow
        };
        if (win.ShowDialog() == true && win.Applied)
            TagEdit.SetLyrics(win.FixedText);
    }

    private void OnAddCoverFromDisk()
    {
        var path = WpfUI.OpenFile("图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|所有文件|*.*",
            "选择封面图片");
        if (path == null) return;
        var cover = _coverService.LoadImageFromFile(path);
        if (cover == null)
        {
            WpfUI.Warning("无法读取该图片。");
            return;
        }
        TagEdit.AddPictures(new[] { cover }, TagEdit.OverwriteExisting);
    }

    private void OnExtractCurrentCover()
    {
        var pic = TagEdit.CurrentPicture;
        if (pic?.ImageData == null) return;
        var ext = pic.MimeType?.Contains("png") == true ? "png" : "jpg";
        var path = WpfUI.SaveFile($"{ext.ToUpper()} 图片|*.{ext}|所有文件|*.*",
            $"cover.{ext}", "提取封面");
        if (path != null) File.WriteAllBytes(path, pic.ImageData);
    }

    private void OnOpenCoverExternal()
    {
        var pic = TagEdit.CurrentPicture;
        if (pic?.ImageData == null) return;
        try
        {
            var ext = pic.MimeType?.ToLowerInvariant() switch
            {
                "image/png" => ".png", "image/bmp" => ".bmp", "image/gif" => ".gif",
                _ => ".jpg"
            };
            var tmp = Path.Combine(Path.GetTempPath(), "MusicTag_cover_" + Guid.NewGuid().ToString("N") + ext);
            File.WriteAllBytes(tmp, pic.ImageData);
            Process.Start(new ProcessStartInfo(tmp) { UseShellExecute = true });
        }
        catch (Exception ex) { WpfUI.Warning("打开失败：" + ex.Message); }
    }

    private void OnCompressCover()
    {
        var pic = TagEdit.CurrentPicture;
        if (pic == null) return;
        var input = InputWindow.Ask("更改分辨率", "请输入封面最长边像素（等比缩放，JPEG 质量 85）：",
            Math.Max(pic.Width, pic.Height) > 0 ? Math.Max(pic.Width, pic.Height).ToString() : "500");
        if (input == null) return;
        if (!int.TryParse(input, out var max) || max < 16 || max > 4096)
        {
            WpfUI.Warning("请输入 16 到 4096 之间的整数。");
            return;
        }
        var compressed = _coverService.CompressCover(pic, max, max, 85);
        if (compressed != null) TagEdit.ReplaceCurrentPicture(compressed);
        else WpfUI.Warning("压缩失败。");
    }

    // ============================================================
    // 持久化 / 辅助
    // ============================================================

    private ListViewState LoadListViewState()
    {
        var state = new ListViewState();
        var json = _settings.ListViewFileSetting;
        if (string.IsNullOrWhiteSpace(json)) return state;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonArray array)
            {
                // 旧版：纯文件路径数组
                state.Files = array.Select(t => t?.ToString() ?? "").ToList();
            }
            else
            {
                var obj = node.Deserialize<ListViewState>();
                if (obj != null) return obj;
            }
        }
        catch { /* 损坏配置忽略 */ }
        return state;
    }

    private void SaveListViewState()
    {
        try
        {
            _settings.ListViewFileSetting = JsonSerializer.Serialize(CurrentState());
            // 立即落盘：切换工作目录、重命名、删除等变化后即使程序异常退出也不丢失
            _settings.Save();
        }
        catch (Exception ex) { _logger.Warn("保存列表状态失败：" + ex.Message); }
    }

    /// <summary>TagData → MusicFile（与 FileScannerService.ApplyTags 同语义，VM 内重读用）。</summary>
    internal static void ApplyTags(MusicFile mf, TagData tags)
    {
        mf.Title = tags.Title ?? "";
        mf.Artist = tags.Artist ?? "";
        mf.Album = tags.Album ?? "";
        mf.Year = tags.Year;
        mf.Track = tags.Track;
        mf.TrackCount = tags.TrackCount ?? 0;
        mf.Genre = tags.Genre ?? "";
        mf.Comment = tags.Comment ?? "";
        mf.AlbumArtist = tags.AlbumArtist ?? "";
        mf.Composer = tags.Composer ?? "";
        mf.Lyricist = tags.Lyricist ?? "";
        mf.Disc = tags.Disc;
        mf.DiscCount = tags.DiscCount ?? 0;
        mf.HasLyrics = !string.IsNullOrEmpty(tags.Lyrics) || tags.LyricsPresent;
        mf.HasCoverArt = tags.CoverArtData != null || tags.AllPictures is { Count: > 0 }
                         || tags.PictureCount > 0;
        if (tags.IsSummary)
        {
            // 轻量结果：主动清掉可能残留的重资源（批量保存后用轻量重读刷新时会走到这里）
            mf.CoverArtData = null;
            mf.AllPictures = null;
            mf.Lyrics = null;
        }
        else
        {
            mf.CoverArtData = tags.CoverArtData;
            if (tags.AllPictures != null) mf.AllPictures = tags.AllPictures;
            mf.Lyrics = tags.Lyrics;
        }
        mf.CoverArtMimeType = tags.CoverArtMimeType;
        mf.CoverArtType = tags.CoverArtType;
        mf.PictureCount = tags.AllPictures?.Count ?? tags.PictureCount;
        // 轻量扫描结果：重资源不在本对象中；全量读取结果（含“无封面/无歌词”）标记为已加载
        mf.DetailsLoaded = !tags.IsSummary;
        if (tags.DurationMs is > 0) mf.Duration = TimeSpan.FromMilliseconds(tags.DurationMs.Value);
        if (tags.BitRate is > 0) mf.BitRate = tags.BitRate.Value;
        if (tags.SampleRate is > 0) mf.SampleRate = tags.SampleRate.Value;
        if (tags.Channels is > 0) mf.Channels = tags.Channels.Value;
        if (tags.BitsPerSample is > 0) mf.BitsPerSample = tags.BitsPerSample.Value;
        if (tags.TagFormat != null) mf.TagFormat = tags.TagFormat;
        mf.CurrentPictureIndex = 0;
    }

    /// <summary>
    /// 按需补加载单个文件的重资源（全部封面字节 + 歌词正文）。
    /// 轻量扫描后这些数据不在内存中，编辑/历史快照/歌词处理前调用本方法。
    /// </summary>
    private async Task EnsureDetailsLoadedAsync(MusicFile file)
    {
        if (file.DetailsLoaded) return;
        try
        {
            var tags = await _tagService.ReadTagsAsync(file.FilePath);
            ApplyTags(file, tags);
        }
        catch (Exception ex)
        {
            _logger.Warn($"补读标签详情失败：{file.FilePath} - {ex.Message}");
            // 失败时按已加载处理，避免对坏盘/无权限文件反复重试
            file.DetailsLoaded = true;
        }
    }

    private async Task RunBusyAsync(string? status, bool showProgress, Func<Task> action)
    {
        if (status != null) StatusText = status;
        IsBusy = true;
        IsIndeterminate = showProgress;
        if (!showProgress) Progress = 0;
        try { await action(); }
        catch (Exception ex)
        {
            _logger.Error(ex, "操作失败");
            WpfUI.Error("操作失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsIndeterminate = false;
            Progress = 0;
        }
    }

    /// <summary>视图关闭前保存过滤条件等易变设置并落盘。</summary>
    public void SaveSessionSettings()
    {
        _settings.FilterListViewKeyword = FilterKeyword;
        _settings.FilterListViewType = SelectedFilterType == "全部" ? null : SelectedFilterType;
        try { _settings.Save(); }
        catch (Exception ex) { _logger.Warn("保存会话设置失败：" + ex.Message); }
    }

    // ============================================================
    // 窗口位置 / 列布局（由 MainWindow 调用）
    // ============================================================

    public string? GetWindowBounds() => _settings.MainFormPosSizeInfo;
    public bool GetMaximized() => _settings.Maximized;

    public void SaveWindowBounds(int x, int y, int w, int h, bool maximized)
    {
        _settings.MainFormPosSizeInfo = $"{x},{y},{w},{h}";
        _settings.Maximized = maximized;
        // 关闭路径最后一次写入：列布局、窗口位置等缓存项在此一并落盘
        try { _settings.Save(); }
        catch (Exception ex) { _logger.Warn("保存窗口设置失败：" + ex.Message); }
    }

    /// <summary>用 DataGrid 当前可见列（列名/宽度/显示顺序）更新并持久化列设置。</summary>
    public void PersistColumns(IEnumerable<(string Name, int Width, int DisplayIndex)> visibleColumns)
    {
        var visible = visibleColumns.ToList();
        foreach (var col in ColumnSettings)
        {
            var found = visible.FirstOrDefault(v =>
                string.Equals(v.Name, col.Name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(found.Name))
            {
                col.IsShow = true;
                col.Width = found.Width;
                col.DisplayIndex = found.DisplayIndex;
            }
            else
            {
                col.IsShow = false;
            }
        }
        // 隐藏列排到可见列之后
        int order = visible.Count;
        foreach (var col in ColumnSettings.Where(c => !c.IsShow).OrderBy(c => c.DisplayIndex))
            col.DisplayIndex = order++;
        SaveColumnSettings();
    }
}
