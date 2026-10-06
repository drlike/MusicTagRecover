using CommunityToolkit.Mvvm.ComponentModel;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.Services;

namespace MusicTagRecover.ViewModels.Dialogs;

/// <summary>
/// “文件名 ⇄ 标签”联动对话框的 ViewModel。
/// 第一页为“文件名→标签”(ChangeTags)，第二页为“标签→文件名”(RenameFiles)。
/// 设置持久化在 ISettingsService 的 FilenameCustomPattern / FilenameRelCondition /
/// FilenameRelRegexCondition / FilenameRelSelectedTab 字段中。
/// </summary>
public sealed partial class FilenameRelationViewModel : ObservableObject
{
    /// <summary>占位符提示文案</summary>
    public const string PlaceholderHint =
        "占位符：@0 文件名　@1 标题　@2 艺术家　@3 专辑　@4 碟号　@5 音轨号　@6 年份　@7 注释　@8 专辑艺术家";

    /// <summary>预览用示例文件名（不含扩展名）</summary>
    private const string SampleFileName = "歌手 - 示例歌曲";

    private readonly ISettingsService _settings;
    private readonly FilenameRelationMode? _forcedMode;

    // ===== “文件名→标签”页 =====

    /// <summary>普通解析模式</summary>
    [ObservableProperty] private string _changeTagsPattern = "@2 - @1";

    /// <summary>是否使用正则解析（仅文件名→标签页可用）</summary>
    [ObservableProperty] private bool _useRegex;

    /// <summary>正则表达式</summary>
    [ObservableProperty] private string _regexPatternText = string.Empty;

    // ===== “标签→文件名”页 =====

    /// <summary>重命名模式</summary>
    [ObservableProperty] private string _renameFilesPattern = "@2 - @1";

    /// <summary>是否同时重命名关联文件（.lrc / 封面）</summary>
    [ObservableProperty] private bool _renameRelatedFiles;

    // ===== 正则捕获组映射（组 1..4 → 字段 0..8，0 表示不映射） =====

    [ObservableProperty] private int _group1Target;
    [ObservableProperty] private int _group2Target;
    [ObservableProperty] private int _group3Target;
    [ObservableProperty] private int _group4Target;

    // ===== 页签与预览 =====

    /// <summary>当前选中页：0 = 文件名→标签，1 = 标签→文件名</summary>
    [ObservableProperty] private int _selectedTabIndex;

    /// <summary>实时预览文本</summary>
    [ObservableProperty] private string _previewText = string.Empty;

    /// <summary>捕获组映射下拉选项</summary>
    public IReadOnlyList<GroupTargetOption> GroupTargets { get; } = new[]
    {
        new GroupTargetOption(0, "不映射"),
        new GroupTargetOption(1, "@1 标题"),
        new GroupTargetOption(2, "@2 艺术家"),
        new GroupTargetOption(3, "@3 专辑"),
        new GroupTargetOption(4, "@4 碟号"),
        new GroupTargetOption(5, "@5 音轨号"),
        new GroupTargetOption(6, "@6 年份"),
        new GroupTargetOption(7, "@7 注释"),
        new GroupTargetOption(8, "@8 专辑艺术家"),
    };

    /// <summary>forcedMode 非空时锁定页面并隐藏 Tab 头</summary>
    public bool IsLocked => _forcedMode.HasValue;

    /// <summary>“使用正则”复选框是否可用（仅文件名→标签页可用）</summary>
    public bool CanUseRegex =>
        _forcedMode == FilenameRelationMode.ChangeTags
        || (!_forcedMode.HasValue && SelectedTabIndex == 0);

    /// <summary>确定后填充的选项（取消时为 null）</summary>
    public FilenameRelationOptions? Options { get; private set; }

    public FilenameRelationViewModel(ISettingsService settings, FilenameRelationMode? forcedMode)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _forcedMode = forcedMode;

        LoadSettings();

        // 锁定模式下直接显示指定页；重命名页不允许使用正则
        SelectedTabIndex = forcedMode switch
        {
            FilenameRelationMode.ChangeTags => 0,
            FilenameRelationMode.RenameFiles => 1,
            _ => Math.Clamp(_settings.FilenameRelSelectedTab, 0, 1),
        };
        if (SelectedTabIndex == 1) UseRegex = false;

