using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;

namespace MusicTagRecover.ViewModels.Dialogs;

/// <summary>
/// 自动匹配标签设置对话框的 ViewModel。
/// 行包装对象直接读写 <see cref="AutoMatchFieldOption"/>（Load 得到的副本，取消不落盘）。
/// </summary>
public sealed partial class AutoMatchTagsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    /// <summary>当前编辑的选项（确定时 Save 回设置服务）</summary>
    public AutoMatchOptions Options { get; }

    /// <summary>10 个字段行（顺序与 <see cref="AutoMatchOptions.Names"/> 一致）</summary>
    public ObservableCollection<AutoMatchFieldRowViewModel> Rows { get; } = new();

    /// <summary>写入模式下拉选项</summary>
    public IReadOnlyList<WriteModeOption> WriteModes { get; } = new[]
    {
        new WriteModeOption(AutoMatchWriteMode.SaveToTag, "保存到标签"),
        new WriteModeOption(AutoMatchWriteMode.SaveToFile, "保存到文件"),
        new WriteModeOption(AutoMatchWriteMode.SaveToTagAndFile, "标签和文件"),
    };

    /// <summary>并发线程数（文本框直接编辑，提交时校验 1..16）</summary>
    [ObservableProperty] private string _threadCountText = "4";

    /// <summary>不下载标题含“伴奏/纯音乐”等关键字的歌词</summary>
    [ObservableProperty] private bool _dontDownloadInstrumental = true;

    /// <summary>
    /// “启用”全选复选框三态：true=全部启用，false=全部停用，null=部分启用。
    /// 用户点击时统一按“全选/取消全选”切换；点击半选态按取消处理（WPF 默认行为）。
    /// </summary>
    [ObservableProperty] private bool? _areAllEnabled;

    /// <summary>
    /// “覆盖已有”全选复选框三态：true=全部勾选，false=全部未勾选，null=部分勾选。
    /// 仅控制各行 Overwrite，与“启用”互不影响。
    /// </summary>
    [ObservableProperty] private bool? _areAllOverwrite;

    /// <summary>抑制“启用”聚合态写回各行时的递归通知</summary>
    private bool _syncingEnabled;

    /// <summary>抑制“覆盖已有”聚合态写回各行时的递归通知</summary>
    private bool _syncingOverwrite;

    public AutoMatchTagsViewModel(ISettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Options = AutoMatchOptions.Load(settings);

        foreach (var name in AutoMatchOptions.Names)
        {
            var row = new AutoMatchFieldRowViewModel(name, Options.Get(name));
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }

        ThreadCountText = Options.ThreadCount.ToString();
        DontDownloadInstrumental = Options.DontDownloadLyricWithInstrumentInTitle;
        // 依据已加载选项初始化两个三态聚合（避免初始通知抖动：行已先订阅，这里统一回填）
        RefreshAllEnabled();
        RefreshAllOverwrite();
    }

    /// <summary>行内复选框变化时，同步对应列的全选三态。</summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AutoMatchFieldRowViewModel.Enabled))
            RefreshAllEnabled();
        else if (e.PropertyName == nameof(AutoMatchFieldRowViewModel.Overwrite))
            RefreshAllOverwrite();
    }

    partial void OnAreAllEnabledChanged(bool? value)
    {
        if (_syncingEnabled) return;
        SetAllEnabled(value == true);
    }

    partial void OnAreAllOverwriteChanged(bool? value)
    {
        if (_syncingOverwrite) return;
        SetAllOverwrite(value == true);
    }

    /// <summary>Ctrl+A / 切换按钮：在“全选”与“取消全选”之间翻转（作用于“启用”列）。</summary>
    [RelayCommand]
    private void ToggleAll() => SetAllEnabled(!AreAllEnabledEnabled);

    /// <summary>“覆盖已有”全选复选框：在“全选”与“取消全选”之间翻转。</summary>
    [RelayCommand]
    private void ToggleAllOverwrite() => SetAllOverwrite(!AreAllOverwriteChecked);

    /// <summary>“启用”列是否处于全部启用状态（无行时为 false，即空列表不视为全选）。</summary>
    private bool AreAllEnabledEnabled =>
        Rows.Count > 0 && Rows.All(r => r.Enabled);

    /// <summary>“覆盖已有”列是否处于全部勾选状态。</summary>
    private bool AreAllOverwriteChecked =>
        Rows.Count > 0 && Rows.All(r => r.Overwrite);

    private void SetAllEnabled(bool enabled)
    {
        _syncingEnabled = true;
        try
        {
            foreach (var row in Rows)
                row.Enabled = enabled;
            AreAllEnabled = enabled;
        }
        finally
        {
            _syncingEnabled = false;
        }
    }

    private void SetAllOverwrite(bool overwrite)
    {
        _syncingOverwrite = true;
        try
        {
            foreach (var row in Rows)
                row.Overwrite = overwrite;
            AreAllOverwrite = overwrite;
        }
        finally
        {
            _syncingOverwrite = false;
        }
    }

    private void RefreshAllEnabled()
    {
        _syncingEnabled = true;
        try
        {
            var enabledCount = 0;
            foreach (var row in Rows)
                if (row.Enabled) enabledCount++;
            // 无行或全不选=false；全选=true；其余=部分（null）
            AreAllEnabled = enabledCount == 0 ? false
                : enabledCount == Rows.Count ? true : (bool?)null;
        }
        finally
        {
            _syncingEnabled = false;
        }
    }

    private void RefreshAllOverwrite()
    {
        _syncingOverwrite = true;
        try
        {
            var checkedCount = 0;
            foreach (var row in Rows)
                if (row.Overwrite) checkedCount++;
            // 无行或全不选=false；全选=true；其余=部分（null）
            AreAllOverwrite = checkedCount == 0 ? false
                : checkedCount == Rows.Count ? true : (bool?)null;
        }
        finally
        {
            _syncingOverwrite = false;
        }
    }

    /// <summary>+ 按钮：线程数 +1（上限 MaxThreadCount）</summary>
    [RelayCommand]
    private void IncreaseThread() => AdjustThread(+1);

    /// <summary>- 按钮：线程数 -1（下限 1）</summary>
    [RelayCommand]
    private void DecreaseThread() => AdjustThread(-1);

    private void AdjustThread(int delta)
    {
        int.TryParse(ThreadCountText, out var current);
        var value = Math.Clamp(current + delta, 1, AutoMatchOptions.MaxThreadCount);
        ThreadCountText = value.ToString();
    }

    /// <summary>
    /// 确定时调用：校验线程数并持久化。
    /// </summary>
    /// <param name="error">校验失败时的错误提示</param>
    /// <returns>true 表示校验通过且已保存</returns>
    public bool TryCommit(out string error)
    {
        if (!int.TryParse(ThreadCountText?.Trim(), out var threads)
            || threads < 1 || threads > AutoMatchOptions.MaxThreadCount)
        {
            error = $"并发线程数必须是 1 到 {AutoMatchOptions.MaxThreadCount} 之间的整数。";
            return false;
        }

        Options.ThreadCount = threads;
        Options.DontDownloadLyricWithInstrumentInTitle = DontDownloadInstrumental;
        Options.Save(_settings);

        error = string.Empty;
        return true;
    }
}

