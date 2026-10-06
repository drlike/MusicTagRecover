using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Models;
using MusicTagRecover.ViewModels;
using MusicTagRecover.Win32;

namespace MusicTagRecover.Views;

/// <summary>
/// 主窗口：菜单/工具栏/22 列 DataGrid/拖放/内联重命名/快捷键/WM_COPYDATA 单实例接收。
/// 业务逻辑全部在 MainViewModel；本类只做视图层胶水。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private DataGridTemplateColumn? _filenameColumn;
    private bool _suppressSelectionChange;

    /// <summary>列名 → FileRowVm 属性名</summary>
    private static readonly Dictionary<string, string> ColumnPropertyMap = new()
    {
        ["filename"] = nameof(FileRowVm.Filename),
        ["filedir"] = nameof(FileRowVm.Filedir),
        ["tagtypes"] = nameof(FileRowVm.TagTypes),
        ["title"] = nameof(FileRowVm.Title),
        ["artist"] = nameof(FileRowVm.Artist),
        ["album"] = nameof(FileRowVm.Album),
        ["albumartist"] = nameof(FileRowVm.AlbumArtist),
        ["year"] = nameof(FileRowVm.Year),
        ["trackstr"] = nameof(FileRowVm.TrackStr),
        ["discstr"] = nameof(FileRowVm.DiscStr),
        ["genre"] = nameof(FileRowVm.Genre),
        ["composer"] = nameof(FileRowVm.Composer),
        ["lyricist"] = nameof(FileRowVm.Lyricist),
        ["comment"] = nameof(FileRowVm.Comment),
        ["haspicture"] = nameof(FileRowVm.HasPicture),
        ["lyrics"] = nameof(FileRowVm.Lyrics),
        ["channels"] = nameof(FileRowVm.Channels),
        ["samplerate"] = nameof(FileRowVm.SampleRate),
        ["bitrate"] = nameof(FileRowVm.BitRate),
        ["bitpersample"] = nameof(FileRowVm.BitPerSample),
        ["durationinms"] = nameof(FileRowVm.DurationInMs),
        ["updatetime"] = nameof(FileRowVm.UpdateTime),
    };

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
        FilesGrid.Sorting += OnGridSorting;
        FilesGrid.CellEditEnding += OnCellEditEnding;
        FilesGrid.PreparingCellForEdit += OnPreparingCellForEdit;
        vm.ColumnsChanged += (_, _) => RebuildColumns();
        vm.HiddenDirs.CollectionChanged += (_, _) => RebuildHiddenDirsMenu();
        vm.SelectionRestoreRequired += RestoreGridSelection;
        ((App)Application.Current).ExternalFilesReceived += OnExternalFilesReceived;
    }

    /// <summary>
    /// Rows 重建（过滤/排序/保存后刷新）后按文件路径恢复 DataGrid 选区，
    /// 恢复期间屏蔽 SelectionChanged 联动，完成后手动同步一次标签面板。
    /// </summary>
    private void RestoreGridSelection(IReadOnlyList<string> paths)
    {
        var byPath = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        _suppressSelectionChange = true;
        try
        {
            FilesGrid.SelectedItems.Clear();
            foreach (var item in FilesGrid.Items.Cast<FileRowVm>())
            {
                if (byPath.Contains(item.File.FilePath))
                    FilesGrid.SelectedItems.Add(item);
            }
        }
        finally
        {
            _suppressSelectionChange = false;
        }
        _ = _vm.OnSelectionChanged(FilesGrid.SelectedItems);
    }

    // ============================================================
    // 启动：列、窗口状态、外部文件
    // ============================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RebuildColumns();
        RebuildHiddenDirsMenu();
        RestoreWindowState();
    }

    private void OnExternalFilesReceived(IReadOnlyList<string> paths)
    {
        Dispatcher.Invoke(async () => await _vm.LoadPathsAsync(paths));
    }

    // ============================================================
    // DataGrid 动态列
    // ============================================================

    private void RebuildColumns()
    {
        _suppressSelectionChange = true;
        FilesGrid.Columns.Clear();
        _filenameColumn = null;

        var visible = _vm.ColumnSettings
            .Where(c => c.IsShow)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        foreach (var col in visible)
        {
            if (!ColumnPropertyMap.TryGetValue(col.Name, out var prop)) continue;

            DataGridColumn column;
            if (col.Name == "filename")
            {
                column = CreateFilenameColumn();
                _filenameColumn = (DataGridTemplateColumn)column;
            }
            else
            {
                var binding = new System.Windows.Data.Binding(prop) { Mode = BindingMode.OneWay };
                column = new DataGridTextColumn { Binding = binding };
                var align = col.TextAlign switch
                {
                    ColumnTextAlign.Center => TextAlignment.Center,
                    ColumnTextAlign.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, align));
                style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
                ((DataGridTextColumn)column).ElementStyle = style;
            }

            column.Header = ColumnHeaderInfo.DisplayNames.TryGetValue(col.Name, out var dn)
                ? dn : col.Name;
            column.Width = new DataGridLength(col.Width > 0 ? col.Width : 80);
            column.SortMemberPath = prop;
            ColumnNameProperty.SetName(column, col.Name);
            FilesGrid.Columns.Add(column);
        }
        _suppressSelectionChange = false;
    }

    private DataGridTemplateColumn CreateFilenameColumn()
    {
        // 显示模板
        var viewFactory = new FrameworkElementFactory(typeof(TextBlock));
        viewFactory.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(FileRowVm.Filename)));
        viewFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var viewTemplate = new DataTemplate { VisualTree = viewFactory };

        // 编辑模板：TextBox 初始文本在 PreparingCellForEdit 时填入
        var editFactory = new FrameworkElementFactory(typeof(TextBox));
        editFactory.SetValue(TextBox.BorderThicknessProperty, new Thickness(0));
        editFactory.SetValue(TextBox.PaddingProperty, new Thickness(0));
        editFactory.AddHandler(LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is TextBox tb)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }));
        var editTemplate = new DataTemplate { VisualTree = editFactory };

        return new DataGridTemplateColumn
        {
            CellTemplate = viewTemplate,
            CellEditingTemplate = editTemplate,
        };
    }

    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is not TextBox tb || e.Row.Item is not FileRowVm row) return;
        tb.Text = row.File.FileName; // 含扩展名，提交时无扩展名自动补
    }

    private async void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not FileRowVm row || e.EditingElement is not TextBox tb) return;

        // 先取消编辑态，再执行可能失败/重命名的异步提交
        _suppressSelectionChange = true;
        FilesGrid.CancelEdit();
        _suppressSelectionChange = false;
        e.Cancel = true;
        await _vm.CommitRenameAsync(row, tb.Text);
    }

    private void OnGridSorting(object? sender, DataGridSortingEventArgs e)
    {
        var name = ColumnNameProperty.GetName(e.Column);
        if (name == null) return;
        e.Handled = true;
        _vm.SortBy(name);
        var (_, asc) = _vm.CurrentSort;
        e.Column.SortDirection = asc ? ListSortDirection.Ascending : ListSortDirection.Descending;
    }

    /// <summary>附加属性：DataGridColumn → 列 key（filename/title/...）</summary>
    public static class ColumnNameProperty
    {
        public static readonly DependencyProperty NameProperty =
            DependencyProperty.RegisterAttached("Name", typeof(string), typeof(ColumnNameProperty),
                new PropertyMetadata(null));

        public static void SetName(DependencyObject o, string? v) => o.SetValue(NameProperty, v);
        public static string? GetName(DependencyObject o) => (string?)o.GetValue(NameProperty);
    }

    // ============================================================
    // 选中联动
    // ============================================================

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChange) return;
        await _vm.OnSelectionChanged(FilesGrid.SelectedItems);
    }

    // ============================================================
    // 快捷键
    // ============================================================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var inEditor = Keyboard.FocusedElement is TextBoxBase or ComboBox;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 全屏播放时的快捷键优先处理（Esc/空格/方向键）
        if (_isPlayerFullscreen)
        {
            switch (key)
            {
                case Key.Escape: SetPlayerFullscreen(false); e.Handled = true; return;
                case Key.Space when !inEditor: Player.TogglePlayPause(); e.Handled = true; return;
                case Key.Left when !inEditor: Player.SeekBy(TimeSpan.FromSeconds(-5)); e.Handled = true; return;
                case Key.Right when !inEditor: Player.SeekBy(TimeSpan.FromSeconds(5)); e.Handled = true; return;
                case Key.Up when !inEditor: Player.AdjustVolume(5); e.Handled = true; return;
                case Key.Down when !inEditor: Player.AdjustVolume(-5); e.Handled = true; return;
            }
        }

        if (ctrl && key == Key.S) { _vm.SaveCommand.Execute(SelectedItems); e.Handled = true; }
        else if (ctrl && key == Key.D && !inEditor) { _vm.ChangeWorkingDirectoryCommand.Execute(null); e.Handled = true; }
        else if (ctrl && key == Key.P && !inEditor) { StartPreviewPlay(); e.Handled = true; }
        else if (ctrl && key == Key.R && !inEditor) { _vm.ClearTagsCommand.Execute(SelectedItems); e.Handled = true; }
        else if (ctrl && key == Key.T && !inEditor) { _vm.ReadTagsCommand.Execute(SelectedItems); e.Handled = true; }
        else if (ctrl && key == Key.A && !inEditor)
        {
            FilesGrid.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && key == Key.U && !inEditor)
        {
            FilesGrid.UnselectAll();
            e.Handled = true;
        }
        else if (ctrl && shift && key == Key.A && !inEditor)
        {
            InvertSelection();
            e.Handled = true;
        }
        else if (key == Key.F2 && !inEditor) { BeginRename(); e.Handled = true; }
        else if (key == Key.Delete && !inEditor)
        {
            if (shift) _vm.DeleteFilesCommand.Execute(SelectedItems);
            else _vm.RemoveRows(FilesGrid.SelectedItems);
            e.Handled = true;
        }
        else if (key == Key.F5 && !inEditor) { _vm.RefreshCommand.Execute(null); e.Handled = true; }
    }

    private IList SelectedItems => FilesGrid.SelectedItems;

    private void InvertSelection()
    {
        _suppressSelectionChange = true;
        var selected = FilesGrid.SelectedItems.Cast<object>().ToHashSet();
        FilesGrid.UnselectAll();
        foreach (var item in FilesGrid.Items)
            if (!selected.Contains(item)) FilesGrid.SelectedItems.Add(item);
        _suppressSelectionChange = false;
        _ = _vm.OnSelectionChanged(FilesGrid.SelectedItems);
    }

    private void BeginRename()
    {
        if (_filenameColumn == null || FilesGrid.SelectedItem == null) return;
        FilesGrid.CurrentColumn = _filenameColumn;
        FilesGrid.CurrentCell = new DataGridCellInfo(FilesGrid.SelectedItem, _filenameColumn);
        FilesGrid.BeginEdit();
    }

    // ============================================================
    // 菜单/工具栏事件（薄转发到 VM）
    // ============================================================

    private void OnChangeDirectory(object s, RoutedEventArgs e)
        => _vm.ChangeWorkingDirectoryCommand.Execute(null);
    private void OnAddDirectory(object s, RoutedEventArgs e)
        => _vm.AddDirectoryCommand.Execute(null);
    private void OnManageDirectories(object s, RoutedEventArgs e)
        => _vm.ManageDirectoriesCommand.Execute(null);
    private void OnPreviewPlay(object s, RoutedEventArgs e) => StartPreviewPlay();

    // ============================================================
    // 内嵌播放器（FFmpeg 9 + APE 官方 SDK 自研内核，全部在主窗口内完成，不弹独立窗口）
    // ============================================================

    private bool _isPlayerFullscreen;
    /// <summary>Player 在 DockPanel 中的原始父容器（退出全屏时还原用）。</summary>
    private DockPanel? _playerDockHost;

    private void StartPreviewPlay() => StartPreviewPlay(null);

    /// <summary>
    /// 开始预览播放。startRow 为双击列表指定的起始行时，播放当前可见列表（Rows 顺序）
    /// 并从该行开始；否则按选区决定（单选播放整个列表、多选播放选中项）。
    /// </summary>
    private void StartPreviewPlay(FileRowVm? startRow)
    {
        List<string> paths;
        int startIndex;
        if (startRow != null)
        {
            paths = _vm.Rows.Select(r => r.File.FilePath).Where(File.Exists).ToList();
            startIndex = paths.IndexOf(startRow.File.FilePath);
            if (startIndex < 0) startIndex = 0;
        }
        else if (FilesGrid.SelectedItems.Count <= 1)
        {
            paths = _vm.Rows.Select(r => r.File.FilePath).Where(File.Exists).ToList();
            var selected = FilesGrid.SelectedItem as FileRowVm;
            startIndex = selected == null ? 0 : paths.IndexOf(selected.File.FilePath);
            if (startIndex < 0) startIndex = 0;
        }
        else
        {
            paths = FilesGrid.SelectedItems.Cast<FileRowVm>()
                .Select(r => r.File.FilePath).Where(File.Exists).ToList();
            startIndex = 0;
        }

        if (paths.Count == 0)
        {
            WpfUI.Info("列表中没有可播放的文件。\n请先打开工作目录或在列表中选择音视频文件。");
            return;
        }

        Player.Play(paths, startIndex);
    }

    /// <summary>
    /// 双击非“文件名”列的单元格：直接播放该行文件（从当前可见列表该位置开始）。
    /// 文件名列保留原行为（双击进入内联重命名），表头/空白区双击不处理。
    /// </summary>
    private void OnListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var hit = VisualTreeHelper.HitTest(FilesGrid, e.GetPosition(FilesGrid));
        if (hit == null) return;

        var cell = FindVisualParent<DataGridCell>(hit.VisualHit);
        if (cell?.Column == null || cell.DataContext is not FileRowVm row) return;

        var colName = ColumnNameProperty.GetName(cell.Column);
        if (string.IsNullOrEmpty(colName) || colName == "filename") return;

        StartPreviewPlay(row);
    }

    private void OnPlayerFullscreen(object? sender, bool enter) => SetPlayerFullscreen(enter);

    private void SetPlayerFullscreen(bool enter)
    {
        if (enter == _isPlayerFullscreen) return;

        if (enter)
        {
            // 记录停靠宿主后，把控件移到覆盖全屏的 Grid（播放不中断，Image 呈现无 HWND 空域问题）
            _playerDockHost = (DockPanel)Player.Parent;
            _playerDockHost.Children.Remove(Player);
            FullScreenLayer.Children.Add(Player);
            FullScreenLayer.Visibility = Visibility.Visible;
            Player.HorizontalAlignment = HorizontalAlignment.Stretch;
            Player.VerticalAlignment = VerticalAlignment.Stretch;
            _isPlayerFullscreen = true;
            Player.ApplyFullscreenLayout(true);
            FullScreenLayer.Focus();
        }
        else
        {
            FullScreenLayer.Children.Remove(Player);
            FullScreenLayer.Visibility = Visibility.Collapsed;
            Player.HorizontalAlignment = HorizontalAlignment.Stretch;
            Player.VerticalAlignment = VerticalAlignment.Stretch;
            // 还原到 DockPanel 中原位置（DataGrid 之前，Bottom 停靠）
            _playerDockHost?.Children.Insert(Math.Max(0, _playerDockHost.Children.Count - 1), Player);
            _isPlayerFullscreen = false;
            Player.ApplyFullscreenLayout(false);
        }
    }

    private void OnPlayerClose(object? sender, EventArgs e)
    {
        if (_isPlayerFullscreen) SetPlayerFullscreen(false);
        Player.Visibility = Visibility.Collapsed;
    }
    private void OnRefresh(object s, RoutedEventArgs e) => _vm.RefreshCommand.Execute(null);
    private void OnExit(object s, RoutedEventArgs e) => Close();
    private void OnSave(object s, RoutedEventArgs e) => _vm.SaveCommand.Execute(SelectedItems);
    private void OnClearTags(object s, RoutedEventArgs e) => _vm.ClearTagsCommand.Execute(SelectedItems);
    private void OnReadTags(object s, RoutedEventArgs e) => _vm.ReadTagsCommand.Execute(SelectedItems);
    private void OnSelectAll(object s, RoutedEventArgs e) => FilesGrid.SelectAll();
    private void OnSelectNone(object s, RoutedEventArgs e) => FilesGrid.UnselectAll();
    private void OnInvertSelection(object s, RoutedEventArgs e) => InvertSelection();
    private void OnRename(object s, RoutedEventArgs e) => BeginRename();
    private void OnRemoveRows(object s, RoutedEventArgs e) => _vm.RemoveRows(FilesGrid.SelectedItems);
    private void OnDeleteFiles(object s, RoutedEventArgs e) => _vm.DeleteFilesCommand.Execute(SelectedItems);
    private void OnTagHistory(object s, RoutedEventArgs e) => _vm.OpenTagHistoryCommand.Execute(SelectedItems);
    private void OnSelectColumns(object s, RoutedEventArgs e) => _vm.ConfigureColumnsCommand.Execute(null);
    private void OnHideDirectories(object s, RoutedEventArgs e)
        => _vm.HideSelectedDirectories(FilesGrid.SelectedItems);

    // ============================================================
    // 文件列表右键菜单
    // ============================================================

    /// <summary>
    /// 右键按下时：若点中的行不在当前选区内，则把选区收缩为该行（单选场景）；
    /// 点中已选中行时保留多选结果，随后的菜单命令作用于整个选区。
    /// </summary>
    private void OnListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = VisualTreeHelper.HitTest(FilesGrid, e.GetPosition(FilesGrid));
        if (hit == null) return;
        var row = FindVisualParent<DataGridRow>(hit.VisualHit);
        if (row?.Item is not FileRowVm item) return;

        if (!FilesGrid.SelectedItems.Contains(item))
        {
            _suppressSelectionChange = true;
            FilesGrid.UnselectAll();
            FilesGrid.SelectedItem = item;
            _suppressSelectionChange = false;
            _ = _vm.OnSelectionChanged(FilesGrid.SelectedItems);
        }
    }

    private static T? FindVisualParent<T>(System.Windows.DependencyObject? start) where T : System.Windows.DependencyObject
    {
        var node = start;
        while (node != null && node is not T)
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        return node as T;
    }

    private void OnContextEncodingFix(object s, RoutedEventArgs e)
        => _vm.EncodingFixSelectedCommand.Execute(SelectedItems);

    private void OnContextDffNativeTags(object s, RoutedEventArgs e)
        => _vm.OpenDffNativeTagsCommand.Execute(SelectedItems);

    private void OnContextRename(object s, RoutedEventArgs e) => BeginRename();

    private void OnOpenFileLocation(object s, RoutedEventArgs e)
        => _vm.OpenFileLocationCommand.Execute(SelectedItems);

    private void OnPictureNetease(object s, RoutedEventArgs e) => _ = _vm.OpenPictureSearch(SelectedItems, "netease");
    private void OnPictureQq(object s, RoutedEventArgs e) => _ = _vm.OpenPictureSearch(SelectedItems, "qq");
    private void OnPictureItunes(object s, RoutedEventArgs e) => _ = _vm.OpenPictureSearch(SelectedItems, "itunes");
    private void OnPictureKuwo(object s, RoutedEventArgs e) => _ = _vm.OpenPictureSearch(SelectedItems, "kuwo");
    private void OnPictureLastfm(object s, RoutedEventArgs e) => _ = _vm.OpenPictureSearch(SelectedItems, "lastfm");
    private void OnPictureMusicbrainz(object s, RoutedEventArgs e) => _ = _vm.OpenPictureSearch(SelectedItems, "musicbrainz");
    private void OnPictureDiscogs(object s, RoutedEventArgs e) => _ = _vm.OpenPictureSearch(SelectedItems, "discogs");

    private void OnLyricsNetease(object s, RoutedEventArgs e) => _ = _vm.OpenLyricsSearch(SelectedItems, "netease");
    private void OnLyricsQq(object s, RoutedEventArgs e) => _ = _vm.OpenLyricsSearch(SelectedItems, "qq");
    private void OnLyricsKuwo(object s, RoutedEventArgs e) => _ = _vm.OpenLyricsSearch(SelectedItems, "kuwo");

    private void OnTagNetease(object s, RoutedEventArgs e) => _ = _vm.OpenTagSearch(SelectedItems, "netease");
    private void OnTagQq(object s, RoutedEventArgs e) => _ = _vm.OpenTagSearch(SelectedItems, "qq");
    private void OnTagItunes(object s, RoutedEventArgs e) => _ = _vm.OpenTagSearch(SelectedItems, "itunes");
    private void OnTagKuwo(object s, RoutedEventArgs e) => _ = _vm.OpenTagSearch(SelectedItems, "kuwo");
    private void OnTagLastfm(object s, RoutedEventArgs e) => _ = _vm.OpenTagSearch(SelectedItems, "lastfm");
    private void OnTagMusicbrainz(object s, RoutedEventArgs e) => _ = _vm.OpenTagSearch(SelectedItems, "musicbrainz");
    private void OnTagDiscogs(object s, RoutedEventArgs e) => _ = _vm.OpenTagSearch(SelectedItems, "discogs");

    private void OnLyricsReformat(object s, RoutedEventArgs e)
        => _vm.LyricsReformatTimelineCommand.Execute(SelectedItems);
    private void OnLyricsRemoveTimeline(object s, RoutedEventArgs e)
        => _vm.LyricsRemoveTimelineCommand.Execute(SelectedItems);
    private void OnLyricsRemoveBlank(object s, RoutedEventArgs e)
        => _vm.LyricsRemoveBlankLinesCommand.Execute(SelectedItems);
    private void OnLyricsRemoveHead(object s, RoutedEventArgs e)
        => _vm.LyricsRemoveHeadTagsCommand.Execute(SelectedItems);
    private void OnSaveLrc(object s, RoutedEventArgs e) => _vm.SaveLrcCommand.Execute(SelectedItems);
    private void OnImportLrc(object s, RoutedEventArgs e) => _vm.ImportLrcCommand.Execute(SelectedItems);
    private void OnEditLyrics(object s, RoutedEventArgs e) => _vm.EditLyricsCommand.Execute(SelectedItems);
    private void OnExtractCovers(object s, RoutedEventArgs e) => _vm.ExtractCoversCommand.Execute(SelectedItems);
    private void OnTagsS2T(object s, RoutedEventArgs e)
        => _vm.ConvertTagsToTraditionalCommand.Execute(SelectedItems);
    private void OnTagsT2S(object s, RoutedEventArgs e)
        => _vm.ConvertTagsToSimplifiedCommand.Execute(SelectedItems);
    private void OnFileS2T(object s, RoutedEventArgs e)
        => _vm.ConvertFilenamesToTraditionalCommand.Execute(SelectedItems);
    private void OnFileT2S(object s, RoutedEventArgs e)
        => _vm.ConvertFilenamesToSimplifiedCommand.Execute(SelectedItems);
    private void OnAutoMatch(object s, RoutedEventArgs e) => _vm.AutoMatchCommand.Execute(SelectedItems);
    private void OnFilenameRelation(object s, RoutedEventArgs e)
        => _vm.FilenameRelationCommand.Execute(SelectedItems);

    private void OnOpenCompare(object s, RoutedEventArgs e) => _vm.OpenCompareCommand.Execute(null);
    private void OnOpenDeleteHistory(object s, RoutedEventArgs e)
        => _vm.OpenDeleteHistoryCommand.Execute(null);
    private void OnOpenSettings(object s, RoutedEventArgs e) => _vm.OpenSettingsCommand.Execute(null);
    private void OnAbout(object s, RoutedEventArgs e) => _vm.OpenAboutCommand.Execute(null);

    private void RebuildHiddenDirsMenu()
    {
        HiddenDirsMenu.Items.Clear();
        if (_vm.HiddenDirs.Count == 0)
        {
            HiddenDirsMenu.IsEnabled = false;
            HiddenDirsMenu.Header = "（无）";
            return;
        }
        HiddenDirsMenu.IsEnabled = true;
        HiddenDirsMenu.Header = "恢复显示目录";
        foreach (var dir in _vm.HiddenDirs)
        {
            var item = new MenuItem { Header = dir };
            item.Click += (_, _) => _vm.RestoreDirectoryCommand.Execute(dir);
            HiddenDirsMenu.Items.Add(item);
        }
    }

    // ============================================================
    // 拖放
    // ============================================================

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        await _vm.LoadPathsAsync(paths);
    }

    // ============================================================
    // 单实例 WM_COPYDATA
    // ============================================================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_COPYDATA) return IntPtr.Zero;
        try
        {
            var cds = Marshal.PtrToStructure<NativeMethods.COPYDATASTRUCT>(lParam);
            if (cds.dwData == (IntPtr)App.CopyDataId && !string.IsNullOrEmpty(cds.lpData))
            {
                var paths = cds.lpData.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (paths.Length > 0)
                {
                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;
                    Activate();
                    _ = _vm.LoadPathsAsync(paths);
                }
            }
        }
        catch { /* 忽略坏消息 */ }
        handled = true;
        return IntPtr.Zero;
    }

    // ============================================================
    // 窗口状态持久化
    // ============================================================

    private void RestoreWindowState()
    {
        var info = _vm.GetWindowBounds();
        if (info == null) return;
        var parts = info.Split(',');
        if (parts.Length >= 4 &&
            int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) &&
            int.TryParse(parts[2], out var w) && int.TryParse(parts[3], out var h))
        {
            // 至少部分落在虚拟屏内才恢复，避免窗口跑出屏幕
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;
            if (w > 200 && h > 150 && x + w > 0 && y + h > 0 &&
                x < virtualWidth && y < virtualHeight)
            {
                Left = x; Top = y; Width = w; Height = h;
            }
        }
        if (_vm.GetMaximized()) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 停止播放器，释放 FFmpeg 解码资源
        _ = Player.ShutdownAsync();
        // 保存列宽/顺序
        var visible = new List<(string, int, int)>();
        foreach (var col in FilesGrid.Columns)
        {
            var name = ColumnNameProperty.GetName(col);
            if (name == null) continue;
            visible.Add((name, (int)Math.Max(40, col.ActualWidth), col.DisplayIndex));
        }
        _vm.PersistColumns(visible);
        _vm.SaveSessionSettings();

        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        _vm.SaveWindowBounds((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height,
            WindowState == WindowState.Maximized);

        ((App)Application.Current).ExternalFilesReceived -= OnExternalFilesReceived;
    }
}
