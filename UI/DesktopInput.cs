using System.Diagnostics;
using System.Runtime.InteropServices;
using CloudWallpaper.Game;

namespace CloudWallpaper.UI;

/// <summary>
/// 壁纸窗口在图标层后面，收不到鼠标，所以联机时装一个全局鼠标钩子，只关心落在桌面上的操作：
///   左键单击空白处 → 走过去；左键单击别人的角色 → 弹出攻击/拥抱；按住中键 → 动作圆盘。
/// 钩子只是旁听，从不拦截，桌面本身的点击、框选、拖动照常工作。只在界面线程使用。
/// </summary>
internal sealed class DesktopInput : IDisposable
{
    const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208;
    const int HoldMs = 280;

    readonly Control _wallpaper;
    readonly Func<RECT[]> _icons;
    readonly Native.HookProc _proc;       // 保持引用，防止被回收
    readonly ActionWheel _wheel = new();
    readonly InteractMenu _menu = new();
    readonly System.Windows.Forms.Timer _hold = new() { Interval = HoldMs };
    IntPtr _hook;

    Point _downPt, _midPt;
    long _downAt;
    bool _downOnDesktop, _midDown;

    /// <param name="wallpaper">壁纸窗口，用来把屏幕坐标换算成壁纸坐标。</param>
    /// <param name="icons">当前桌面图标的矩形（壁纸坐标）。</param>
    public DesktopInput(Control wallpaper, Func<RECT[]> icons)
    {
        _wallpaper = wallpaper;
        _icons = icons;
        _proc = Hook;
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            Look look;
            lock (Session.World.Sync) look = Session.World.Me?.Look;
            if (_midDown && look != null) { _menu.HideMenu(); _wheel.ShowAt(_midPt, look); }
        };
        _menu.Chosen += (id, kind) => Session.Interact(id, kind);
    }

    public void SetEnabled(bool on)
    {
        if (on && _hook == IntPtr.Zero)
        {
            using Process p = Process.GetCurrentProcess();
            using ProcessModule m = p.MainModule;
            _hook = Native.SetWindowsHookEx(WH_MOUSE_LL, _proc, Native.GetModuleHandle(m?.ModuleName), 0);
        }
        else if (!on && _hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _hold.Stop(); _midDown = false;
            if (_wheel.Open) _wheel.Close(Point.Empty);
            _menu.HideMenu();
        }
    }

    /// <summary>这个屏幕位置最上面的窗口是不是桌面（图标层或壁纸层）。</summary>
    static bool IsDesktop(Point pt)
    {
        IntPtr w = Native.WindowFromPoint(new Native.POINT { X = pt.X, Y = pt.Y });
        if (w == IntPtr.Zero) return false;
        string cls = Native.ClassOf(w);
        if (cls is "SHELLDLL_DefView" or "WorkerW" or "Progman") return true;
        return cls == "SysListView32" && Native.ClassOf(Native.GetAncestor(w, 1)) == "SHELLDLL_DefView";
    }

    // 钩子回调必须很快返回：这里只记录状态，真正的处理放到消息循环里
    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Session.InRoom)
        {
            try
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                var pt = new Point(info.pt.X, info.pt.Y);
                switch ((int)wParam)
                {
                    case WM_LBUTTONDOWN:
                        _downPt = pt; _downAt = Environment.TickCount64; _downOnDesktop = IsDesktop(pt);
                        break;
                    case WM_LBUTTONUP:
                        if (_downOnDesktop && Environment.TickCount64 - _downAt < 450 &&
                            Math.Abs(pt.X - _downPt.X) < 6 && Math.Abs(pt.Y - _downPt.Y) < 6)   // 是单击，不是框选或拖动
                            _wallpaper.BeginInvoke(() => OnClick(pt));
                        _downOnDesktop = false;
                        break;
                    case WM_MBUTTONDOWN:
                        if (IsDesktop(pt)) { _midDown = true; _midPt = pt; _wallpaper.BeginInvoke(() => { _hold.Stop(); _hold.Start(); }); }
                        break;
                    case WM_MBUTTONUP:
                        if (_midDown)
                        {
                            _midDown = false;
                            _wallpaper.BeginInvoke(() =>
                            {
                                _hold.Stop();
                                if (!_wheel.Open) return;
                                int anim = _wheel.Close(pt);
                                if (anim >= 0) Session.Act(anim);
                            });
                        }
                        break;
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    void OnClick(Point screen)
    {
        if (!Session.InRoom) return;
        _menu.HideMenu();
        Point p = _wallpaper.PointToClient(screen);
        Size size = _wallpaper.ClientSize;
        if (size.Width < 1 || size.Height < 1) return;
        // 角色在屏幕上的大小系数（点击范围不小于 0.7，太小的角色也容易点中）
        float size01 = Settings.AnimalScale * 0.5f;
        float scale = _wallpaper.DeviceDpi / 96f * Math.Max(size01, 0.7f);
        float headUp = 100 * _wallpaper.DeviceDpi / 96f * size01;
        float stepPx = SpriteLayer.StepPx(_wallpaper.DeviceDpi / 96f, Settings.AnimalScale);

        // 点到了别人的角色：弹出攻击 / 站头顶
        Actor target; float tx = 0, ty = 0;
        lock (Session.World.Sync)
        {
            target = Session.World.HitTest(p.X, p.Y, size.Width, size.Height, scale, stepPx);
            if (target != null) { tx = target.X * size.Width; ty = target.Y * size.Height - target.Level * stepPx; }
        }
        if (target != null)
        {
            Point anchor = _wallpaper.PointToScreen(new Point((int)tx, (int)(ty - headUp)));
            _menu.ShowFor(target.Id, target.Name, anchor);
            return;
        }

        // 点在图标上是在操作图标，不算
        foreach (RECT r in _icons())
            if (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) return;

        Session.MoveTo(p.X / (float)size.Width, p.Y / (float)size.Height);
    }

    public void Dispose()
    {
        SetEnabled(false);
        _hold.Dispose(); _wheel.Dispose(); _menu.Dispose();
    }
}
