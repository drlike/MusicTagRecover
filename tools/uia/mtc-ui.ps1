<#
UIA debugging toolkit for MusicTagClone (WPF, net10.0-windows).
PS 5.1 compatible, pure ASCII. Chinese UI text is matched by CODEPOINT arrays
via -Codes (PS 5.1 console mangles Chinese args; codepoints are safe).

Usage:
  powershell -ExecutionPolicy Bypass -File tools\uia\mtc-ui.ps1 -Cmd <cmd> [params]

Commands:
  help             show this help
  launch           start app via WMI (survives shell job object); -ExePath optional
  wait             poll until main window appears; -Timeout seconds (default 15)
  move             move/resize main window; -X -Y -W -H (logical px), -Scale override
  shot             screenshot target window (physical px) -> -OutFile; -Full = screen
  close            graceful WM_CLOSE to main window
  kill             force Stop-Process
  mem              print WorkingSet / PrivateMB / Handles
  log              print today's ERROR/Exception lines from newest log
  find             locate window: -Target main|dialog|native; print hwnd/class/rect/codes
  tree             dump UIA subtree of target: -Depth (default 5), -MaxNodes (default 400)
  click            UIA-Invoke Button whose name starts with -Codes (in target subtree)
  toggle           UIA-Toggle CheckBox matched by -Codes prefix or -Index (Nth overall)
  state            print ToggleState of element matched by -Codes
  checkboxstates   list all CheckBoxes in target: index | ToggleState | name codes
  gridrows         count DataGrid rows (ControlType.DataItem) in target
  select           select Nth DataGrid row; -Index (0-based)
  scroll           scroll first DataGrid: -HPct -VPct (0..100, -1 = keep axis)
  text             dump Text elements (raw + codepoints); optional ASCII -Regex filter
  keys             focus target window, send -Seq: ctrl+a|ctrl+s|ctrl+z|ctrl+c|ctrl+v|
                   alt+n|delete|enter|esc|tab|down|up|left|right|home|end|f5|space|a..z
  bmclick          NATIVE dialog: EnumChildWindows Button with title prefix -Codes, BM_CLICK
  wmclose          PostMessage WM_CLOSE to top-level window with title prefix -Codes
  clickat          raw mouse click at PHYSICAL px (-X -Y). Last resort: prefer UIA.

Common codepoints:
  "Music"        0x4D,0x75,0x73,0x69,0x63        (main window title prefix)
  select-folder  0x9009,0x62E9                   dialog title / confirm button
  select-all     0x5168,0x9009
  cancel         0x53D6,0x6D88
  batch(auto-match) 0x6279,0x91CF

Notes (hard-won):
- Prefer UIA Invoke/Toggle over mouse: mouse px are DPI-scaled and fragile.
- WPF owned dialogs are top-level windows: find via EnumWindows(pid) + FromHandle.
- keybd_event (not SendKeys/WinForms) to avoid assembly load issues in PS 5.1.
- Screenshots/clicks use physical pixels; window -move uses logical px * DPI scale.
#>
param(
  [Parameter(Mandatory = $true)][string]$Cmd,
  [string]$ExePath,
  [int]$X = 0, [int]$Y = 0, [int]$W = 1500, [int]$H = 980,
  [double]$Scale = 0,
  [string]$OutFile,
  [switch]$Full,
  [int[]]$Codes,
  [string]$CodesHex,   # comma-separated hex, e.g. "81EA,52A8" (safer than -Codes via -File)
  [string]$Target = 'main',      # main | dialog | native
  [int]$Index = -1,
  [int]$Depth = 5,
  [int]$MaxNodes = 400,
  [int]$HPct = -1, [int]$VPct = -1,
  [string]$Seq,
  [string]$Regex,
  [int]$Timeout = 15,
  [string]$ProcName = 'MusicTagClone'
)
$ErrorActionPreference = 'Stop'

# -File 传参时逗号数组会退化为单字符串，统一从十六进制串解析
if ($CodesHex) {
  $Codes = @($CodesHex.Split(',') | ForEach-Object { [Convert]::ToInt32($_.Trim(), 16) })
}

