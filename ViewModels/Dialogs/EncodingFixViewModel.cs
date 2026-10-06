using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicTagRecover.ViewModels.Dialogs;

/// <summary>
/// 乱码修复对话框 ViewModel（单字段 / 批量两种模式共用）。
/// 典型乱码是 UTF-8 字节被按 GBK 等代码页解码，修复方向：
///   正向：srcEnc.GetBytes(乱码) → UTF-8.GetString
///   反向：UTF-8.GetBytes(乱码) → srcEnc.GetString
/// 编码与方向全局统一；批量模式下每行可单独勾选是否应用。
/// </summary>
public sealed partial class EncodingFixViewModel : ObservableObject
{
    static EncodingFixViewModel()
    {
        // .NET (Core) 默认只自带 UTF-8，需注册代码页提供程序才能使用 936/950 等编码
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>是否为批量模式</summary>
    public bool IsBatch { get; }

    /// <summary>窗口标题</summary>
    public string Title { get; }

    /// <summary>单字段模式的原始文本（只读展示）</summary>
    public string OriginalText { get; }

    /// <summary>批量模式的字段行</summary>
    public ObservableCollection<EncodingFieldRowViewModel> Rows { get; } = new();

    /// <summary>可选源编码（默认选中 GB2312/GBK，最常见的乱码来源）</summary>
    public IReadOnlyList<EncodingOption> Encodings { get; } = new[]
    {
        new EncodingOption("GB2312 (GBK)", 936),
        new EncodingOption("Big5", 950),
        new EncodingOption("Shift-JIS", 932),
        new EncodingOption("EUC-KR", 949),
        new EncodingOption("ISO-8859-1", 28591),
        new EncodingOption("UTF-8", Encoding.UTF8.CodePage),
    };

    /// <summary>当前选中的源编码项</summary>
    [ObservableProperty] private EncodingOption? _selectedEncodingOption;

    /// <summary>是否交换修复方向（反向修复）</summary>
    [ObservableProperty] private bool _reverseDirection;

    /// <summary>单字段模式的预览文本</summary>
    [ObservableProperty] private string _previewText = string.Empty;

    /// <summary>单字段修复结果（默认等于原文）</summary>
    public string FixedText { get; private set; }

    /// <summary>批量修复结果（勾选应用的字段）</summary>
    public Dictionary<string, string> FixedFields { get; } = new();

    /// <summary>当前选中的源编码</summary>
    public Encoding? SelectedEncoding => SelectedEncodingOption?.Encoding;

    /// <summary>是否点击了“应用”</summary>
    public bool Applied { get; private set; }

    /// <summary>
    /// 单字段构造。
    /// </summary>
    /// <param name="fieldLabel">字段名（用于标题）</param>
    /// <param name="text">原始乱码文本</param>
    /// <param name="skipField">保留参数：从批量场景进入时传入的来源字段名（单字段模式不使用）</param>
    public EncodingFixViewModel(string fieldLabel, string text, string? skipField)
    {
        IsBatch = false;
        Title = $"修复乱码 - {fieldLabel}";
        OriginalText = text ?? string.Empty;
        FixedText = OriginalText;

        SelectedEncodingOption = Encodings[0];
        RefreshAll();
    }

    /// <summary>
    /// 批量构造：fields 为 字段名 → 内容 的多行列表。
    /// </summary>
    public EncodingFixViewModel(IEnumerable<KeyValuePair<string, string>> fields)
    {
        IsBatch = true;
        Title = "批量修复乱码";
        OriginalText = string.Empty;
        // 批量模式不使用单值结果字段，初始化为空以满足非空约束
        FixedText = string.Empty;

        foreach (var pair in fields)
            Rows.Add(new EncodingFieldRowViewModel(pair.Key, pair.Value ?? string.Empty));

        SelectedEncodingOption = Encodings[0];
        RefreshAll();
    }

    partial void OnSelectedEncodingOptionChanged(EncodingOption? value) => RefreshAll();
    partial void OnReverseDirectionChanged(bool value) => RefreshAll();

    /// <summary>按当前编码与方向刷新全部预览</summary>
    private void RefreshAll()
    {
        var encoding = SelectedEncodingOption?.Encoding;
        if (encoding == null) return;

        PreviewText = Convert(OriginalText, encoding, ReverseDirection);
        foreach (var row in Rows)
            row.Preview = Convert(row.Original, encoding, ReverseDirection);
    }

    /// <summary>
    /// 编码转换。
    /// 正向：源编码字节 → UTF-8 字符串；反向：UTF-8 字节 → 源编码字符串。
    /// </summary>
    private static string Convert(string text, Encoding source, bool reverse)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        return reverse
            ? source.GetString(Encoding.UTF8.GetBytes(text))
            : Encoding.UTF8.GetString(source.GetBytes(text));
    }

    /// <summary>点击“应用”：固化结果</summary>
    public void Commit()
    {
        Applied = true;

        if (!IsBatch)
        {
            FixedText = PreviewText;
            return;
        }

        FixedFields.Clear();
        foreach (var row in Rows)
        {
            if (row.Apply)
                FixedFields[row.Key] = row.Preview;
        }
    }
}

/// <summary>源编码下拉项</summary>
public sealed class EncodingOption
{
    public string DisplayName { get; }
    public Encoding Encoding { get; }

    public EncodingOption(string displayName, int codePage)
    {
        DisplayName = displayName;
        Encoding = Encoding.GetEncoding(codePage);
    }
}

/// <summary>批量模式的字段行</summary>
public sealed partial class EncodingFieldRowViewModel : ObservableObject
{
    /// <summary>字段名</summary>
    public string Key { get; }

    /// <summary>原始内容</summary>
    public string Original { get; }

    /// <summary>修复后的预览内容</summary>
    [ObservableProperty] private string _preview = string.Empty;

    /// <summary>是否对该字段应用修复（默认勾选）</summary>
    [ObservableProperty] private bool _apply = true;

    public EncodingFieldRowViewModel(string key, string original)
    {
        Key = key;
        Original = original;
    }
}
