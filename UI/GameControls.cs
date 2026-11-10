using System.ComponentModel;
using System.Drawing.Drawing2D;
using CloudWallpaper.Game;

namespace CloudWallpaper.UI;

/// <summary>游戏菜单的配色。</summary>
internal static class Ui
{
    public static readonly Color Paper = Color.FromArgb(0xFF, 0xF8, 0xE7);
    public static readonly Color Ink = Color.FromArgb(0x5A, 0x4A, 0x36);
    public static readonly Color Soft = Color.FromArgb(0x9A, 0x8A, 0x70);
    public static readonly Color Line = Color.FromArgb(0xE2, 0xD2, 0xAC);
    public static readonly Color Green = Color.FromArgb(0x6F, 0xB0, 0x4F);
    public static readonly Color Orange = Color.FromArgb(0xFF, 0x9F, 0x3A);
    public static readonly Color Red = Color.FromArgb(0xEE, 0x6A, 0x5A);
    public const string FontName = "Microsoft YaHei UI";

    public static Label Caption(string text, int x, int y) => new()
    {
        Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Soft, BackColor = Paper,
        Font = new Font(FontName, 9f, FontStyle.Bold),
    };

    public static Label Text(string text, int x, int y, int w, int h, float size = 9f, bool bold = false) => new()
    {
        Text = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = Ink, BackColor = Paper,
        Font = new Font(FontName, size, bold ? FontStyle.Bold : FontStyle.Regular),
    };

    public static TextBox Input(int x, int y, int w, int maxLength) => new()
    {
        Location = new Point(x, y), Width = w, MaxLength = maxLength, BorderStyle = BorderStyle.FixedSingle,
        Font = new Font(FontName, 11f), ForeColor = Ink, BackColor = Color.White,
    };
}

/// <summary>圆角的卡通按钮：普通状态是白色小卡片，选中或作为主按钮时是带底部厚度的彩色块。</summary>
internal sealed class GameButton : Control
{
    bool _hover, _down, _selected;

    /// <summary>选中 / 主按钮时的颜色。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent { get; set; } = Ui.Green;
    /// <summary>始终用 Accent 填充（用于「加入房间」这类主按钮）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Solid { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; Invalidate(); } } }

    public GameButton(string text, int x, int y, int w, int h, float fontSize = 9.5f)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Text = text; Bounds = new Rectangle(x, y, w, h);
        Font = new Font(Ui.FontName, fontSize, FontStyle.Bold);
        BackColor = Ui.Paper;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = DeviceDpi / 96f, depth = 3 * s;
        bool filled = (Solid || _selected) && Enabled;
        Color fill = !Enabled ? Color.FromArgb(0xEE, 0xE8, 0xDA) : filled ? Accent : _hover ? Color.FromArgb(0xFF, 0xF1, 0xCC) : Color.White;
        if (filled && _hover) fill = ControlPaint.Light(fill, 0.25f);
        Color edge = filled ? ControlPaint.Dark(Accent, 0.08f) : Ui.Line;
        Color text = !Enabled ? Color.FromArgb(0xB5, 0xAC, 0x98) : filled ? Color.White : Ui.Ink;

        float press = _down && Enabled ? depth * 0.7f : 0;
        var face = new RectangleF(1, 1 + press, Width - 2, Height - 2 - depth);
        using (GraphicsPath shadow = SheepPainter.RoundRect(new RectangleF(1, 1 + depth, Width - 2, Height - 2 - depth), 9 * s))
        using (var sb = new SolidBrush(edge))
            g.FillPath(sb, shadow);
        using (GraphicsPath path = SheepPainter.RoundRect(face, 9 * s))
        using (var fb = new SolidBrush(fill))
        using (var pen = new Pen(edge, 1.2f * s))
        {
            g.FillPath(fb, path);
            if (!filled) g.DrawPath(pen, path);
        }
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var tb = new SolidBrush(text);
        g.DrawString(Text, Font, tb, face, fmt);
    }
}

/// <summary>一排圆形色块，点一下选颜色。</summary>
internal sealed class SwatchRow : Control
{
    readonly int[] _colors;
    int _value, _hover = -1;

    public event Action<int> Picked;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value { get => _value; set { _value = value; Invalidate(); } }