/// <summary>写入模式下拉项（枚举值 + 中文显示）</summary>
public sealed class WriteModeOption
{
    public AutoMatchWriteMode Value { get; }
    public string Text { get; }

    public WriteModeOption(AutoMatchWriteMode value, string text)
    {
        Value = value;
        Text = text;
    }
}

/// <summary>字段设置行：启用 / 写入模式 / 覆盖已有，直接代理到底层选项对象</summary>
public sealed partial class AutoMatchFieldRowViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> FieldDisplayNames = new()
    {
        [AutoMatchOptions.Cover] = "封面",
        [AutoMatchOptions.Lyrics] = "歌词",
        [AutoMatchOptions.Title] = "标题",
        [AutoMatchOptions.Artist] = "艺术家",
        [AutoMatchOptions.Album] = "专辑",
        [AutoMatchOptions.Year] = "年份",
        [AutoMatchOptions.Track] = "音轨号",
        [AutoMatchOptions.Disc] = "碟号",
        [AutoMatchOptions.Genre] = "风格",
        [AutoMatchOptions.Comment] = "注释",
    };

    private readonly AutoMatchFieldOption _option;

    public string Name { get; }
    public string DisplayName { get; }

    public AutoMatchFieldRowViewModel(string name, AutoMatchFieldOption option)
    {
        Name = name;
        _option = option;
        DisplayName = FieldDisplayNames.TryGetValue(name, out var text) ? text : name;
    }

    /// <summary>是否启用该字段</summary>
    public bool Enabled
    {
        get => _option.Enabled;
        set
        {
            if (_option.Enabled == value) return;
            _option.Enabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>写入模式</summary>
    public AutoMatchWriteMode WriteMode
    {
        get => _option.WriteMode;
        set
        {
            if (_option.WriteMode == value) return;
            _option.WriteMode = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是否覆盖已有内容</summary>
    public bool Overwrite
    {
        get => _option.Overwrite;
        set
        {
            if (_option.Overwrite == value) return;
            _option.Overwrite = value;
            OnPropertyChanged();
        }
    }
}
