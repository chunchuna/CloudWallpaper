using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using CloudWallpaper.Game;

namespace CloudWallpaper.UI;

/// <summary>逐像素透明、置顶、不抢焦点的小窗口，内容是一张自己画的位图。</summary>
internal class LayeredWindow : Form
{
    readonly bool _clickThrough;

    public LayeredWindow(bool clickThrough)
    {
        _clickThrough = clickThrough;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST (| WS_EX_TRANSPARENT)
            cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000 | 0x00000008 | (_clickThrough ? 0x00000020 : 0);
            return cp;
        }
    }

    /// <summary>把位图显示到屏幕的 (x, y)。位图必须是 32 位带透明度的。</summary>
    protected void Present(Bitmap bmp, int x, int y)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = Native.SelectObject(mem, hbmp);
        try
        {
            var size = new Native.SIZE { W = bmp.Width, H = bmp.Height };
            var src = new Native.POINT();
            var pos = new Native.POINT { X = x, Y = y };
            var blend = new Native.BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };   // AC_SRC_OVER, AC_SRC_ALPHA
            Native.UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);   // ULW_ALPHA
        }
        finally
        {
            Native.SelectObject(mem, old);
            Native.DeleteObject(hbmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected static float DpiScale => Screen.PrimaryScreen == null ? 1f : GetScale();
    static float GetScale() { using Graphics g = Graphics.FromHwnd(IntPtr.Zero); return g.DpiX / 96f; }
}

/// <summary>
/// 动作圆盘：按住鼠标中键出现，把鼠标移到某个动作上松开即执行。
/// 它不接收鼠标（点击会穿透），高亮完全根据鼠标相对圆心的方向计算。
/// </summary>
internal sealed class ActionWheel : LayeredWindow
{
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    readonly System.Diagnostics.Stopwatch _clock = new();
    Point _center;
    Look _look;
    float _scale = 1;
    Bitmap _bmp;

    public bool Open { get; private set; }

    public ActionWheel() : base(clickThrough: true) => _timer.Tick += (_, _) => Redraw();

    public void ShowAt(Point center, Look look)
    {
        _center = center; _look = look; _scale = DpiScale;
        _clock.Restart();
        Open = true;
        Redraw();
        if (!Visible) Show();
        _timer.Start();
    }

    /// <summary>关闭圆盘，返回选中的动作；鼠标停在中心或圆盘外则返回 -1。</summary>
    public int Close(Point cursor)
    {
        int sel = Open ? Sector(cursor) : -1;
        Open = false;
        _timer.Stop();
        Hide();
        return sel < 0 ? -1 : Anim.Wheel[sel].Anim;
    }

    float Outer => 168 * _scale;
    float Inner => 48 * _scale;

    int Sector(Point cursor)
    {
        float dx = cursor.X - _center.X, dy = cursor.Y - _center.Y, d = MathF.Sqrt(dx * dx + dy * dy);
        if (d < Inner || d > Outer * 1.6f) return -1;
        int n = Anim.Wheel.Length;
        float ang = MathF.Atan2(dx, -dy);                    // 正上方为 0，顺时针增加
        if (ang < 0) ang += MathF.PI * 2;
        return (int)MathF.Floor((ang + MathF.PI / n) / (MathF.PI * 2 / n)) % n;
    }

    void Redraw()
    {
        int size = (int)(Outer * 2 + 24 * _scale);
        if (_bmp == null || _bmp.Width != size) { _bmp?.Dispose(); _bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb); }
        Render(_bmp, _look, Sector(Cursor.Position), (float)_clock.Elapsed.TotalSeconds, _scale);
        Present(_bmp, _center.X - size / 2, _center.Y - size / 2);
    }

    /// <summary>把圆盘画到位图上（也供调试导出图片）。</summary>
    public static void Render(Bitmap bmp, Look look, int selected, float t, float scale)
    {
        using Graphics g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        float c = bmp.Width / 2f, outer = 168 * scale, inner = 48 * scale;
        int n = Anim.Wheel.Length;
        float step = 360f / n;
        float appear = Math.Min(1, t * 7);                    // 弹出的小动画
        outer *= 0.85f + 0.15f * appear;

        using var font = new Font("Microsoft YaHei UI", 13f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        for (int i = 0; i < n; i++)
        {
            bool sel = i == selected;
            float start = -90 - step / 2 + i * step + 1.6f, sweep = step - 3.2f;
            float ro = sel ? outer + 9 * scale : outer;
            using var path = new GraphicsPath();
            path.AddArc(c - ro, c - ro, ro * 2, ro * 2, start, sweep);
            path.AddArc(c - inner, c - inner, inner * 2, inner * 2, start + sweep, -sweep);
            path.CloseFigure();
            using var fill = new SolidBrush(sel ? Color.FromArgb(250, 0xFF, 0xF3, 0xC4) : Color.FromArgb(232, 0xFF, 0xFF, 0xFF));
            using var pen = new Pen(sel ? Color.FromArgb(255, 0xF2, 0xA6, 0x3A) : Color.FromArgb(200, 0x8F, 0xB8, 0x6A), (sel ? 3.2f : 2f) * scale);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);

            // 扇区里放一个正在做这个动作的小角色
            float mid = (-90 + i * step) * MathF.PI / 180f, rr = (inner + ro) / 2;
            float px = c + MathF.Cos(mid) * rr, py = c + MathF.Sin(mid) * rr;
            GraphicsState st = g.Save();
            g.TranslateTransform(px, py + 16 * scale);
            float k = (sel ? 0.66f : 0.56f) * scale;
            g.ScaleTransform(k, k);
            int anim = Anim.Wheel[i].Anim;
            SheepPainter.Draw(g, look, anim, sel ? t : 0.9f, false);
            g.Restore(st);

            using var tb = new SolidBrush(sel ? Color.FromArgb(0xB8, 0x62, 0x10) : Color.FromArgb(0x4E, 0x6B, 0x3A));
            g.DrawString(Anim.Wheel[i].Label, font, tb, px, py + 31 * scale, fmt);
        }

        using var hub = new SolidBrush(Color.FromArgb(235, 0x6F, 0xA8, 0x4F));
        g.FillEllipse(hub, c - inner + 5 * scale, c - inner + 5 * scale, (inner - 5 * scale) * 2, (inner - 5 * scale) * 2);
        using var small = new Font("Microsoft YaHei UI", 11.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString(selected < 0 ? "松开取消" : Anim.Wheel[selected].Label, small, Brushes.White, c, c, fmt);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _bmp?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>点击别人的角色后弹出的小菜单：攻击（撞过去）或拥抱。</summary>
internal sealed class InteractMenu : LayeredWindow
{
    static readonly string[] Labels = { "攻击", "站头顶" };
    readonly System.Windows.Forms.Timer _autoClose = new() { Interval = 6000 };
    Bitmap _bmp;
    float _scale = 1;
    int _hover = -1;
    string _targetId, _targetName;
    Point _origin;

    /// <summary>选了某一项：参数为目标角色 id 和类型（0 攻击，1 拥抱）。</summary>
    public event Action<string, int> Chosen;

    public InteractMenu() : base(clickThrough: false) => _autoClose.Tick += (_, _) => HideMenu();

    /// <summary>在屏幕坐标 anchor（角色头顶）上方显示。</summary>
    public void ShowFor(string targetId, string targetName, Point anchor)
    {
        _targetId = targetId; _targetName = targetName; _scale = DpiScale; _hover = -1;
        Size size = MenuSize(_scale);
        _origin = new Point(anchor.X - size.Width / 2, Math.Max(0, anchor.Y - size.Height));
        Redraw();
        if (!Visible) Show();
        _autoClose.Stop(); _autoClose.Start();
    }

    public void HideMenu()
    {
        _autoClose.Stop();
        if (Visible) Hide();
    }

    static Size MenuSize(float s) => new((int)(212 * s), (int)(112 * s));

    static RectangleF Button(int i, float s) => i == 0 ? new(10 * s, 34 * s, 84 * s, 54 * s) : new(100 * s, 34 * s, 102 * s, 54 * s);

    void Redraw()
    {
        Size size = MenuSize(_scale);
        if (_bmp == null || _bmp.Size != size) { _bmp?.Dispose(); _bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb); }
        Render(_bmp, _targetName, _hover, _scale);
        Present(_bmp, _origin.X, _origin.Y);
    }

    public static void Render(Bitmap bmp, string targetName, int hover, float s)
    {
        using Graphics g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        // 气泡底板 + 指向角色的小三角
        var panel = new RectangleF(2 * s, 2 * s, bmp.Width - 4 * s, bmp.Height - 16 * s);
        using (GraphicsPath path = SheepPainter.RoundRect(panel, 16 * s))
        {
            float mx = bmp.Width / 2f;
            path.AddPolygon(new[] { new PointF(mx - 9 * s, panel.Bottom - 1), new PointF(mx + 9 * s, panel.Bottom - 1), new PointF(mx, panel.Bottom + 11 * s) });
            using var fill = new SolidBrush(Color.FromArgb(244, 0xFF, 0xFB, 0xEE));
            using var pen = new Pen(Color.FromArgb(230, 0x8F, 0xB8, 0x6A), 2.2f * s);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var nameFont = new Font("Microsoft YaHei UI", 13f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var nameBrush = new SolidBrush(Color.FromArgb(0x4E, 0x6B, 0x3A));
        g.DrawString("对 " + targetName, nameFont, nameBrush, new RectangleF(8 * s, 6 * s, bmp.Width - 16 * s, 26 * s), fmt);

        using var btnFont = new Font("Microsoft YaHei UI", 14f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        for (int i = 0; i < 2; i++)
        {
            RectangleF r = Button(i, s);
            Color baseCol = i == 0 ? Color.FromArgb(0xF2, 0x6B, 0x5B) : Color.FromArgb(0x4F, 0xA6, 0xE8);
            if (i == hover) { r.Inflate(2 * s, 2 * s); baseCol = ControlPaint.Light(baseCol, 0.35f); }
            using GraphicsPath bp = SheepPainter.RoundRect(r, 12 * s);
            using var shadow = new SolidBrush(ControlPaint.Dark(baseCol, 0.12f));
            using var fill = new SolidBrush(baseCol);
            g.TranslateTransform(0, 3 * s); g.FillPath(shadow, bp); g.TranslateTransform(0, -3 * s);
            g.FillPath(fill, bp);

            // 小图标：攻击是爆炸星，站头顶是向上的箭头
            float ix = r.X + 16 * s, iy = r.Y + r.Height / 2;
            if (i == 0)
            {
                var pts = new PointF[16];
                for (int k = 0; k < 16; k++)
                {
                    float a = k * MathF.PI / 8, rr = ((k & 1) == 0 ? 10 : 5) * s;
                    pts[k] = new PointF(ix + MathF.Cos(a) * rr, iy + MathF.Sin(a) * rr);
                }
                g.FillPolygon(Brushes.White, pts);
            }
            else
                g.FillPolygon(Brushes.White, new[]
                {
                    new PointF(ix, iy - 10 * s), new PointF(ix + 9 * s, iy), new PointF(ix + 3.5f * s, iy),
                    new PointF(ix + 3.5f * s, iy + 9 * s), new PointF(ix - 3.5f * s, iy + 9 * s), new PointF(ix - 3.5f * s, iy), new PointF(ix - 9 * s, iy),
                });
            g.DrawString(Labels[i], btnFont, Brushes.White, new RectangleF(r.X + 24 * s, r.Y, r.Width - 25 * s, r.Height), fmt);
        }
    }

    int ButtonAt(Point p)
    {
        for (int i = 0; i < 2; i++) if (Button(i, _scale).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = ButtonAt(e.Location);
        if (h != _hover) { _hover = h; Redraw(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; if (Visible) Redraw(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        int b = e.Button == MouseButtons.Left ? ButtonAt(e.Location) : -1;
        HideMenu();
        if (b >= 0) Chosen?.Invoke(_targetId, b);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _autoClose.Dispose(); _bmp?.Dispose(); }
        base.Dispose(disposing);
    }
}