    public SwatchRow(int[] colors, int x, int y, int w)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        _colors = colors;
        Bounds = new Rectangle(x, y, w, 28);
        BackColor = Ui.Paper;
        Cursor = Cursors.Hand;
    }

    float Step => Math.Min(Height + 1f, Width / (float)_colors.Length);

    int IndexAt(int x)
    {
        int i = (int)(x / Step);
        return i >= 0 && i < _colors.Length ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) { int h = IndexAt(e.X); if (h != _hover) { _hover = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int i = IndexAt(e.X);
        if (i < 0) return;
        _value = _colors[i];
        Invalidate();
        Picked?.Invoke(_value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f, step = Step, r = Height / 2f - 4 * s;
        for (int i = 0; i < _colors.Length; i++)
        {
            float cx = i * step + step / 2, cy = Height / 2f;
            Color c = Color.FromArgb(255, (_colors[i] >> 16) & 255, (_colors[i] >> 8) & 255, _colors[i] & 255);
            float rr = i == _hover ? r + 1.5f * s : r;
            using var b = new SolidBrush(c);
            using var p = new Pen(Color.FromArgb(0xC9, 0xB8, 0x90), 1.2f * s);
            g.FillEllipse(b, cx - rr, cy - rr, rr * 2, rr * 2);
            g.DrawEllipse(p, cx - rr, cy - rr, rr * 2, rr * 2);
            if (_colors[i] != _value) continue;
            using var ring = new Pen(Ui.Orange, 2.4f * s);
            g.DrawEllipse(ring, cx - r - 3 * s, cy - r - 3 * s, (r + 3 * s) * 2, (r + 3 * s) * 2);
        }
    }
}

/// <summary>房间成员列表：每行是头像、名字、当前状态，以及 HP 和饥饿两条状态条。</summary>
internal sealed class MemberList : Control
{
    public struct Row
    {
        public string Name, Status;
        public Look Look;
        public int Hp, Hunger;
        public bool Me;
    }

    const int RowH = 54;
    List<Row> _rows = new();

    public MemberList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Ui.Paper;
    }

    public void SetRows(List<Row> rows)
    {
        _rows = rows;
        int h = Math.Max(1, (int)(rows.Count * RowH * DeviceDpi / 96f));
        if (Height != h) Height = h;
        Invalidate();
    }

    static void Bar(Graphics g, float x, float y, float w, float h, int value, Color color, string label, Font font, float s)
    {
        using var lb = new SolidBrush(Ui.Soft);
        g.DrawString(label, font, lb, x, y - 3 * s);
        float bx = x + 30 * s, bw = w - 30 * s - 26 * s;
        using (GraphicsPath back = SheepPainter.RoundRect(new RectangleF(bx, y, bw, h), h / 2))
        using (var bb = new SolidBrush(Color.FromArgb(0xEC, 0xE2, 0xC8)))
            g.FillPath(bb, back);
        float fill = bw * Math.Clamp(value, 0, 100) / 100f;
        if (fill > h)
        {
            using GraphicsPath front = SheepPainter.RoundRect(new RectangleF(bx, y, fill, h), h / 2);
            using var fb = new SolidBrush(color);
            g.FillPath(fb, front);
        }
        g.DrawString(value.ToString(), font, lb, bx + bw + 3 * s, y - 3 * s);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = DeviceDpi / 96f, rowH = RowH * s;
        using var nameFont = new Font(Ui.FontName, 10f, FontStyle.Bold);
        using var small = new Font(Ui.FontName, 8f);
        using var ink = new SolidBrush(Ui.Ink);
        using var soft = new SolidBrush(Ui.Soft);
        using var line = new Pen(Ui.Line, 1);
        using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

        for (int i = 0; i < _rows.Count; i++)
        {
            Row r = _rows[i];
            float y = i * rowH;
            if (r.Look != null) SheepPainter.Portrait(g, r.Look, 24 * s, y + rowH / 2 - 1 * s, 20 * s);

            string name = r.Me ? r.Name + "（我）" : r.Name;
            SizeF size = g.MeasureString(name, nameFont);
            float nameW = Math.Min(size.Width, 150 * s);
            g.DrawString(name, nameFont, ink, new RectangleF(52 * s, y + 4 * s, nameW, 22 * s), fmt);
            g.DrawString(r.Status, small, soft, new RectangleF(54 * s + nameW, y + 8 * s, Width - 58 * s - nameW, 18 * s), fmt);

            float barW = (Width - 56 * s) / 2 - 4 * s;
            Bar(g, 54 * s, y + 31 * s, barW, 9 * s, r.Hp, Color.FromArgb(0xEE, 0x6A, 0x5A), "HP", small, s);
            Bar(g, 54 * s + barW + 8 * s, y + 31 * s, barW, 9 * s, r.Hunger, Color.FromArgb(0xFF, 0xA9, 0x3A), "饥饿", small, s);
            if (i > 0) g.DrawLine(line, 4 * s, y, Width - 4 * s, y);
        }
    }
}