# ---- Win32 helper (C# 5 compatible: no out-var, no interpolation, no ?.) ----
if (-not ('Ui' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class Ui
{
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int index);

  public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
  public static string Title(IntPtr h) { StringBuilder sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
  public static string Cls(IntPtr h) { StringBuilder sb = new StringBuilder(128); GetClassName(h, sb, 128); return sb.ToString(); }
  public static string Codes(string s, int max) {
    StringBuilder sb = new StringBuilder();
    int n = s.Length; if (max > 0 && n > max) n = max;
    for (int i = 0; i < n; i++) { sb.Append(((int)s[i]).ToString("X4")); sb.Append(' '); }
    return sb.ToString().TrimEnd();
  }
  public static bool TitleHasPrefix(IntPtr h, int[] codes) {
    string t = Title(h);
    if (codes == null || codes.Length == 0) return t.Length > 0;
    if (t.Length < codes.Length) return false;
    for (int i = 0; i < codes.Length; i++) if ((int)t[i] != codes[i]) return false;
    return true;
  }
  public static IntPtr FindTopLevel(uint pid, int[] titlePrefix) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h) && TitleHasPrefix(h, titlePrefix)) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static IntPtr FindOtherTopLevel(uint pid, IntPtr exclude) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h) && h != exclude) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static IntPtr FindNativeDialog(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h) && Cls(h) == "#32777") { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static string ListChildren(IntPtr parent) {
    StringBuilder sb = new StringBuilder();
    EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
      sb.Append(h.ToInt64()).Append('|').Append(Cls(h)).Append('|').Append(Codes(Title(h), 12)).Append('\n');
      return true;
    }, IntPtr.Zero);
    return sb.ToString();
  }
  public static bool ClickChildButton(IntPtr parent, int[] prefix) {
    bool[] hit = new bool[] { false };
    EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
      if (Cls(h) != "Button") return true;
      if (TitleHasPrefix(h, prefix)) { SendMessage(h, 0x00F5, IntPtr.Zero, IntPtr.Zero); hit[0] = true; return false; }
      return true;
    }, IntPtr.Zero);
    return hit[0];
  }
  public static void Focus(IntPtr h) {
    uint dummy; uint cur = GetCurrentThreadId();
    uint tgt = GetWindowThreadProcessId(GetForegroundWindow(), out dummy);
    AttachThreadInput(cur, tgt, true); SetForegroundWindow(h); AttachThreadInput(cur, tgt, false);
  }
  static void Tap(byte vk) {
    keybd_event(vk, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 2, UIntPtr.Zero);
  }
  static byte ResolveKey(string k) {
    if (k.Length == 1 && k[0] >= 'a' && k[0] <= 'z') return (byte)('A' + (k[0] - 'a'));
    if (k.Length == 1 && k[0] >= '0' && k[0] <= '9') return (byte)k[0];
    switch (k) {
      case "del": case "delete": return 0x2E;
      case "enter": case "return": return 0x0D;
      case "esc": return 0x1B;
      case "tab": return 0x09;
      case "space": return 0x20;
      case "backspace": return 0x08;
      case "down": return 0x28; case "up": return 0x26; case "left": return 0x25; case "right": return 0x27;
      case "home": return 0x24; case "end": return 0x23;
      case "f5": return 0x74;
    }
    return 0;
  }
  public static void SendSeq(string seq) {
    string[] parts = seq.ToLower().Split('+');
    string key = parts[parts.Length - 1];
    bool c = false, a = false, s = false;
    foreach (string p in parts) {
      if (p == "ctrl") c = true; else if (p == "alt") a = true; else if (p == "shift") s = true;
    }
    byte vk = ResolveKey(key);
    if (vk == 0) throw new ArgumentException("unknown key: " + key);
    if (c) keybd_event(0x11, 0, 0, UIntPtr.Zero);
    if (a) keybd_event(0x12, 0, 0, UIntPtr.Zero);
    if (s) keybd_event(0x10, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(60);
    Tap(vk);
    if (s) keybd_event(0x10, 0, 2, UIntPtr.Zero);
    if (a) keybd_event(0x12, 0, 2, UIntPtr.Zero);
    if (c) keybd_event(0x11, 0, 2, UIntPtr.Zero);
  }
  public static double DpiScale() {
    IntPtr dc = GetDC(IntPtr.Zero); int d = GetDeviceCaps(dc, 88); ReleaseDC(IntPtr.Zero, dc);
    return d / 96.0;
  }
  public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(120);
    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(60);
    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
  }
}
'@
}

