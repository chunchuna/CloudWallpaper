# 调试工具：不切换窗口，直接抓取壁纸层 + 桌面图标层并合成一张图
param([string]$Out = "$env:TEMP\cw_shot.png")
$env:LIB = $null   # 仅本进程：避免无效的 LIB 路径让 Add-Type 编译失败
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Cap {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int L, T, R, B; }
  public static IntPtr DefViewTop() {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
"@
[Cap]::SetProcessDPIAware() | Out-Null
# 壁纸窗口是某个 WorkerW 的子窗口，逐个 WorkerW 查找
$wall = [IntPtr]::Zero; $ww = [IntPtr]::Zero
while ($wall -eq [IntPtr]::Zero) {
  $ww = [Cap]::FindWindowEx([IntPtr]::Zero, $ww, "WorkerW", [NullString]::Value)
  if ($ww -eq [IntPtr]::Zero) { break }
  if ([Cap]::FindWindowEx($ww, [IntPtr]::Zero, "SHELLDLL_DefView", [NullString]::Value) -ne [IntPtr]::Zero) { continue }
  $wall = [Cap]::FindWindowEx($ww, [IntPtr]::Zero, [NullString]::Value, [NullString]::Value)
}
if ($wall -eq [IntPtr]::Zero) { throw "找不到壁纸窗口，CloudWallpaper 是否在运行？" }
$r = New-Object Cap+RECT
[Cap]::GetClientRect($wall, [ref]$r) | Out-Null
$w = $r.R; $h = $r.B
function Grab($hwnd) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  [Cap]::PrintWindow($hwnd, $dc, 2) | Out-Null
  $g.ReleaseHdc($dc); $g.Dispose(); return $bmp
}
$bg = Grab $wall
$icons = Grab ([Cap]::DefViewTop())
$bg.Save($Out.Replace(".png", "_wall.png"))
$icons.MakeTransparent([System.Drawing.Color]::FromArgb(255,0,0,0))
$g = [System.Drawing.Graphics]::FromImage($bg)
$g.DrawImage($icons, 0, 0)
$g.Dispose()
$bg.Save($Out); $bg.Dispose(); $icons.Dispose()
"wall=$wall size=${w}x$h -> $Out"
