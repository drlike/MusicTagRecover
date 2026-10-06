using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MusicTagRecover.Services.Compare;

/// <summary>
/// 回收站安全删除服务（静态，无状态）。
/// 所有删除操作通过 SHFileOperation + FOF_ALLOWUNDO 进入系统回收站，
/// 绝不物理删除；恢复通过解析各盘 $Recycle.Bin\&lt;SID&gt;\$I 元数据文件，
/// 找到对应 $R 实体后 Move 回原路径。
/// 兼容 Windows 版本 1（4 字节名字长）与版本 2（8 字节名字长）两种 $I 布局。
/// </summary>
public static class RecycleBinService
{
    // ============ SHFileOperation 常量 ============
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;           // 不显示进度对话框
    private const ushort FOF_NOCONFIRMATION = 0x0010;   // 不弹"确认删除"
    private const ushort FOF_ALLOWUNDO = 0x0040;        // 进回收站（核心）
    private const ushort FOF_NOERRORUI = 0x0400;        // 不弹错误 UI

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;    // 双 \0 结尾；封送器自动补最后的终止符
        public string pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;   // 用 int 避免 bool 封送差异
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>
    /// 将文件/文件夹移入系统回收站（安全兜底：禁止物理删除）。
    /// 返回是否成功。
    /// </summary>
    public static bool DeleteToRecycleBin(string path)
    {
        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
            return false;

        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            // 末尾补一个 \0，与封送器自动追加的终止符构成"双 \0 结尾"
            pFrom = path + "\0",
            pTo = "\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };
        var ret = SHFileOperation(ref op);
        // 0 = 成功；DE_INVALIDFILES(0x7C) 等错误码返回非零
        return ret == 0 && op.fAnyOperationsAborted == 0;
    }

    // ============================================================
    // 回收站条目解析与恢复
    // ============================================================

    /// <summary>回收站中的一条条目（$I 元数据 + $R 实体文件）。</summary>
    public class RecycleEntry
    {
        /// <summary>原始完整路径（删除前的位置）</summary>
        public string OriginalPath = "";
        /// <summary>删除时间</summary>
        public DateTime DeletedTime;
        /// <summary>原始大小（字节；目录为 0）</summary>
        public long Size;
        /// <summary>$I 元数据文件路径</summary>
        public string MetaPath = "";
        /// <summary>$R 实体文件/文件夹路径</summary>
        public string DataPath = "";
        /// <summary>是否目录</summary>
        public bool IsDirectory;
    }