# ---- helpers ----
$script:MainCodes = @(0x4D, 0x75, 0x73, 0x69, 0x63)   # "Music"

function Get-Proc {
  $p = Get-Process $ProcName -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $p) { throw "process '$ProcName' is not running" }
  return $p
}

function Get-TargetHwnd {
  $p = Get-Proc
  $procId = [uint32]$p.Id
  $mainH = [Ui]::FindTopLevel($procId, $script:MainCodes)
  if ($Target -eq 'main') {
    if ($mainH -ne [IntPtr]::Zero) { return $mainH }
    return [Ui]::FindTopLevel($procId, $null)
  }
  if ($Target -eq 'native') {
    $h = [Ui]::FindNativeDialog($procId)
    if ($h -eq [IntPtr]::Zero) { throw "no native (#32777) dialog found" }
    return $h
  }
  # dialog: title prefix codes first, else any visible top-level that is not main
  if ($Codes -and $Codes.Count -gt 0) {
    $h = [Ui]::FindTopLevel($procId, $Codes)
    if ($h -ne [IntPtr]::Zero) { return $h }
  }
  $h = [Ui]::FindOtherTopLevel($procId, $mainH)
  if ($h -eq [IntPtr]::Zero) { throw "no dialog window found (use -Codes to match title prefix)" }
  return $h
}

function Get-UiRoot {
  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
  $h = Get-TargetHwnd
  if ($h -eq [IntPtr]::Zero) { throw "target window not found" }
  return [System.Windows.Automation.AutomationElement]::FromHandle($h)
}

function Test-NamePrefix($e, [int[]]$prefix) {
  if (-not $prefix -or $prefix.Count -eq 0) { return $false }
  $n = $e.Current.Name
  if ($n.Length -lt $prefix.Count) { return $false }
  for ($i = 0; $i -lt $prefix.Count; $i++) { if ([int]$n[$i] -ne $prefix[$i]) { return $false } }
  return $true
}

function Find-ByControlType($root, $ctlType) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctlType)
  return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-ToggleState($e) {
  try {
    $tp = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $tp.Current.ToggleState.ToString()
  } catch { return "n/a" }
}

