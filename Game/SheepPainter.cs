using System.Drawing.Drawing2D;

namespace CloudWallpaper.Game;

/// <summary>
/// 用 GDI+ 画角色。壁纸里的精灵、捏角色界面、动作圆盘都用这一份代码，保证看到的样子一致。
/// 调用前把坐标原点移到角色脚下的地面中心；1 个单位约等于 1 像素（角色高约 75）。
/// </summary>
internal static class SheepPainter
{
    [ThreadStatic] static Font _textFont, _symbolFont;

    static Color C(int rgb, int a = 255) => Color.FromArgb(a, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
    static Color Shade(Color c, float k) => Color.FromArgb(c.A, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
    static Color Tint(Color c, float k) => Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * k), (int)(c.G + (255 - c.G) * k), (int)(c.B + (255 - c.B) * k));
    // 描边只比本色深一点、线也细：轮廓还在，但不是生硬的黑边
    static Color Outline(Color c) => Shade(c, c.GetBrightness() < 0.25f ? 0.66f : 0.80f);
    const float OutlineWidth = 1.2f;

    /// <summary>各种类的体型：身体宽高、腿长、头的横竖半径。</summary>
    static void Dims(int species, out float bw, out float bh, out float leg, out float hrx, out float hry)
    {
        switch (species)
        {
            case Look.Goat: bw = 54; bh = 32; leg = 25; hrx = 12; hry = 13.5f; break;
            case Look.Cow: bw = 68; bh = 42; leg = 21; hrx = 16; hry = 13.5f; break;
            case Look.Bull: bw = 74; bh = 46; leg = 21; hrx = 17; hry = 14.5f; break;
            default: bw = 58; bh = 40; leg = 20; hrx = 13.5f; hry = 12.5f; break;
        }
    }

    /// <summary>圆形头像：以头为中心裁出一个圆。</summary>
    public static void Portrait(Graphics g, Look L, float cx, float cy, float r)
    {
        Dims(L.S, out float bw, out float bh, out float leg, out _, out _);
        float hx = bw * 0.38f, hy = -(leg + bh * 0.5f - 5) - bh * 0.28f;   // 站立时头的中心
        float k = r / 23f;

        GraphicsState st = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
            using var bg = new SolidBrush(Color.FromArgb(0xBF, 0xE6, 0x9A));
            g.FillPath(bg, path);
            g.SetClip(path, CombineMode.Intersect);
        }
        g.TranslateTransform(cx - hx * k, cy - hy * k + 2 * k);
        g.ScaleTransform(k, k);
        Draw(g, L, Anim.Idle, 0.5f, false);
        g.Restore(st);

