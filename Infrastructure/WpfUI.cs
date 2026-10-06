using System.Windows;
using Microsoft.Win32;
using MusicTagRecover.Win32;

namespace MusicTagRecover.Infrastructure;

/// <summary>
/// 统一的 WPF 对话框入口（消息框、打开/保存文件、选择文件夹）。
/// </summary>
public static class WpfUI
{
    public static MessageBoxResult Info(string text, string title = "MusicTag")
        => MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static MessageBoxResult Warning(string text, string title = "MusicTag")
        => MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static MessageBoxResult Error(string text, string title = "MusicTag")
        => MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static bool Confirm(string text, string title = "MusicTag")
        => MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
           == MessageBoxResult.OK;

    public static MessageBoxResult YesNo(string text, string title = "MusicTag")
        => MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

    public static MessageBoxResult YesNoCancel(string text, string title = "MusicTag")
        => MessageBox.Show(text, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

    /// <summary>打开文件对话框，返回选中路径（取消为 null）。</summary>
    public static string? OpenFile(string filter, string? title = null, string? initialDir = null,
        bool multiSelect = false)
    {
        var dlg = new OpenFileDialog
        {
            Filter = filter,
            Title = title ?? "选择文件",
            Multiselect = multiSelect,
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(initialDir)) dlg.InitialDirectory = initialDir;
        return dlg.ShowDialog(CurrentWindow()) == true ? dlg.FileName : null;
    }

    /// <summary>多文件打开对话框。</summary>
    public static IReadOnlyList<string> OpenFiles(string filter, string? title = null,
        string? initialDir = null)
    {
        var dlg = new OpenFileDialog
        {
            Filter = filter,
            Title = title ?? "选择文件",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(initialDir)) dlg.InitialDirectory = initialDir;
        return dlg.ShowDialog(CurrentWindow()) == true ? dlg.FileNames : Array.Empty<string>();
    }

    /// <summary>保存文件对话框，返回目标路径（取消为 null）。</summary>
    public static string? SaveFile(string filter, string? fileName = null, string? title = null,
        string? initialDir = null)
    {
        var dlg = new SaveFileDialog
        {
            Filter = filter,
            Title = title ?? "保存为",
            FileName = fileName ?? "",
            OverwritePrompt = true,
        };
        if (!string.IsNullOrEmpty(initialDir)) dlg.InitialDirectory = initialDir;
        return dlg.ShowDialog(CurrentWindow()) == true ? dlg.FileName : null;
    }

    /// <summary>选择一个或多个文件夹（Vista 风格，带"包含子文件夹"复选框）。</summary>
    public static FolderPicker PickFolders(string title = "选择文件夹", bool allowMultiSelect = true,
        bool includeSubDirs = true)
    {
        var picker = new FolderPicker
        {
            Title = title,
            AllowMultiSelect = allowMultiSelect,
            IncludeSubDirectories = includeSubDirs,
        };
        picker.ShowDialog(CurrentWindow());
        return picker;
    }

    private static Window? CurrentWindow()
        => Application.Current.Windows.Count > 0
            ? Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
              ?? Application.Current.MainWindow
            : null;
}
