using System.Runtime.InteropServices;
using System.Text;

namespace MusicTagRecover.Win32;

/// <summary>
/// Win32 P/Invoke declarations for single-instance support.
/// </summary>
internal static class NativeMethods
{
    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;
    public const int WM_COPYDATA = 0x004A;

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter,
        string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, string lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpData;
    }

    // ============================================================
    // 内存工作集回收（切换工作目录后调用）
    // ============================================================

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    /// <summary>
    /// 把当前进程暂不使用的物理内存页交还给 OS（等效 -1,-1 的 EmptyWorkingSet）。
    /// 仅在大目录切换、强制 GC 之后调用一次，让任务管理器中的内存占用快速回落。
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(proc.Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { /* 部分受限环境可能拒绝，忽略即可 */ }
    }
}