/// <summary>
/// 菜单左侧的场景：天空中一座悬空岛，当前角色站在上面。点一下角色会随机做个动作。
/// </summary>
internal sealed class IslandView : Control
{
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly Random _rng = new();
    int _anim = Anim.Idle;
    float _animStart;

    /// <summary>当前显示的角色。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<CharacterDef> Character { get; set; }
    /// <summary>在空白处按下鼠标（用来拖动窗口）。</summary>
    public event Action DragStart;

    public IslandView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _timer.Tick += (_, _) => { if (Visible) Invalidate(); };
        _timer.Start();
    }

    public void Play(int anim) { _anim = anim; _animStart = (float)_clock.Elapsed.TotalSeconds; }

    float K => Width / 540f;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        float k = K, cx = 270 * k, cy = 372 * k;
        if (Math.Abs(e.X - cx) < 95 * k && e.Y > cy - 200 * k && e.Y < cy + 20 * k)
            Play(Anim.Wheel[_rng.Next(Anim.Wheel.Length)].Anim);
        else if (e.Button == MouseButtons.Left) DragStart?.Invoke();
    }

    static void Cloud(Graphics g, float x, float y, float s, int alpha)
    {
        using var b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        g.FillEllipse(b, x - 60 * s, y - 14 * s, 120 * s, 34 * s);
        g.FillEllipse(b, x - 42 * s, y - 34 * s, 50 * s, 46 * s);
        g.FillEllipse(b, x - 8 * s, y - 44 * s, 60 * s, 56 * s);
        g.FillEllipse(b, x + 26 * s, y - 26 * s, 40 * s, 38 * s);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        float t = (float)_clock.Elapsed.TotalSeconds;

        using (var sky = new LinearGradientBrush(new Point(0, 0), new Point(0, Height), Color.FromArgb(0x4F, 0xA9, 0xF5), Color.FromArgb(0xCF, 0xEE, 0xFF)))
            g.FillRectangle(sky, ClientRectangle);

        g.ScaleTransform(K, K);            // 以下按 540 × 580 的画布来画

        // 太阳
        for (int i = 4; i >= 0; i--)
        {
            using var glow = new SolidBrush(Color.FromArgb(26, 255, 244, 200));
            float r = 34 + i * 16;
            g.FillEllipse(glow, 452 - r, 82 - r, r * 2, r * 2);
        }
        using (var sun = new SolidBrush(Color.FromArgb(0xFF, 0xF6, 0xD0))) g.FillEllipse(sun, 452 - 30, 82 - 30, 60, 60);

        // 远处飘动的云
        Cloud(g, (t * 9 + 60) % 700 - 80, 120, 1.0f, 210);
        Cloud(g, (t * 5 + 380) % 700 - 80, 210, 0.7f, 170);
        Cloud(g, (t * 13 + 520) % 700 - 80, 470, 1.25f, 235);
        Cloud(g, (t * 7 + 200) % 700 - 80, 535, 0.9f, 200);

        // 远处的小岛
        float bob2 = MathF.Sin(t * 0.6f + 2) * 4;
        using (var rock2 = new SolidBrush(Color.FromArgb(0x9C, 0x8A, 0x86)))
            g.FillPolygon(rock2, new[] { new PointF(54, 300 + bob2), new PointF(126, 300 + bob2), new PointF(98, 344 + bob2), new PointF(78, 338 + bob2) });
        using (var grass2 = new SolidBrush(Color.FromArgb(0x8B, 0xCB, 0x66))) g.FillEllipse(grass2, 48, 290 + bob2, 84, 20);

        // ---- 主岛 ----
        float bob = MathF.Sin(t * 0.9f) * 6;
        g.TranslateTransform(270, 372 + bob);

        // 岩石底座：上宽下尖，分几层颜色
        var rock = new[]
        {
            new PointF(-176, 0), new PointF(176, 0), new PointF(150, 40), new PointF(112, 58), new PointF(84, 104),
            new PointF(40, 122), new PointF(14, 168), new PointF(-16, 132), new PointF(-58, 118), new PointF(-92, 70), new PointF(-140, 48),
        };
        using (var rb = new SolidBrush(Color.FromArgb(0x8D, 0x6E, 0x58))) g.FillPolygon(rb, rock);
        using (var dark = new SolidBrush(Color.FromArgb(0x74, 0x58, 0x46)))
        {
            g.FillPolygon(dark, new[] { new PointF(20, 0), new PointF(176, 0), new PointF(150, 40), new PointF(112, 58), new PointF(84, 104), new PointF(40, 122), new PointF(14, 168), new PointF(30, 96), new PointF(64, 52) });
        }
        using (var soil = new SolidBrush(Color.FromArgb(0xA8, 0x7E, 0x58))) g.FillEllipse(soil, -178, -22, 356, 54);
        // 飘在旁边的碎石
        using (var chip = new SolidBrush(Color.FromArgb(0x8D, 0x6E, 0x58)))
        {
            float c1 = MathF.Sin(t * 1.3f) * 5, c2 = MathF.Sin(t * 1.1f + 1) * 6;
            g.FillPolygon(chip, new[] { new PointF(-150, 96 + c1), new PointF(-128, 92 + c1), new PointF(-134, 116 + c1) });
            g.FillPolygon(chip, new[] { new PointF(118, 128 + c2), new PointF(142, 122 + c2), new PointF(134, 150 + c2), new PointF(122, 146 + c2) });
        }
        // 草皮
        using (var grassEdge = new SolidBrush(Color.FromArgb(0x5E, 0xA2, 0x42))) g.FillEllipse(grassEdge, -182, -30, 364, 58);
        using (var grass = new SolidBrush(Color.FromArgb(0x86, 0xCB, 0x5B))) g.FillEllipse(grass, -182, -36, 364, 56);
        using (var light = new SolidBrush(Color.FromArgb(0xA3, 0xDD, 0x72))) g.FillEllipse(light, -120, -30, 200, 30);

        // 小树
        using (var trunk = new SolidBrush(Color.FromArgb(0x8A, 0x5F, 0x3C))) g.FillRectangle(trunk, -128, -78, 12, 62);
        float sway = MathF.Sin(t * 1.4f) * 2.5f;
        using (var leafDark = new SolidBrush(Color.FromArgb(0x4E, 0x9A, 0x3E)))
        using (var leaf = new SolidBrush(Color.FromArgb(0x6C, 0xBE, 0x4F)))
        {
            g.FillEllipse(leafDark, -160 + sway, -128, 76, 66);
            g.FillEllipse(leaf, -150 + sway, -150, 60, 56);
            g.FillEllipse(leaf, -124 + sway, -122, 50, 46);
        }
        // 小花
        int[] fx = { 96, 122, 140, -62 };
        int[] fc = { 0xFF8FA3, 0xFFD24A, 0xFFFFFF, 0xFFFFFF };
        for (int i = 0; i < fx.Length; i++)
        {
            float fy = -10 + (i & 1) * 8;
            using var stem = new Pen(Color.FromArgb(0x4E, 0x9A, 0x3E), 2);
            g.DrawLine(stem, fx[i], fy, fx[i], fy + 9);
            using var petal = new SolidBrush(Color.FromArgb(255, (fc[i] >> 16) & 255, (fc[i] >> 8) & 255, fc[i] & 255));
            g.FillEllipse(petal, fx[i] - 5, fy - 6, 10, 10);
            using var core = new SolidBrush(Color.FromArgb(0xF2, 0xA6, 0x3A));
            g.FillEllipse(core, fx[i] - 2, fy - 3, 4, 4);
        }

        // 角色
        CharacterDef ch = Character?.Invoke();
        if (ch != null)
        {
            float at = t - _animStart, dur = Anim.Duration(_anim);
            if (_anim != Anim.Idle && ((dur > 0 && at > dur) || (dur == 0 && at > 5))) { _anim = Anim.Idle; _animStart = t; at = 0; }
            GraphicsState st = g.Save();
            g.TranslateTransform(8, -8);
            g.ScaleTransform(2.2f, 2.2f);
            SheepPainter.Draw(g, ch.Look, _anim, at, false);
            g.Restore(st);

            // 名牌
            using var font = new Font(Ui.FontName, 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            SizeF size = g.MeasureString(ch.Name, font);
            var plate = new RectangleF(8 - size.Width / 2 - 14, 42, size.Width + 28, 30);
            using GraphicsPath pp = SheepPainter.RoundRect(plate, 15);
            using var pb = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            using var tb = new SolidBrush(Ui.Ink);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.FillPath(pb, pp);
            g.DrawString(ch.Name, font, tb, plate, fmt);
        }
        g.ResetTransform();

        using var hintFont = new Font(Ui.FontName, 9f);
        using var hint = new SolidBrush(Color.FromArgb(190, 255, 255, 255));
        g.DrawString("点一下角色看看动作 · 拖动空白处移动窗口", hintFont, hint, 14 * K, Height - 26 * K);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
