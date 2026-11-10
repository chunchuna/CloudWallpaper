# 调试工具：列出桌面相关窗口层级
$env:LIB = $null
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class Diag {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int L, T, R, B; }
  static string Cls(IntPtr h) { var sb = new StringBuilder(128); GetClassName(h, sb, 128); return sb.ToString(); }
  static string Info(IntPtr h) { RECT r; GetWindowRect(h, out r); uint pid; GetWindowThreadProcessId(h, out pid);
    return string.Format("0x{0:X} [{1}] pid={2} vis={3} rect=({4},{5},{6},{7}) parent=0x{8:X}", (long)h, Cls(h), pid, IsWindowVisible(h), r.L, r.T, r.R, r.B, (long)GetParent(h)); }
  public static List<string> Dump(uint myPid) {
    SetProcessDPIAware();
    var o = new List<string>();
    EnumWindows((h, l) => {
      string c = Cls(h); uint pid; GetWindowThreadProcessId(h, out pid);
      if (c == "Progman" || c == "WorkerW" || pid == myPid) {
        o.Add(Info(h));
        EnumChildWindows(h, (ch, l2) => { o.Add("    " + Info(ch)); return true; }, IntPtr.Zero);
      }
      return true; }, IntPtr.Zero);
    return o;
  }
}
"@
$p = Get-Process CloudWallpaper -ErrorAction SilentlyContinue | Select-Object -First 1
[Diag]::Dump([uint32]$(if ($p) { $p.Id } else { 0 }))
