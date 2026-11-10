namespace CloudWallpaper;

/// <summary>定位桌面图标层，并跨进程读取每个图标的矩形。</summary>
internal sealed class DesktopIcons : IDisposable
{
    const uint LVM_GETITEMCOUNT = 0x1004, LVM_GETITEMRECT = 0x100E;
    const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020;
    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;

    IntPtr _listView, _process, _remote;

    /// <summary>找到应当承载壁纸窗口的父窗口（图标层后面的 WorkerW）。</summary>
    public static IntPtr FindWallpaperHost(out IntPtr defView)
    {
        IntPtr progman = Native.FindWindow("Progman", null);
        // 让 Progman 在图标层后面生成一个 WorkerW
        Native.SendMessageTimeout(progman, 0x052C, (IntPtr)0xD, (IntPtr)0x1, 0, 1000, out _);
        Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        IntPtr workerW = IntPtr.Zero, dv = IntPtr.Zero;
        Native.EnumWindows((top, _) =>
        {
            IntPtr v = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (v != IntPtr.Zero)
            {
                dv = v;
                workerW = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);

        // 新版 Windows 11：WorkerW 是 Progman 的子窗口
        if (workerW == IntPtr.Zero) workerW = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        defView = dv;
        return workerW != IntPtr.Zero ? workerW : progman;
    }

    bool Connect()
    {
        if (_listView != IntPtr.Zero && Native.IsWindow(_listView)) return true;
        Disconnect();

        FindWallpaperHostNoMessage(out IntPtr defView);
        if (defView == IntPtr.Zero) return false;
        IntPtr lv = Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        if (lv == IntPtr.Zero) return false;

        Native.GetWindowThreadProcessId(lv, out uint pid);
        IntPtr proc = Native.OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (proc == IntPtr.Zero) return false;
        IntPtr mem = Native.VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)64, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (mem == IntPtr.Zero) { Native.CloseHandle(proc); return false; }

        _listView = lv; _process = proc; _remote = mem;
        return true;
    }

    static void FindWallpaperHostNoMessage(out IntPtr defView)
    {
        IntPtr dv = IntPtr.Zero;
        Native.EnumWindows((top, _) =>
        {
            IntPtr v = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (v != IntPtr.Zero) { dv = v; return false; }
            return true;
        }, IntPtr.Zero);
        defView = dv;
    }

    void Disconnect()
    {
        if (_process != IntPtr.Zero)
        {
            if (_remote != IntPtr.Zero) Native.VirtualFreeEx(_process, _remote, UIntPtr.Zero, MEM_RELEASE);
            Native.CloseHandle(_process);
        }
        _listView = _process = _remote = IntPtr.Zero;
    }

    /// <summary>读取所有图标矩形（含文字标签），坐标换算到 target 窗口的客户区。失败返回 null。</summary>
    public RECT[] Read(IntPtr target)
    {
        if (!Connect()) return null;
        if (!Native.IsWindowVisible(_listView)) return Array.Empty<RECT>();   // 用户隐藏了桌面图标

        if (Native.SendMessageTimeout(_listView, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG, 300, out IntPtr countRes) == IntPtr.Zero) return null;
        int count = (int)countRes;
        var result = new RECT[count];
        var size = (UIntPtr)16;

        for (int i = 0; i < count; i++)
        {
            var rc = new RECT();   // Left = LVIR_BOUNDS (0)
            if (!Native.WriteProcessMemory(_process, _remote, ref rc, size, out _)) { Disconnect(); return null; }
            if (Native.SendMessageTimeout(_listView, LVM_GETITEMRECT, (IntPtr)i, _remote,
                    Native.SMTO_ABORTIFHUNG, 300, out IntPtr ok) == IntPtr.Zero || ok == IntPtr.Zero) return null;
            if (!Native.ReadProcessMemory(_process, _remote, ref rc, size, out _)) { Disconnect(); return null; }
            Native.MapWindowPoints(_listView, target, ref rc, 2);
            result[i] = rc;
        }
        return result;
    }

    public void Dispose() => Disconnect();
}
