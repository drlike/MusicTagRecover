using System.Text;
using System.Windows;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// 乱码修复对话框：支持单字段与批量两种构造方式。
/// 源编码与修复方向全局统一，切换时实时刷新预览；单字段取 <see cref="FixedText"/>，
/// 批量取 <see cref="FixedFields"/>（仅勾选应用的行）。
/// </summary>
public partial class EncodingFixWindow : Window
{
    private readonly EncodingFixViewModel _viewModel;

    /// <summary>单字段构造：fieldLabel 显示在标题中，skipField 保留（批量场景的来源字段）</summary>
    public EncodingFixWindow(string fieldLabel, string text, string? skipField)
    {
        InitializeComponent();
        _viewModel = new EncodingFixViewModel(fieldLabel, text, skipField);
        InitializeAfterInitializeComponent();
    }

    /// <summary>批量构造：fields 为 字段名 → 内容 的列表</summary>
    public EncodingFixWindow(IEnumerable<KeyValuePair<string, string>> fields)
    {
        InitializeComponent();
        _viewModel = new EncodingFixViewModel(fields);
        InitializeAfterInitializeComponent();
    }

    private void InitializeAfterInitializeComponent()
    {
        DataContext = _viewModel;
        Title = _viewModel.Title;
    }

    /// <summary>单字段修复结果（未点应用时默认为原文）</summary>
    public string FixedText => _viewModel.FixedText;

    /// <summary>批量修复结果（字段名 → 修复后文本）</summary>
    public Dictionary<string, string> FixedFields => _viewModel.FixedFields;

    /// <summary>当前选中的源编码</summary>
    public Encoding? SelectedEncoding => _viewModel.SelectedEncoding;

    /// <summary>是否点击了“应用”</summary>
    public bool Applied => _viewModel.Applied;

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Commit();
        DialogResult = true;
    }
}