function Show-ElementLine($e, $tag) {
  $n = $e.Current.Name
  Write-Output ("{0} ct={1} class={2} name='{3}' codes=[{4}]" -f `
    $tag, $e.Current.ControlType.ProgrammaticName, $e.Current.ClassName, $n, [Ui]::Codes($n, 8))
}

function Get-DataGrid($root) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'DataGrid')
  $dg = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
  if (-not $dg) { throw "no DataGrid found in target" }
  return $dg
}

function Get-GridRows($root) {
  $dg = Get-DataGrid $root
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::DataItem)
  return @($dg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
}

# ---- dispatcher ----
switch ($Cmd) {

  'help' {
    foreach ($l in (Get-Content $PSCommandPath -TotalCount 70)) {
      if ($l -eq '<#') { continue }
      if ($l -eq '#>') { break }
      Write-Output $l
    }
  }

  'launch' {
    if (-not $ExePath) {
      $repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
      $ExePath = Join-Path $repoRoot 'bin\Debug\net10.0-windows\MusicTagClone.exe'
    }
    if (-not (Test-Path $ExePath)) { throw "exe not found: $ExePath" }
    # WMI create detaches from shell job object (child survives this session)
    $r = ([wmiclass]"\\.\root\cimv2:Win32_Process").Create('"' + $ExePath + '"')
    Write-Output ("launched pid={0} return={1}" -f $r.ProcessId, $r.ReturnValue)
  }

  'wait' {
    $p = Get-Proc
    $deadline = (Get-Date).AddSeconds($Timeout)
    while ((Get-Date) -lt $deadline) {
      $h = [Ui]::FindTopLevel([uint32]$p.Id, $script:MainCodes)
      if ($h -ne [IntPtr]::Zero) { Write-Output ("main window ready hwnd={0}" -f $h); exit 0 }
      Start-Sleep -Milliseconds 400
    }
    throw "main window not ready within ${Timeout}s"
  }

  'move' {
    $h = Get-TargetHwnd
    $sc = if ($Scale -gt 0) { $Scale } else { [Ui]::DpiScale() }
    [void][Ui]::MoveWindow($h, [int]($X * $sc), [int]($Y * $sc), [int]($W * $sc), [int]($H * $sc), $true)
    Write-Output ("moved scale={0} hwnd={1}" -f $sc, $h)
  }

  'shot' {
    Add-Type -AssemblyName System.Drawing
    if (-not $OutFile) { throw "-OutFile required" }
    if ($Full) {
      $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
      $r = New-Object Ui+RECT; $r.Left = $b.X; $r.Top = $b.Y; $r.Right = $b.X + $b.Width; $r.Bottom = $b.Y + $b.Height
    } else {
      $r = [Ui]::Rect((Get-TargetHwnd))
    }
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
    $bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output ("saved {0} ({1}x{2})" -f $OutFile, $w, $ht)
  }

  'close' { $h = Get-TargetHwnd; [void][Ui]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero); Write-Output "WM_CLOSE sent" }

  'kill' { Get-Proc | Stop-Process -Force; Write-Output "killed" }

  'mem' {
    $p = Get-Proc | Select-Object Id, @{ n = 'WS_MB'; e = { [math]::Round($_.WorkingSet64 / 1MB, 1) } },
      @{ n = 'PrivateMB'; e = { [math]::Round($_.PrivateMemorySize64 / 1MB, 1) } }, Handles
    $p | Format-List | Out-String | Write-Output
  }

  'log' {
    $repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $dirs = @(Join-Path $repoRoot 'bin\Debug\net10.0-windows\log'), (Join-Path $repoRoot 'log')
    $f = $null
    foreach ($d in $dirs) {
      if (Test-Path $d) {
        $cand = Get-ChildItem $d -Filter 'log-*.log' -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($cand) { $f = $cand; break }
      }
    }
    if (-not $f) { throw "no log file found" }
    $err = Get-Content $f.FullName | Where-Object { $_ -match 'ERROR|Exception' }
    if ($err) { $err | Select-Object -Last 20 | Write-Output } else { Write-Output ("no errors in " + $f.Name) }
  }

  'find' {
    $h = Get-TargetHwnd
    $r = [Ui]::Rect($h)
    Write-Output ("hwnd={0} class={1} pid={2} rect=({3},{4})-({5},{6})" -f `
      $h, [Ui]::Cls($h), [Ui]::PidOf($h), $r.Left, $r.Top, $r.Right, $r.Bottom)
    Write-Output ("title='{0}' codes=[{1}]" -f [Ui]::Title($h), [Ui]::Codes([Ui]::Title($h), 16))
  }

  'tree' {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $root = Get-UiRoot
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $script:nn = 0
    function Walk($e, $d) {
      if ($script:nn -ge $MaxNodes) { return }
      $script:nn++
      Show-ElementLine $e ("L" + $d)
      if ($d -ge $Depth) { return }
      $child = $walker.GetFirstChild($e)
      while ($child -and $script:nn -lt $MaxNodes) {
        Walk $child ($d + 1)
        $child = $walker.GetNextSibling($child)
      }
    }
    Walk $root 0
    Write-Output ("nodes={0}" -f $script:nn)
  }

  'click' {
    $root = Get-UiRoot
    $btns = @(Find-ByControlType $root ([System.Windows.Automation.ControlType]::Button))
    if ($Index -ge 0) {
      if ($Index -ge $btns.Count) { throw ("button index out of range: {0}/{1}" -f $Index, $btns.Count) }
      $btns[$Index].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      Show-ElementLine $btns[$Index] "invoked"
      exit 0
    }
    if (-not $Codes -or $Codes.Count -eq 0) { throw "-Codes required (or -Index)" }
    foreach ($b in $btns) {
      if (Test-NamePrefix $b $Codes) {
        $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Show-ElementLine $b "invoked"
        exit 0
      }
    }
    throw ("no button matched codes [{0}]" -f ([Ui]::Codes(-join ($Codes | ForEach-Object { [char]$_ }), 8)))
  }

  'toggle' {
    $root = Get-UiRoot
    $boxes = @(Find-ByControlType $root ([System.Windows.Automation.ControlType]::CheckBox))
    $hit = $null
    if ($Index -ge 0) {
      if ($Index -ge $boxes.Count) { throw ("checkbox index out of range: {0}/{1}" -f $Index, $boxes.Count) }
      $hit = $boxes[$Index]
    } else {
      if (-not $Codes -or $Codes.Count -eq 0) { throw "-Codes required (or -Index)" }
      foreach ($b in $boxes) { if (Test-NamePrefix $b $Codes) { $hit = $b; break } }
    }
    if (-not $hit) { throw "no checkbox matched" }
    $hit.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 150
    Show-ElementLine $hit ("toggled state=" + (Get-ToggleState $hit))
  }

  'state' {
    $root = Get-UiRoot
    $boxes = @(Find-ByControlType $root ([System.Windows.Automation.ControlType]::CheckBox))
    if (-not $Codes -or $Codes.Count -eq 0) { throw "-Codes required" }
    foreach ($b in $boxes) {
      if (Test-NamePrefix $b $Codes) {
        Show-ElementLine $b ("state=" + (Get-ToggleState $b))
        exit 0
      }
    }
    throw "no checkbox matched"
  }

  'checkboxstates' {
    $root = Get-UiRoot
    $boxes = @(Find-ByControlType $root ([System.Windows.Automation.ControlType]::CheckBox))
    Write-Output ("checkboxes: {0}" -f $boxes.Count)
    for ($i = 0; $i -lt $boxes.Count; $i++) {
      $n = $boxes[$i].Current.Name
      Write-Output ("{0} | {1} | [{2}]" -f $i, (Get-ToggleState $boxes[$i]), [Ui]::Codes($n, 6))
    }
  }

  'gridrows' {
    $root = Get-UiRoot
    $rows = Get-GridRows $root
    Write-Output ("grid rows: {0}" -f $rows.Count)
  }

  'select' {
    $root = Get-UiRoot
    $rows = Get-GridRows $root
    if ($Index -lt 0 -or $Index -ge $rows.Count) { throw ("-Index required, range 0..{0}" -f ($rows.Count - 1)) }
    $rows[$Index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output ("selected row {0}/{1}" -f $Index, $rows.Count)
  }

  'scroll' {
    $root = Get-UiRoot
    $dg = Get-DataGrid $root
    $sp = $dg.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $sp.SetScrollPercent($HPct, $VPct)
    Write-Output ("scrolled h={0} v={1}" -f $HPct, $VPct)
  }

  'text' {
    $root = Get-UiRoot
    $texts = @(Find-ByControlType $root ([System.Windows.Automation.ControlType]::Text))
    foreach ($t in $texts) {
      $n = $t.Current.Name
      if ($n.Length -eq 0) { continue }
      if ($Regex -and $n -notmatch $Regex) { continue }
      Write-Output ("'{0}' codes=[{1}]" -f $n, [Ui]::Codes($n, 12))
    }
  }

  'keys' {
    if (-not $Seq) { throw "-Seq required (e.g. ctrl+a)" }
    $h = Get-TargetHwnd
    [Ui]::Focus($h)
    Start-Sleep -Milliseconds 300
    [Ui]::SendSeq($Seq)
    Write-Output ("sent '{0}' to hwnd={1}" -f $Seq, $h)
  }

  'bmclick' {
    if (-not $Codes -or $Codes.Count -eq 0) { throw "-Codes required (button title prefix)" }
    $h = Get-TargetHwnd
    if ([Ui]::ClickChildButton($h, $Codes)) { Write-Output ("BM_CLICK sent, hwnd={0}" -f $h) }
    else {
      Write-Output "no matching Button child; children:"
      Write-Output ([Ui]::ListChildren($h))
      exit 2
    }
  }

  'wmclose' {
    if (-not $Codes -or $Codes.Count -eq 0) { throw "-Codes required (title prefix)" }
    $p = Get-Proc
    $h = [Ui]::FindTopLevel([uint32]$p.Id, $Codes)
    if ($h -eq [IntPtr]::Zero) { throw "no window matched" }
    [void][Ui]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Write-Output ("WM_CLOSE sent to hwnd={0}" -f $h)
  }

  'clickat' {
    [Ui]::Click($X, $Y)
    Write-Output ("clicked physical ({0},{1})" -f $X, $Y)
  }

  default { throw "unknown cmd '$Cmd'; try -Cmd help" }
}
