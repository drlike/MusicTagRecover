using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MusicTagRecover.Win32.FileDialog;

namespace MusicTagRecover.Win32;

/// <summary>
/// 文件夹选择对话框 — 封装 IFileOpenDialog（Vista 风格文件夹选择器），
/// 支持多选、内嵌「包含子文件夹」复选框、记住上次路径。WPF 版：owner 为 Window，取其 HWND。
/// </summary>
public sealed class FolderPicker
{
    private const int CheckButtonId = 1;

    /// <summary>上次选择的文件夹路径（静态，跨实例保持）</summary>
    private static string? _lastSelectedFolder;

    /// <summary>对话框标题</summary>
    public string Title { get; set; } = "";

    /// <summary>是否允许选择多个文件夹</summary>
    public bool AllowMultiSelect { get; set; } = true;

    /// <summary>「包含子文件夹」复选框的默认状态</summary>
    public bool IncludeSubDirectories { get; set; } = true;

    /// <summary>「包含子文件夹」复选框的标签文字</summary>
    public string CheckBoxLabel { get; set; } = "包含子文件夹";

    /// <summary>用户选择的文件夹路径列表</summary>
    public List<string> SelectedPaths { get; } = new();

    /// <summary>用户是否勾选了「包含子文件夹」</summary>
    public bool IncludeSubDirectoriesChecked { get; private set; } = true;

    /// <summary>
    /// 显示文件夹选择对话框（IFileOpenDialog，Vista 及以上系统均可用）。
    /// </summary>
    /// <param name="owner">所有者 WPF 窗口</param>
    /// <returns>用户是否点了确定</returns>
    public bool ShowDialog(Window? owner)
    {
        SelectedPaths.Clear();
        return ShowVistaDialog(owner);
    }

    private bool ShowVistaDialog(Window? owner)
    {
        try
        {
            // 创建 IFileOpenDialog 实例
            var dialog = (IFileOpenDialog)new FileOpenDialogRCW();

            // 设置选项: 文件夹选择模式 + 文件系统必须存在
            FOS fos = FOS.FOS_PICKFOLDERS
                     | FOS.FOS_FORCEFILESYSTEM
                     | FOS.FOS_FILEMUSTEXIST
                     | FOS.FOS_DONTADDTORECENT;
            if (AllowMultiSelect)
                fos |= FOS.FOS_ALLOWMULTISELECT;

            dialog.SetOptions(fos);

            // 设置标题
            if (!string.IsNullOrEmpty(Title))
                dialog.SetTitle(Title);

            // 添加「包含子文件夹」复选框
            try
            {
                var customize = (IFileDialogCustomize)dialog;
                customize.AddCheckButton(CheckButtonId, CheckBoxLabel, IncludeSubDirectories);
            }
            catch
            {
                // 某些系统可能不支持 IFileDialogCustomize，忽略
            }

            // 设置默认文件夹
            IShellItem? defaultFolder = CreateShellItemFromPath(
                _lastSelectedFolder ?? GetDefaultMusicFolder());
            if (defaultFolder != null)
            {
                try { dialog.SetDefaultFolder(defaultFolder); }
                catch { /* 忽略无效路径 */ }
            }

            // 显示对话框（owner 为 null 或句柄尚未创建时传 Zero）
            IntPtr hwnd = IntPtr.Zero;
            if (owner != null)
            {
                var helper = new WindowInteropHelper(owner);
                hwnd = helper.Handle;
            }
            int hr = dialog.Show(hwnd);
            if (hr != 0) // S_OK
                return false;

            // 获取选中的文件夹路径
            dialog.GetResults(out IShellItemArray results);
            uint count = results.GetCount();
            for (uint i = 0; i < count; i++)
            {
                IShellItem item = results.GetItemAt(i);
                string? path = GetShellItemPath(item);
                if (path != null)
                    SelectedPaths.Add(path);
            }

            // 保存第一个路径作为上次路径
            if (SelectedPaths.Count > 0)
            {
                string? dir = Path.GetDirectoryName(SelectedPaths[0]);
                if (dir != null)
                    _lastSelectedFolder = dir;
            }

            // 读取复选框状态
            IncludeSubDirectoriesChecked = IncludeSubDirectories; // 默认值
            if (SelectedPaths.Count > 0)
            {
                try
                {
                    var customize = (IFileDialogCustomize)dialog;
                    customize.GetCheckButtonState(CheckButtonId, out bool isChecked);
                    IncludeSubDirectoriesChecked = isChecked;
                }
                catch { /* 忽略 */ }
            }

            return SelectedPaths.Count > 0;
        }
        catch (Exception ex) when (ex is COMException || ex is InvalidCastException)
        {
            // COM 不可用（理论上 Vista+ 不会发生）
            return false;
        }
    }

    private static string? GetShellItemPath(IShellItem item)
    {
        try
        {
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out string path);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static IShellItem? CreateShellItemFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return null;

        try
        {
            // 使用 SHCreateItemFromParsingName API 创建 IShellItem
            Guid shellItemGuid = typeof(IShellItem).GUID;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemGuid,
                out object shellItem);
            if (hr == 0)
                return (IShellItem)shellItem;
        }
        catch { }
        return null;
    }

    /// <summary>获取默认音乐文件夹</summary>
    private static string GetDefaultMusicFolder()
    {
        try
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        }
        catch
        {
            return string.Empty;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern int SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out object ppv);
}
