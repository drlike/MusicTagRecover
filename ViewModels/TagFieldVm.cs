using CommunityToolkit.Mvvm.ComponentModel;
using MusicTagRecover.Models;

namespace MusicTagRecover.ViewModels;

/// <summary>
/// 标签面板中的单个可编辑文本字段（标题/艺术家/年份…）。
/// </summary>
public partial class TagFieldVm : ObservableObject
{
    public string Key { get; }
    public string Label { get; }

    /// <summary>字段是否允许多行（注释/歌词在 UI 上高度更大）</summary>
    public bool Multiline { get; }

    private bool _loading;

    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private bool _isModified;

    public TagFieldVm(string key, string label, bool multiline = false)
    {
        Key = key;
        Label = label;
        Multiline = multiline;
    }

    partial void OnTextChanged(string value)
    {
        if (!_loading) IsModified = true;
    }

    /// <summary>程序化赋值（载入文件内容），不标记为已修改。</summary>
    public void Load(string? value)
    {
        _loading = true;
        Text = value ?? string.Empty;
        IsModified = false;
        _loading = false;
    }

    /// <summary>批量模式占位：&lt;keep&gt; 不改 / &lt;blank&gt; 清空。</summary>
    public void LoadKeepToken()
    {
        _loading = true;
        Text = TagEditViewModel.KeepToken;
        IsModified = false;
        _loading = false;
    }
}

/// <summary>封面信息展示项</summary>
public record CoverInfoVm(string Format, string Resolution, string Size, string Type);