    /// <summary>
    /// 枚举所有本地盘回收站中的条目；originalPath 非 null 时仅返回原始路径精确匹配（忽略大小写）的条目，
    /// 按删除时间倒序（最新的在前）。
    /// </summary>
    public static List<RecycleEntry> FindRecycleEntries(string? originalPath = null)
    {
        var result = new List<RecycleEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

            var recycleRoot = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            if (!Directory.Exists(recycleRoot)) continue;

            // 每个 SID 子目录是一个用户的回收站分区
            string[] sidDirs;
            try { sidDirs = Directory.GetDirectories(recycleRoot); }
            catch { continue; }

            foreach (var sid in sidDirs)
            {
                string[] metaFiles;
                try { metaFiles = Directory.GetFiles(sid, "$I*.*"); }
                catch { continue; } // 无权限访问其他用户分区，跳过

                foreach (var meta in metaFiles)
                {
                    var entry = ParseMetaFile(meta);
                    if (entry == null) continue;
                    if (originalPath != null &&
                        !string.Equals(entry.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Add(entry);
                }
            }
        }
        result.Sort((a, b) => b.DeletedTime.CompareTo(a.DeletedTime));
        return result;
    }

    /// <summary>解析单个 $I 元数据文件。</summary>
    private static RecycleEntry? ParseMetaFile(string metaPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(metaPath);
            if (bytes.Length < 24) return null;

            // $I 布局：[0..7] 版本号(1/2)  [8..15] 原大小  [16..23] 删除时间 FILETIME
            var version = BitConverter.ToInt64(bytes, 0);
            var size = BitConverter.ToInt64(bytes, 8);
            var fileTime = BitConverter.ToInt64(bytes, 16);
            if (fileTime <= 0) return null;

            // 原始路径：版本2 用 8 字节名字长（字符数），版本1 用 4 字节
            int nameLenOffset;
            int nameLen;
            if (version == 2)
            {
                if (bytes.Length < 32) return null;
                nameLen = (int)BitConverter.ToInt64(bytes, 24);
                nameLenOffset = 32;
            }
            else
            {
                nameLen = BitConverter.ToInt32(bytes, 24);
                nameLenOffset = 28;
            }
            if (nameLen <= 0 || nameLenOffset + nameLen * 2 > bytes.Length) return null;

            var original = Encoding.Unicode.GetString(bytes, nameLenOffset, nameLen * 2)
                .TrimEnd('\0');
            if (string.IsNullOrEmpty(original)) return null;

            // $R 实体与 $I 同名前缀（$Ixxxx → $Rxxxx）
            var dataPath = Path.Combine(
                Path.GetDirectoryName(metaPath)!,
                "$R" + Path.GetFileName(metaPath).Substring(2));
            if (!File.Exists(dataPath) && !Directory.Exists(dataPath)) return null;

            return new RecycleEntry
            {
                OriginalPath = original,
                DeletedTime = DateTime.FromFileTime(fileTime),
                Size = size,
                MetaPath = metaPath,
                DataPath = dataPath,
                IsDirectory = Directory.Exists(dataPath)
            };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// 将指定原始路径最近一次删除的内容从回收站恢复到原位置。
    /// 若原位置已被占用则自动附加恢复后缀。返回是否成功。
    /// </summary>
    public static bool Restore(string originalPath, out string restoredTo)
    {
        restoredTo = originalPath;
        var entries = FindRecycleEntries(originalPath);
        if (entries.Count == 0) return false;
        return RestoreEntry(entries[0], out restoredTo);
    }

    /// <summary>恢复单条回收站条目到其原始路径。</summary>
    public static bool RestoreEntry(RecycleEntry entry, out string restoredTo)
    {
        restoredTo = entry.OriginalPath;

        // 原位置已被占用 → 追加后缀避免覆盖用户数据
        if (File.Exists(restoredTo) || Directory.Exists(restoredTo))
        {
            var dir = Path.GetDirectoryName(restoredTo) ?? "";
            var name = Path.GetFileNameWithoutExtension(restoredTo);
            var ext = entry.IsDirectory ? "" : Path.GetExtension(restoredTo);
            var suffix = "_已恢复";
            var i = 1;
            while (File.Exists(restoredTo) || Directory.Exists(restoredTo))
                restoredTo = Path.Combine(dir, name + suffix + (i > 1 ? i.ToString() : "") + ext);
        }

        try
        {
            if (entry.IsDirectory)
                Directory.Move(entry.DataPath, restoredTo);
            else
                File.Move(entry.DataPath, restoredTo);

            // 恢复成功后清理 $I 元数据，避免残留幽灵条目
            try { File.Delete(entry.MetaPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 查找 rootDir 下所有空文件夹（递归，自底向上；父目录因子目录全空而变空的也会列出）。
    /// 返回按路径长度倒序（先深后浅）的列表。
    /// </summary>
    public static List<string> FindEmptyDirectories(string rootDir)
    {
        var empty = new List<string>();
        if (!Directory.Exists(rootDir)) return empty;

        // 非递归手动 DFS，避免委托分配；跳过无法访问的子树
        var stack = new Stack<string>();
        stack.Push(rootDir);
        var all = new List<string>();
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            all.Add(dir);
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var sub in subs) stack.Push(sub);
        }

        // 自底向上判定：无文件且（无子目录 或 子目录全部为空）
        // 用集合倒序遍历近似实现：路径更深的后处理，子目录已判定为空则从父的子目录计数中扣除
        var emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        all.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (var dir in all)
        {
            bool hasFile;
            string[] subs;
            try
            {
                hasFile = Directory.EnumerateFiles(dir).GetEnumerator().MoveNext();
                subs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            if (hasFile) continue;

            var allSubEmpty = true;
            foreach (var sub in subs)
            {
                if (!emptySet.Contains(sub)) { allSubEmpty = false; break; }
            }
            if (allSubEmpty)
            {
                emptySet.Add(dir);
                if (!string.Equals(dir, rootDir, StringComparison.OrdinalIgnoreCase))
                    empty.Add(dir);
            }
        }

        empty.Sort((a, b) => b.Length.CompareTo(a.Length));
        return empty;
    }
}