        using var ring = new Pen(Color.FromArgb(0x8F, 0xB8, 0x6A), Math.Max(1.5f, r * 0.09f));
        g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
    }

    static void Ellipse(Graphics g, Color fill, float cx, float cy, float rx, float ry, bool outline = true)
    {
        using var b = new SolidBrush(fill);
        g.FillEllipse(b, cx - rx, cy - ry, rx * 2, ry * 2);
        if (!outline) return;
        using var p = new Pen(Outline(fill), OutlineWidth);
        g.DrawEllipse(p, cx - rx, cy - ry, rx * 2, ry * 2);
    }

    static void Stroke(Graphics g, Color c, float width, params PointF[] pts)
    {
        using var p = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (pts.Length == 2) g.DrawLine(p, pts[0], pts[1]);
        else g.DrawCurve(p, pts, 0.5f);
    }

    static void Poly(Graphics g, Color fill, params PointF[] pts)
    {
        using var b = new SolidBrush(fill);
        using var p = new Pen(Outline(fill), OutlineWidth) { LineJoin = LineJoin.Round };
        g.FillPolygon(b, pts);
        g.DrawPolygon(p, pts);
    }

    static PointF P(float x, float y) => new(x, y);

    /// <param name="t">当前动作已经播放的秒数。</param>
    /// <param name="shadow">是否画脚下的影子（站在别人头上时不画）。</param>
    /// <param name="effectScale">
    /// 动作特效（气泡、爱心、音符、Zzz、星星……）相对角色本体的放大倍数。
    /// 桌面上的角色可以调得很小，但特效要保持看得清的大小，所以单独缩放。
    /// </param>
    public static void Draw(Graphics g, Look L, int anim, float t, bool faceLeft, bool shadow = true, float effectScale = 1f)
    {
        GraphicsState outer = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // ---- 体型 ----
        Dims(L.S, out float bw, out float bh, out float leg, out float hrx, out float hry);
        Color body = C(L.B), face = C(L.F), horn = C(L.H), spot = C(L.P), acc = C(L.AC);
        Color legCol = L.S == Look.Sheep ? face : Shade(body, 0.88f);

        // ---- 姿势 ----
        var la = new float[4];                 // 腿的摆角：0 后远 1 前远 2 后近 3 前近
        float dx = 0, dy = 0, rot = 0, lift = 0, hdx = 0, hdy = 0, hrot = 0;
        bool closed = false, happy = false, lying = false, folded = false, brow = false, dizzy = false;
        const float PI = MathF.PI;

        switch (anim)
        {
            case Anim.Walk:
            {
                float w = t * 11f;
                la[0] = la[3] = MathF.Sin(w) * 24; la[1] = la[2] = MathF.Sin(w + PI) * 24;
                dy = -MathF.Abs(MathF.Sin(w)) * 1.6f; hdy = MathF.Sin(w * 2) * 0.8f;
                break;
            }
            case Anim.Run:
            {
                float w = t * 19f;
                la[0] = la[3] = MathF.Sin(w) * 36; la[1] = la[2] = MathF.Sin(w + PI) * 36;
                dy = -MathF.Abs(MathF.Sin(w)) * 3f; rot = 5; hrot = 16; hdy = 4;
                break;
            }
            case Anim.Wave:
                la[3] = -105 + MathF.Sin(t * 13) * 22; rot = -5; happy = true; hrot = MathF.Sin(t * 6) * 5;
                break;
            case Anim.Dance:
            {
                float w = t * 7f;
                lift = MathF.Abs(MathF.Sin(w)) * 9; rot = MathF.Sin(w * 0.5f) * 9; hrot = MathF.Sin(w * 0.5f + 1) * 10;
                la[0] = la[3] = MathF.Sin(w) * 18; la[1] = la[2] = -MathF.Sin(w) * 18; happy = true;
                break;
            }
            case Anim.Sleep:
                lying = true; closed = true; dy = MathF.Sin(t * 1.4f) * 0.8f; hrot = 14; hdy = bh * 0.30f; hdx = 3;
                break;
            case Anim.Eat:
                hdy = leg + bh * 0.42f + MathF.Sin(t * 9) * 1.3f; hdx = 7; hrot = 8; rot = 3;
                break;
            case Anim.Jump:
            {
                float u = t % 0.75f / 0.75f;
                lift = 4 * u * (1 - u) * 30; la[0] = la[2] = 30; la[1] = la[3] = -30; happy = true;
                break;
            }
            case Anim.Sit:
                rot = -15; dy = 7; folded = true; la[1] = la[3] = 14; closed = t % 4f > 3.7f;
                break;
            case Anim.Love:
                lift = MathF.Abs(MathF.Sin(t * 5)) * 3; happy = true; hrot = MathF.Sin(t * 3) * 6;
                break;
            case Anim.Angry:
                dx = MathF.Sin(t * 45) * 1.0f; la[3] = MathF.Sin(t * 10) > 0 ? -38 : 4; brow = true; hrot = 8;
                break;
            case Anim.Ram:
            {
                float u = Math.Clamp(t / 0.5f, 0, 1);
                dx = MathF.Sin(u * PI) * 16; hrot = 32; hdy = 7; rot = 6; brow = true;
                la[0] = la[2] = 26; la[1] = la[3] = -26;
                break;
            }
            case Anim.Hit:
                if (t < 0.7f)
                {
                    float u = t / 0.7f;
                    rot = -u * 360; lift = 4 * u * (1 - u) * 26 + 6; dizzy = true;
                    la[0] = la[2] = 35; la[1] = la[3] = -35;
                }
                else { lying = true; dizzy = true; hrot = 10; hdy = bh * 0.25f; dx = MathF.Sin(t * 30) * 0.5f; }
                break;
            case Anim.Hug:
                rot = 7; hdx = 3; happy = true; lift = MathF.Abs(MathF.Sin(t * 4)) * 2; la[3] = -40; la[1] = -25;
                break;
            default:   // Idle
                dy = MathF.Sin(t * 2.2f) * 0.9f; hdy = MathF.Sin(t * 2.2f + 0.6f) * 0.6f; closed = t % 3.6f < 0.13f;
                break;
        }

        float bcx = dx;
        float bcy = -(lying ? bh * 0.5f - 1 : leg + bh * 0.5f - 5) + dy - lift;
        float hx = bw * 0.38f, hy = -bh * 0.28f;

        // ---- 地面的影子 ----
        float sw = bw * (0.50f - lift * 0.004f);
        if (shadow)
        {
            using var sb = new SolidBrush(Color.FromArgb(50, 20, 40, 20));
            g.FillEllipse(sb, -sw, -4.5f, sw * 2, 9);
        }

        // ---- 身体（面朝右画，朝左时整体镜像）----
        GraphicsState bodyState = g.Save();
        if (faceLeft) g.ScaleTransform(-1, 1);
        g.TranslateTransform(bcx, bcy);
        g.RotateTransform(rot);

        // 尾巴
        float wag = MathF.Sin(t * 3.2f) * 8;
        if (L.S == Look.Sheep) Ellipse(g, body, -bw * 0.5f - 1, -3, 6.5f, 6.5f);
        else if (L.S == Look.Goat) Poly(g, body, P(-bw * 0.5f + 3, -6), P(-bw * 0.5f - 7, -13 + wag * 0.3f), P(-bw * 0.5f + 1, 0));
        else
        {
            var tip = P(-bw * 0.5f - 7 + wag * 0.5f, 14);
            Stroke(g, Outline(body), 3.6f, P(-bw * 0.5f + 2, -8), P(-bw * 0.5f - 6, 0), tip);
            Stroke(g, body, 2f, P(-bw * 0.5f + 2, -8), P(-bw * 0.5f - 6, 0), tip);
            Ellipse(g, L.S == Look.Cow ? spot : Shade(body, 0.6f), tip.X, tip.Y + 2, 3, 4.2f);
        }

        // 腿（躺下时收起来）。抬得很高的前腿（打招呼、拥抱）要画在身体前面，放到躯干之后再画
        bool raised = !lying && la[3] < -30;
        void Legs(int from, int to)
        {
            float hipY = bh * 0.5f - 7, len = leg + 7;
            for (int i = from; i < to; i++)
            {
                bool front = (i & 1) == 1, near = i >= 2;
                float x = (front ? 1 : -1) * bw * 0.27f + (near ? 0 : 4);
                float a = la[i] * PI / 180f, l = len;
                if (folded && !front) { a = 78 * PI / 180f; l = len * 0.8f; }
                Color c = near ? legCol : Shade(legCol, 0.80f);
                var hip = P(x, hipY);
                var foot = P(x + MathF.Sin(a) * l, hipY + MathF.Cos(a) * l);
                Stroke(g, Outline(c), 8.6f, hip, foot);
                Stroke(g, c, 6f, hip, foot);
                var hoof = P(foot.X - MathF.Sin(a) * 3.2f, foot.Y - MathF.Cos(a) * 3.2f);
                Stroke(g, L.S == Look.Sheep ? Shade(c, 0.6f) : C(0x4A3F3A), 6f, hoof, foot);
            }
        }
        if (!lying) Legs(0, raised ? 3 : 4);

        // 躯干
        float rx = bw * 0.5f, ry = bh * 0.5f;
        if (L.S == Look.Sheep)
        {
            // 一圈毛球：先画一遍深色的描边，再画一遍填充，得到连在一起的蓬松轮廓
            const int n = 12;
            Color ol = Outline(body);
            for (int pass = 0; pass < 2; pass++)
            {
                using var b = new SolidBrush(pass == 0 ? ol : body);
                float grow = pass == 0 ? OutlineWidth : 0;
                for (int i = 0; i < n; i++)
                {
                    float a = i * (PI * 2 / n) + 0.2f, r = bh * 0.22f + i % 3 * 1.3f + grow;
                    float px = MathF.Cos(a) * (rx - 5), py = MathF.Sin(a) * (ry - 5);
                    g.FillEllipse(b, px - r, py - r, r * 2, r * 2);
                }
                g.FillEllipse(b, -rx + 4 - grow, -ry + 4 - grow, (rx - 4 + grow) * 2, (ry - 4 + grow) * 2);
            }
            Color curl = Shade(body, 0.90f);
            Stroke(g, curl, 1.5f, P(-12, -4), P(-8, -8), P(-3, -5));
            Stroke(g, curl, 1.5f, P(2, 6), P(7, 2), P(12, 5));
            Stroke(g, curl, 1.5f, P(-16, 8), P(-11, 5), P(-7, 8));
        }
        else
        {
            using var path = new GraphicsPath();
            path.AddEllipse(-rx, -ry, bw, bh);
            using (var b = new SolidBrush(body)) g.FillPath(b, path);
            if (L.S != Look.Goat)
            {
                // 斑块：裁剪在身体轮廓内
                Region old = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                using var sbr = new SolidBrush(spot);
                g.FillEllipse(sbr, -rx * 0.75f, -ry * 1.1f, rx * 0.7f, ry * 1.1f);
                g.FillEllipse(sbr, -rx * 0.1f, ry * 0.05f, rx * 0.62f, ry * 1.2f);
                if (L.S == Look.Cow) g.FillEllipse(sbr, rx * 0.35f, -ry * 0.9f, rx * 0.5f, ry * 0.75f);
                g.Clip = old;
                old.Dispose();
            }
            else
            {
                using var belly = new SolidBrush(Tint(body, 0.35f));
                Region old = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                g.FillEllipse(belly, -rx * 0.7f, ry * 0.35f, bw * 0.7f, bh * 0.6f);
                g.Clip = old;
                old.Dispose();
            }
            using var op = new Pen(Outline(body), 1.5f);
            g.DrawEllipse(op, -rx, -ry, bw, bh);
        }

        if (raised) Legs(3, 4);

        // ---- 头（正面朝向观众）----
        g.TranslateTransform(hx + hdx, hy + hdy);
        g.RotateTransform(hrot);

        // 角
        for (int s = -1; s <= 1; s += 2)
        {
            float ax = s * hrx * 0.55f, ay = -hry * 0.72f;
            if (L.HS == 1) Poly(g, horn, P(ax - 3.2f, ay + 2), P(ax + 3.2f, ay + 2), P(ax + s * 2.5f, ay - 8));
            else if (L.HS == 2)
            {
                var pts = new PointF[12];
                float cx = s * (hrx + 1.5f), cy = -hry * 0.30f;
                for (int i = 0; i < pts.Length; i++)
                {
                    float th = i / (float)(pts.Length - 1) * 1.7f * PI, r = 9.5f - th * 1.25f;
                    pts[i] = P(cx + s * MathF.Cos(th + PI * 0.75f) * r, cy + MathF.Sin(th + PI * 0.75f) * r - 1);
                }
                Stroke(g, Outline(horn), 7.4f, pts);
                Stroke(g, horn, 5f, pts);
            }
            else if (L.HS == 3)
            {
                var pts = new[] { P(ax, ay + 1), P(ax + s * 7, ay - 10), P(ax + s * 15, ay - 15), P(ax + s * 19, ay - 25) };
                Stroke(g, Outline(horn), 7f, pts);
                Stroke(g, horn, 4.6f, pts);
            }
        }

        // 耳朵
        bool bovine = L.S >= Look.Cow;
        for (int s = -1; s <= 1; s += 2)
        {
            GraphicsState es = g.Save();
            g.TranslateTransform(s * (hrx + (bovine ? 4 : 2.5f)), bovine ? -3 : -1.5f);
            g.RotateTransform(s * (L.S == Look.Goat ? 32 : bovine ? 8 : 18) + MathF.Sin(t * 2.5f + s) * 3);
            Ellipse(g, face, 0, 0, bovine ? 8.5f : 6.5f, bovine ? 4.6f : 3.6f);
            Ellipse(g, C(0xF4B6B0), s * 0.5f, 0.3f, bovine ? 5 : 3.6f, bovine ? 2.4f : 1.7f, false);
            g.Restore(es);
        }

        // 脸
        Ellipse(g, face, 0, 0, hrx, hry);
        if (L.S == Look.Cow && L.P != L.F)
        {
            using var patch = new SolidBrush(spot);
            using var fp = new GraphicsPath();
            fp.AddEllipse(-hrx, -hry, hrx * 2, hry * 2);
            Region old = g.Clip;
            g.SetClip(fp, CombineMode.Intersect);
            g.FillEllipse(patch, hrx * 0.05f, -hry * 1.1f, hrx * 1.1f, hry * 1.25f);
            g.Clip = old;
            old.Dispose();
        }

        // 绵羊头顶的毛
        if (L.S == Look.Sheep)
        {
            Ellipse(g, body, -7, -hry + 1.5f, 6, 5.5f);
            Ellipse(g, body, 7, -hry + 1.5f, 6, 5.5f);
            Ellipse(g, body, 0, -hry - 1.5f, 7, 6.2f);
            using var cover = new SolidBrush(body);
            g.FillEllipse(cover, -9, -hry + 0.5f, 18, 7);
        }

        // 口鼻
        bool darkFace = face.GetBrightness() < 0.45f;
        Color ink = darkFace ? C(0xFFF6EC) : C(0x3A2F2B);
        if (bovine)
        {
            Color muzzle = L.S == Look.Cow ? C(0xF6BDB4) : C(0xEBCFAE);
            Ellipse(g, muzzle, 0, hry * 0.48f, hrx * 0.70f, hry * 0.46f);
            Ellipse(g, Shade(muzzle, 0.62f), -hrx * 0.26f, hry * 0.42f, 1.5f, 2f, false);
            Ellipse(g, Shade(muzzle, 0.62f), hrx * 0.26f, hry * 0.42f, 1.5f, 2f, false);
            if (L.S == Look.Bull)
            {
                using var ring = new Pen(C(0xF2C230), 2f);
                g.DrawArc(ring, -4.5f, hry * 0.52f, 9, 9, 15, 150);
            }
        }
        else
        {
            Poly(g, darkFace ? C(0xF2A9A0) : C(0x6A4C44), P(-2.6f, 3.4f), P(2.6f, 3.4f), P(0, 6.2f));
            Stroke(g, ink, 1.2f, P(-3.4f, 8.4f), P(0, 7.0f), P(3.4f, 8.4f));
            if (L.S == Look.Goat) Poly(g, Tint(face, 0.3f), P(-3.2f, hry - 1.5f), P(3.2f, hry - 1.5f), P(0, hry + 8));
        }

        // 眼睛
        float ex = hrx * 0.46f, ey = bovine ? -3.2f : -1.6f;
        for (int s = -1; s <= 1; s += 2)
        {
            float x = s * ex;
            if (dizzy)
            {
                Stroke(g, ink, 1.5f, P(x - 2.4f, ey - 2.4f), P(x + 2.4f, ey + 2.4f));
                Stroke(g, ink, 1.5f, P(x - 2.4f, ey + 2.4f), P(x + 2.4f, ey - 2.4f));
            }
            else if (happy) Stroke(g, ink, 1.6f, P(x - 2.8f, ey + 1.2f), P(x, ey - 1.6f), P(x + 2.8f, ey + 1.2f));
            else if (closed) Stroke(g, ink, 1.6f, P(x - 2.8f, ey - 0.4f), P(x, ey + 1.4f), P(x + 2.8f, ey - 0.4f));
            else
            {
                Color eye = (L.S == Look.Cow && s > 0 && L.P != L.F ? spot : face).GetBrightness() < 0.45f ? C(0xFFFFFF) : C(0x2B2422);
                Ellipse(g, eye, x, ey, 2.5f, 2.9f, false);
                Ellipse(g, eye.GetBrightness() > 0.5f ? C(0x2B2422) : C(0xFFFFFF), x + 0.8f, ey - 0.9f, 0.9f, 0.9f, false);
            }
            if (brow) Stroke(g, ink, 1.7f, P(x - s * 3.6f, ey - 6.2f), P(x + s * 2.6f, ey - 3.6f));
            Ellipse(g, C(0xFF9FA8, 105), s * hrx * 0.70f, ey + 5.2f, 3.1f, 2.0f, false);
        }

        // 饰品
        switch (L.A)
        {
            case 1:   // 铃铛
                Stroke(g, acc, 3f, P(-hrx * 0.72f, hry - 3), P(0, hry + 0.5f), P(hrx * 0.72f, hry - 3));
                Ellipse(g, C(0xF7C93B), 0, hry + 4.5f, 4.2f, 4.2f);
                Stroke(g, C(0x8A6A12), 1.1f, P(-2.2f, hry + 5.2f), P(2.2f, hry + 5.2f));
                break;
            case 2:   // 蝴蝶结
            {
                float bx = hrx * 0.62f, by = -hry * 0.92f;
                Poly(g, acc, P(bx, by), P(bx - 8, by - 5.5f), P(bx - 8, by + 5.5f));
                Poly(g, acc, P(bx, by), P(bx + 8, by - 5.5f), P(bx + 8, by + 5.5f));
                Ellipse(g, Shade(acc, 0.85f), bx, by, 2.6f, 2.6f);
                break;
            }
            case 3:   // 围巾
                Poly(g, acc, P(-hrx * 0.55f, hry - 1), P(-hrx * 0.15f, hry - 1), P(-hrx * 0.25f, hry + 12), P(-hrx * 0.70f, hry + 11));
                Stroke(g, Outline(acc), 7.5f, P(-hrx * 0.78f, hry - 4), P(0, hry + 0.5f), P(hrx * 0.78f, hry - 4));
                Stroke(g, acc, 5.2f, P(-hrx * 0.78f, hry - 4), P(0, hry + 0.5f), P(hrx * 0.78f, hry - 4));
                break;
            case 4:   // 小花
            {
                float fx = -hrx * 0.60f, fy = -hry * 0.95f;
                for (int i = 0; i < 5; i++)
                {
                    float a = i * PI * 2 / 5 - PI / 2;
                    Ellipse(g, acc, fx + MathF.Cos(a) * 3.6f, fy + MathF.Sin(a) * 3.6f, 3, 3);
                }
                Ellipse(g, C(0xFFD84A), fx, fy, 2.4f, 2.4f);
                break;
            }
            case 5:   // 草帽
            {
                float ty = -hry * 0.86f;
                Ellipse(g, C(0xE9C46A), 0, ty, hrx * 1.12f, 3.8f);
                using (var dome = new GraphicsPath())
                {
                    dome.AddArc(-hrx * 0.62f, ty - 10.5f, hrx * 1.24f, 21, 180, 180);
                    dome.CloseFigure();
                    using var db = new SolidBrush(C(0xE9C46A));
                    using var dp = new Pen(Outline(C(0xE9C46A)), 1.5f);
                    g.FillPath(db, dome);
                    g.DrawPath(dp, dome);
                }
                Stroke(g, acc, 3f, P(-hrx * 0.58f, ty - 1.8f), P(hrx * 0.58f, ty - 1.8f));
                break;
            }
        }

        g.Restore(bodyState);

        // ---- 动作特效（不随角色镜像，文字才不会反）----
        float dir = faceLeft ? -1 : 1;
        float fxX = dir * (bcx + hx), fxY = bcy + hy - hry - 10;
        if (effectScale != 1f && anim != Anim.Eat)   // 吃草的草屑贴着地面，跟着角色大小走
        {
            // 以头顶为中心放大特效
            g.TranslateTransform(fxX, fxY);
            g.ScaleTransform(effectScale, effectScale);
            g.TranslateTransform(-fxX, -fxY);
        }
        DrawEffects(g, anim, t, fxX, fxY, dir);

        g.Restore(outer);
    }

    static void DrawEffects(Graphics g, int anim, float t, float x, float y, float dir)
    {
        _textFont ??= new Font("Microsoft YaHei UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        _symbolFont ??= new Font("Segoe UI Symbol", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        switch (anim)
        {
            case Anim.Wave:
            {
                float pop = Math.Min(1, t * 6);
                var r = new RectangleF(x + dir * 20 - 15, y - 13 - pop * 3, 30, 17);
                using var path = RoundRect(r, 7);
                using var b = new SolidBrush(Color.FromArgb((int)(240 * pop), 255, 255, 255));
                using var p = new Pen(Color.FromArgb((int)(200 * pop), 120, 140, 170), 1.2f);
                g.FillPath(b, path);
                g.DrawPath(p, path);
                using var tb = new SolidBrush(Color.FromArgb((int)(255 * pop), 70, 80, 100));
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("嗨~", _textFont, tb, r, fmt);
                break;
            }
            case Anim.Dance:
                for (int i = 0; i < 3; i++)
                {
                    float ph = (t * 0.55f + i / 3f) % 1f;
                    using var b = new SolidBrush(Color.FromArgb((int)(255 * (1 - ph)), i == 1 ? 0xFF : 0x6C, i == 1 ? 0x8F : 0xA8, i == 1 ? 0xA3 : 0xF0));
                    g.DrawString(i == 1 ? "♫" : "♪", _symbolFont, b, x + (i - 1) * 15 + MathF.Sin(ph * 6 + i) * 5 - 6, y - 8 - ph * 30);
                }
                break;
            case Anim.Sleep:
                for (int i = 0; i < 3; i++)
                {
                    float ph = (t * 0.35f + i / 3f) % 1f;
                    using var b = new SolidBrush(Color.FromArgb((int)(230 * (1 - ph)), 90, 130, 220));
                    using var f = new Font("Segoe UI", 7f + ph * 8f, FontStyle.Bold, GraphicsUnit.Pixel);
                    g.DrawString("Z", f, b, x + dir * (6 + ph * 20) - 4, y + 4 - ph * 34);
                }
                break;
            case Anim.Love:
            case Anim.Hug:
                for (int i = 0; i < 4; i++)
                {
                    float ph = (t * 0.6f + i / 4f) % 1f;
                    Heart(g, x + (i - 1.5f) * 10 + MathF.Sin(ph * 5 + i) * 4, y - 2 - ph * 34, 4.5f + (i & 1) * 1.5f,
                        Color.FromArgb((int)(255 * (1 - ph)), 0xFF, 0x5C, 0x7A));
                }
                break;
            case Anim.Angry:
            {
                float s = 1 + MathF.Sin(t * 12) * 0.12f;
                float cx = x + dir * 13, cy = y - 2;
                using var p = new Pen(Color.FromArgb(235, 0xE5, 0x3B, 0x3B), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                for (int q = 0; q < 4; q++)
                {
                    float sx = (q & 1) == 0 ? -1 : 1, sy = q < 2 ? -1 : 1;
                    g.DrawBezier(p, cx + sx * 1.5f * s, cy + sy * 5.5f * s, cx + sx * 1.8f * s, cy + sy * 1.8f * s,
                        cx + sx * 1.8f * s, cy + sy * 1.8f * s, cx + sx * 5.5f * s, cy + sy * 1.5f * s);
                }
                for (int i = 0; i < 2; i++)
                {
                    float ph = (t * 1.3f + i * 0.5f) % 1f;
                    using var b = new SolidBrush(Color.FromArgb((int)(170 * (1 - ph)), 255, 255, 255));
                    float r = 2.5f + ph * 4;
                    g.FillEllipse(b, x - dir * (14 + ph * 12) - r, y + 14 - ph * 8 - r, r * 2, r * 2);
                }
                break;
            }
            case Anim.Hit:
                if (t > 0.45f)
                    for (int i = 0; i < 3; i++)
                    {
                        float a = t * 5 + i * MathF.PI * 2 / 3;
                        Star(g, x - dir * 4 + MathF.Cos(a) * 15, y + 14 + MathF.Sin(a) * 5, 4.2f, Color.FromArgb(255, 0xFF, 0xD2, 0x3A));
                    }
                break;
            case Anim.Eat:
                for (int i = 0; i < 2; i++)
                {
                    float ph = (t * 1.6f + i * 0.5f) % 1f;
                    using var p = new Pen(Color.FromArgb((int)(220 * (1 - ph)), 0x5F, 0xA8, 0x3C), 1.6f);
                    float gx = x + dir * (10 + i * 5), gy = -3 - ph * 9;
                    g.DrawLine(p, gx, gy, gx + dir * 2, gy - 4);
                }
                break;
        }
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Heart(Graphics g, float cx, float cy, float s, Color c)
    {
        using var path = new GraphicsPath();
        path.AddBezier(cx, cy + s, cx - s * 1.6f, cy - s * 0.2f, cx - s * 0.8f, cy - s * 1.3f, cx, cy - s * 0.4f);
        path.AddBezier(cx, cy - s * 0.4f, cx + s * 0.8f, cy - s * 1.3f, cx + s * 1.6f, cy - s * 0.2f, cx, cy + s);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    static void Star(Graphics g, float cx, float cy, float r, Color c)
    {
        var pts = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            float a = i * MathF.PI / 5 - MathF.PI / 2, rr = (i & 1) == 0 ? r : r * 0.45f;
            pts[i] = new PointF(cx + MathF.Cos(a) * rr, cy + MathF.Sin(a) * rr);
        }
        using var b = new SolidBrush(c);
        using var p = new Pen(Color.FromArgb(c.A, 0xC9, 0x8A, 0x10), 1f);
        g.FillPolygon(b, pts);
        g.DrawPolygon(p, pts);
    }
}
