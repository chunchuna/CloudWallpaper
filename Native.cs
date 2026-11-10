using System.Runtime.InteropServices;
using System.Text;

namespace CloudWallpaper;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PIXELFORMATDESCRIPTOR
{
    public ushort nSize, nVersion;
    public uint dwFlags;
    public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift;
    public byte cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
    public byte cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
    public uint dwLayerMask, dwVisibleMask, dwDamageMask;
}

internal static class Native
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern int MapWindowPoints(IntPtr from, IntPtr to, ref RECT rect, int count);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder pv, uint winIni);

    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, ref RECT buf, UIntPtr size, out UIntPtr read);
    [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, ref RECT buf, UIntPtr size, out UIntPtr written);

    [DllImport("gdi32.dll")] public static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] public static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] public static extern bool SwapBuffers(IntPtr hdc);

    // ---- 显示器刷新率、计时精度 ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, byte[] devMode);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

    /// <summary>所有显示器当前刷新率里最高的那个（Hz）；取不到时返回 60。</summary>
    public static int MaxRefreshRate()
    {
        int best = 0;
        try
        {
            foreach (Screen s in Screen.AllScreens)
            {
                var dm = new byte[220];                              // DEVMODEW
                BitConverter.TryWriteBytes(dm.AsSpan(68, 2), (ushort)220);   // dmSize
                if (!EnumDisplaySettings(s.DeviceName, -1, dm)) continue;    // -1 = ENUM_CURRENT_SETTINGS
                int hz = BitConverter.ToInt32(dm, 184);              // dmDisplayFrequency
                if (hz > best && hz <= 1000) best = hz;
            }
        }
        catch { }
        return best >= 30 ? best : 60;
    }

    // ---- 高精度等待：高刷新率下限帧用，不靠空转 ----
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerEx(IntPtr attrs, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    /// <summary>新建一个高精度定时器（Windows 10 1803 起支持）；不支持时返回 0，调用方退回普通 Sleep。</summary>
    public static IntPtr CreatePreciseTimer() => CreateWaitableTimerEx(IntPtr.Zero, null, 0x2, 0x1F0003);   // HIGH_RESOLUTION, TIMER_ALL_ACCESS

    /// <summary>睡大约 seconds 秒，误差在零点几毫秒以内。</summary>
    public static void PreciseSleep(IntPtr timer, double seconds)
    {
        if (seconds <= 0) return;
        long due = -(long)(seconds * 10_000_000);     // 负数 = 相对时间，单位 100 纳秒
        if (timer != IntPtr.Zero && due < 0 && SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(timer, 100);
        else Thread.Sleep(1);
    }

    // ---- 桌面是否被其他窗口完全挡住 ----
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    /// <summary>
    /// 所有显示器是否都被不透明的窗口盖满了（最大化的浏览器、全屏游戏、几个窗口拼满屏幕等）。
    /// 盖满时壁纸一个像素都看不到，可以完全停止渲染。
    /// </summary>
    public static bool DesktopFullyCovered()
    {
        // 还露在外面的区域：从每块屏幕开始，按从上到下的顺序减去每个窗口
        var open = new List<RECT>();
        foreach (Screen s in Screen.AllScreens)
            open.Add(new RECT { Left = s.Bounds.Left, Top = s.Bounds.Top, Right = s.Bounds.Right, Bottom = s.Bounds.Bottom });
        uint self = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (open.Count == 0) return false;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self) return true;                                   // 自己的菜单窗口不算
            long ex = GetWindowLongPtr(hwnd, -20).ToInt64();                // GWL_EXSTYLE
            const long Transparent = 0x20, Layered = 0x80000, ToolWindow = 0x80, NoActivate = 0x08000000;
            // 点击穿透的窗口、各种半透明悬浮层（帧率显示、录屏工具条等）不算遮挡
            if ((ex & Transparent) != 0 || ((ex & Layered) != 0 && (ex & (ToolWindow | NoActivate)) != 0)) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;   // DWMWA_CLOAKED：其他虚拟桌面上的窗口
            string cls = ClassOf(hwnd);
            if (cls is "Progman" or "WorkerW") return true;                 // 桌面本身
            if (DwmGetWindowAttribute(hwnd, 9, out RECT r, 16) != 0 && !GetWindowRect(hwnd, out r)) return true;   // DWMWA_EXTENDED_FRAME_BOUNDS
            if (r.Width <= 0 || r.Height <= 0) return true;

            for (int i = open.Count - 1; i >= 0; i--)
            {
                RECT o = open[i];
                if (r.Left >= o.Right || r.Right <= o.Left || r.Top >= o.Bottom || r.Bottom <= o.Top) continue;
                open.RemoveAt(i);
                // 把 o 被 r 挡住之后剩下的部分切成最多四块
                if (r.Top > o.Top) open.Add(new RECT { Left = o.Left, Top = o.Top, Right = o.Right, Bottom = r.Top });
                if (r.Bottom < o.Bottom) open.Add(new RECT { Left = o.Left, Top = r.Bottom, Right = o.Right, Bottom = o.Bottom });
                int top = Math.Max(o.Top, r.Top), bottom = Math.Min(o.Bottom, r.Bottom);
                if (r.Left > o.Left) open.Add(new RECT { Left = o.Left, Top = top, Right = r.Left, Bottom = bottom });
                if (r.Right < o.Right) open.Add(new RECT { Left = r.Right, Top = top, Right = o.Right, Bottom = bottom });
            }
            if (open.Count > 400) { open.Clear(); open.Add(new RECT { Right = 1, Bottom = 1 }); return false; }   // 异常情况：当作没盖满
            return true;
        }, IntPtr.Zero);
        return open.Count == 0;
    }

    // ---- 全局鼠标钩子 ----
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);

    // ---- 分层（逐像素透明）窗口 ----
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

    // ---- 拖动无边框窗口 ----
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint SWP_NOACTIVATE = 0x0010, SWP_NOZORDER = 0x0004, SWP_SHOWWINDOW = 0x0040;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
