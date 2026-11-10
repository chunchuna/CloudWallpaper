# Debug tool: capture the settings window (main window of CloudWallpaper) to a PNG
param([string]$Out = "$env:TEMP\cw_settings.png")
$env:LIB = $null
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class CapWin {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int L, T, R, B; }
}
"@
[CapWin]::SetProcessDPIAware() | Out-Null
$p = Get-Process CloudWallpaper -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
if (-not $p) { throw "settings window not found" }
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { throw "settings window not found" }
$r = New-Object CapWin+RECT
[CapWin]::GetWindowRect($h, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
[CapWin]::PrintWindow($h, $dc, 2) | Out-Null
$g.ReleaseHdc($dc); $g.Dispose(); $bmp.Save($Out); $bmp.Dispose()
"$($p.MainWindowTitle) -> $Out"