        UpdatePreview();
    }

    // ===== 设置读写 =====

    private void LoadSettings()
    {
        // 普通“文件名→标签”模式
        var customPattern = _settings.FilenameCustomPattern;
        if (!string.IsNullOrWhiteSpace(customPattern))
            ChangeTagsPattern = customPattern.Trim();

        // “标签→文件名”模式 + 关联文件开关（历史上可能直接存裸模式串，兼容处理）
        var condition = _settings.FilenameRelCondition;
        if (!string.IsNullOrWhiteSpace(condition))
        {
            var text = condition.Trim();
            if (text.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    var obj = JsonObject.Parse(text)!;
                    RenameFilesPattern = obj["pattern"]?.ToString() ?? RenameFilesPattern;
                    RenameRelatedFiles = obj["renameRelatedFiles"]?.GetValue<bool>() ?? false;
                }
                catch { /* 损坏配置保持默认值 */ }
            }
            else
            {
                RenameFilesPattern = text;
            }
        }

        // 正则模式 + 捕获组映射
        var regexCondition = _settings.FilenameRelRegexCondition;
        if (!string.IsNullOrWhiteSpace(regexCondition))
        {
            try
            {
                var obj = JsonObject.Parse(regexCondition.Trim())!;
                RegexPatternText = obj["pattern"]?.ToString() ?? string.Empty;
                UseRegex = obj["useRegex"]?.GetValue<bool>() ?? false;
                if (obj["groupMap"] is JsonObject map)
                {
                    foreach (var pair in map)
                    {
                        if (!int.TryParse(pair.Key, out var group) || pair.Value == null) continue;
                        var target = pair.Value.GetValue<int>();
                        SetGroupTarget(group, target);
                    }
                }
            }
            catch { /* 损坏配置保持默认值 */ }
        }
    }

    private void PersistSettings()
    {
        _settings.FilenameCustomPattern = ChangeTagsPattern?.Trim();
        _settings.FilenameRelCondition = new JsonObject
        {
            ["pattern"] = RenameFilesPattern,
            ["renameRelatedFiles"] = RenameRelatedFiles,
        }.ToJsonString();
        _settings.FilenameRelRegexCondition = new JsonObject
        {
            ["useRegex"] = UseRegex,
            ["pattern"] = RegexPatternText,
            ["groupMap"] = JsonSerializer.SerializeToNode(BuildGroupMap()),
        }.ToJsonString();
        _settings.FilenameRelSelectedTab = SelectedTabIndex;
        _settings.Save();
    }

    // ===== 确定 / 校验 =====

    /// <summary>
    /// 确定：校验当前页输入，通过则构建 Options 并持久化。
    /// </summary>
    /// <param name="error">校验失败时的错误提示</param>
    /// <returns>true 表示校验通过</returns>
    public bool TryConfirm(out string error)
    {
        var mode = _forcedMode ??
            (SelectedTabIndex == 0 ? FilenameRelationMode.ChangeTags : FilenameRelationMode.RenameFiles);

        var options = new FilenameRelationOptions { Mode = mode };

        if (mode == FilenameRelationMode.ChangeTags && UseRegex)
        {
            var map = BuildGroupMap();
            if (!FilenameRelationService.TryValidateRegex(RegexPatternText ?? string.Empty, map, out error))
                return false;
            options.UseRegex = true;
            options.RegexPattern = RegexPatternText!;
            options.RegexGroupMap = map;
            options.Pattern = ChangeTagsPattern ?? string.Empty;
        }
        else if (mode == FilenameRelationMode.ChangeTags)
        {
            if (!FilenameRelationService.TryValidatePattern(ChangeTagsPattern ?? string.Empty, true, out error))
                return false;
            options.Pattern = ChangeTagsPattern!;
        }
        else
        {
            if (!FilenameRelationService.TryValidatePattern(RenameFilesPattern ?? string.Empty, false, out error))
                return false;
            options.Pattern = RenameFilesPattern!;
            options.RenameRelatedFiles = RenameRelatedFiles;
        }

        Options = options;
        PersistSettings();
        error = string.Empty;
        return true;
    }

    // ===== 预览 =====

    partial void OnChangeTagsPatternChanged(string value) => UpdatePreview();
    partial void OnRenameFilesPatternChanged(string value) => UpdatePreview();
    partial void OnUseRegexChanged(bool value) => UpdatePreview();
    partial void OnRegexPatternTextChanged(string value) => UpdatePreview();
    partial void OnGroup1TargetChanged(int value) => UpdatePreview();
    partial void OnGroup2TargetChanged(int value) => UpdatePreview();
    partial void OnGroup3TargetChanged(int value) => UpdatePreview();
    partial void OnGroup4TargetChanged(int value) => UpdatePreview();

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CanUseRegex));
        // 切到“标签→文件名”页时关闭并禁用正则
        if (value == 1 && UseRegex) UseRegex = false;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (SelectedTabIndex == 1)
            UpdateRenamePreview();
        else
            UpdateChangeTagsPreview();
    }

    /// <summary>“标签→文件名”预览：用示例标签生成新文件名</summary>
    private void UpdateRenamePreview()
    {
        try
        {
            var sample = new MusicFile
            {
                FilePath = @"D:\Music\示例.flac",
                Title = "示例歌曲",
                Artist = "歌手",
                Album = "专辑",
                AlbumArtist = "专辑艺术家",
                Disc = 1,
                Track = 2,
                Year = 2024,
                Comment = "注释",
            };
            var name = FilenameRelationService.BuildFilename(sample, RenameFilesPattern ?? string.Empty);
            PreviewText = string.IsNullOrWhiteSpace(name)
                ? "标题或艺术家为空，无法生成文件名。"
                : $"生成文件名：{name}.flac";
        }
        catch (Exception ex)
        {
            PreviewText = "模式无效：" + ex.Message;
        }
    }

    /// <summary>“文件名→标签”预览：用示例文件名解析标签</summary>
    private void UpdateChangeTagsPreview()
    {
        try
        {
            var options = new FilenameRelationOptions
            {
                Mode = FilenameRelationMode.ChangeTags,
                UseRegex = UseRegex,
                Pattern = ChangeTagsPattern ?? string.Empty,
                RegexPattern = RegexPatternText ?? string.Empty,
                RegexGroupMap = BuildGroupMap(),
            };

            var samplePath = Path.Combine(@"D:\Music", SampleFileName + ".flac");
            if (!FilenameRelationService.TryParseFilename(samplePath, options, new TagData(), out var tags))
            {
                PreviewText = $"示例文件名“{SampleFileName}.flac”与当前模式不匹配，解析不出标签。";
                return;
            }

            var lines = new List<string> { $"示例文件名：{SampleFileName}.flac", "解析结果：" };
            void AddLine(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) lines.Add($"　{label}：{value}");
            }
            AddLine("标题", tags.Title);
            AddLine("艺术家", tags.Artist);
            AddLine("专辑", tags.Album);
            if (tags.Disc.HasValue) AddLine("碟号", tags.Disc.Value.ToString());
            if (tags.Track.HasValue) AddLine("音轨号", tags.Track.Value.ToString());
            if (tags.Year.HasValue) AddLine("年份", tags.Year.Value.ToString());
            AddLine("注释", tags.Comment);
            AddLine("专辑艺术家", tags.AlbumArtist);

            PreviewText = lines.Count == 2
                ? "没有从示例文件名中解析到任何标签字段。"
                : string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            PreviewText = "模式或正则无效：" + ex.Message;
        }
    }

    // ===== 捕获组映射 =====

    private Dictionary<int, int> BuildGroupMap()
    {
        var map = new Dictionary<int, int>();
        var targets = new[] { Group1Target, Group2Target, Group3Target, Group4Target };
        for (var i = 0; i < targets.Length; i++)
        {
            if (targets[i] > 0) map[i + 1] = targets[i];
        }
        return map;
    }

    private void SetGroupTarget(int group, int target)
    {
        switch (group)
        {
            case 1: Group1Target = target; break;
            case 2: Group2Target = target; break;
            case 3: Group3Target = target; break;
            case 4: Group4Target = target; break;
        }
    }
}

/// <summary>捕获组映射下拉项（目标字段编号 0..8，0 = 不映射）</summary>
public sealed class GroupTargetOption
{
    public int Value { get; }
    public string Text { get; }

    public GroupTargetOption(int value, string text)
    {
        Value = value;
        Text = text;
    }
}
