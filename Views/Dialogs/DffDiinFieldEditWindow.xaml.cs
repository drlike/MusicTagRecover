using System.Windows;
using System.Windows.Controls;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// DIIN 原生字段编辑表单弹窗。
/// AskNew / AskEdit 返回 (字段ID, 内容)；取消返回 (null, null)。
/// </summary>
public partial class DffDiinFieldEditWindow : Window
{
    private static readonly string[] PresetIds =
    {
        "DITI", "DIAR", "DGEN", "DINA", "DARE", "DPRS", "DENG", "DORD", "DSRC", "DCOU", "DLAH",
    };

    private DffDiinFieldEditWindow(string filePath, string? fieldId, string? text, bool lockId)
    {
        InitializeComponent();
        Title = fieldId == null ? "新增 DIIN 原生字段" : $"编辑 DIIN 原生字段 - {fieldId}";
        foreach (var id in PresetIds)
            IdBox.Items.Add(id);
        IdBox.Text = fieldId ?? "DITI";
        TextBox.Text = text ?? "";
        IdBox.IsReadOnly = lockId; // 编辑既有字段时锁定 ID，避免误改语义
        if (lockId)
            IdBox.ToolTip = "编辑既有字段时 ID 不可修改；如需更换 ID 请先删除后新增。";
        UpdateIdHint();
        Loaded += (_, _) => TextBox.Focus();
    }

    /// <summary>新增字段表单。</summary>
    public static (string? id, string text) AskNew(string ownerFile)
    {
        var win = new DffDiinFieldEditWindow(ownerFile, null, "", lockId: false)
        {
            Owner = Application.Current.MainWindow
        };
        return win.ShowDialog() == true ? (win.IdBox.Text.Trim(), win.TextBox.Text) : (null, "");
    }

    /// <summary>编辑既有字段表单（ID 锁定）。</summary>
    public static (string? id, string text) AskEdit(string ownerFile, string fieldId, string text)
    {
        var win = new DffDiinFieldEditWindow(ownerFile, fieldId, text, lockId: true)
        {
            Owner = Application.Current.MainWindow
        };
        return win.ShowDialog() == true ? (win.IdBox.Text.Trim(), win.TextBox.Text) : (null, "");
    }

    private void OnIdSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateIdHint();

    private void UpdateIdHint()
    {
        var id = IdBox.Text?.Trim() ?? "";
        if (id.Length == 4)
            IdHint.Text = DiinFieldRowViewModel.DescribeDiinId(id) + "（内容以 UTF-8 编码写入）";
        else
            IdHint.Text = $"字段 ID 必须恰好为 4 个可打印 ASCII 字符（当前 {id.Length} 个）。";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var id = IdBox.Text?.Trim() ?? "";
        if (id.Length != 4 || !id.All(c => c >= 0x20 && c <= 0x7E))
        {
            ShowError("字段 ID 必须恰好为 4 个可打印 ASCII 字符（如 DITI）。");
            return;
        }
        if (string.IsNullOrWhiteSpace(TextBox.Text))
        {
            if (MessageBox.Show(this, "字段内容为空，将写入空内容。是否继续？", "内容为空",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
        }
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
