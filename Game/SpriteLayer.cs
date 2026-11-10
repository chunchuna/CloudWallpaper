using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CloudWallpaper.Game;

/// <summary>
/// 把房间里的角色画到壁纸上。每个角色在图集里占一个固定的格子：上半是角色本体，下面一条是名字。
/// 角色用 GDI+ 画进格子、上传成纹理，再用带轻微模糊的着色器贴到屏幕上——边缘是柔和的，不像矢量图那样生硬；
/// 名字不模糊。位置每帧都更新（跟显示器刷新率走），角色本体的重画频率按耗时自动调节。只在渲染线程使用。
/// </summary>
internal sealed class SpriteLayer
{
    const int Cols = 4, Rows = 4;
    /// <summary>默认大小（设置里的「动物大小」在此基础上再乘倍数）。</summary>
    public const float CharScale = 0.625f;
    /// <summary>叠罗汉每一层抬高多少（角色设计单位）。</summary>
    public const float StackStep = 57f;

    const string VertexSrc = @"#version 120
varying vec2 vUv;
void main() { vUv = gl_MultiTexCoord0.xy; gl_Position = gl_Vertex; }
";
    // 和云一样的做法：内部保持清晰，只把轮廓的透明度做成柔和的过渡。
    // 取周围一圈的平均不透明度：角色内部是 1，越靠近轮廓越小，据此把边缘那一圈像素渐渐淡出。
    // uStep 为 0 时原样输出（画名字、铺背景用）。
    const string FragmentSrc = @"#version 120
uniform sampler2D uTex;
uniform vec2 uStep;
varying vec2 vUv;
void main() {
    vec4 c = texture2D(uTex, vUv);
    if (uStep.x > 0.0 && c.a > 0.0) {
        float around = c.a * 0.20
            + (texture2D(uTex, vUv + vec2(uStep.x, 0.0)).a + texture2D(uTex, vUv - vec2(uStep.x, 0.0)).a
             + texture2D(uTex, vUv + vec2(0.0, uStep.y)).a + texture2D(uTex, vUv - vec2(0.0, uStep.y)).a) * 0.12
            + (texture2D(uTex, vUv + uStep).a + texture2D(uTex, vUv - uStep).a
             + texture2D(uTex, vUv + vec2(uStep.x, -uStep.y)).a + texture2D(uTex, vUv + vec2(-uStep.x, uStep.y)).a) * 0.08;
        // 和自己的不透明度比：半透明的影子、渐隐的爱心内部比值也是 1，不会被误伤，只有轮廓附近才小于 1
        c *= smoothstep(0.35, 0.95, around / c.a);              // 颜色是预乘透明度的，整体一起乘
    }
    gl_FragColor = c;
}
";

    struct Sprite
    {
        public string Id, Name;
        public Look Look; public int Anim, Level, Cell; public float AnimT; public bool FaceLeft, IsLocal;
        public float X, Y;
    }

    readonly List<Sprite> _sprites = new();
    readonly Dictionary<string, int> _cells = new();      // 角色 id → 图集格子，保持不变，这样可以隔几帧才重画
    readonly string[] _cellName = new string[Cols * Rows];
    readonly float[] _nameW = new float[Cols * Rows];
    readonly Stopwatch _clock = Stopwatch.StartNew();
    Bitmap _atlas;
    Graphics _g;
    Font _nameFont;
    uint _tex, _prog;
    int _uStep;
    int _cellW, _cellH, _anchor, _animH, _nameH;         // _anchor：脚底在格子里的高度
    float _scale, _animal, _p, _fx;
    double _lastRaster = -1, _rasterInterval;
    bool _layoutDirty;
    int _w, _h;

    public static float StepPx(float scale, float animal) => StackStep * CharScale * scale * animal;

    void EnsureProgram()
    {
        if (_prog != 0) return;
        _prog = GL.BuildProgram(VertexSrc, FragmentSrc);
        _uStep = GL.GetUniformLocation(_prog, "uStep");
    }

    /// <summary>
    /// 把一张和屏幕一样大的纹理原样铺满屏幕（离屏渲染好的背景或云层）。
    /// blend 为 true 时按纹理的透明度叠加。调用后绑定的纹理和着色器会被改掉。
    /// </summary>
    public void Blit(uint texture, bool blend)
    {
        EnsureProgram();
        GL.UseProgram(_prog);
        GL.Uniform2f(_uStep, 0, 0);
        GL.BindTexture(GL.TEXTURE_2D, texture);
        if (blend) { GL.Enable(GL.BLEND); GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA); }
        GL.Begin(GL.QUADS);
        GL.TexCoord2f(0, 0); GL.Vertex2f(-1, -1);
        GL.TexCoord2f(1, 0); GL.Vertex2f(1, -1);
        GL.TexCoord2f(1, 1); GL.Vertex2f(1, 1);
        GL.TexCoord2f(0, 1); GL.Vertex2f(-1, 1);
        GL.End();
        if (blend) GL.Disable(GL.BLEND);
    }

    void Ensure(float scale, float animal)
    {
        EnsureProgram();
        if (_atlas != null && scale == _scale && animal == _animal) return;
        _scale = scale; _animal = animal;
        _g?.Dispose(); _atlas?.Dispose(); _nameFont?.Dispose();
        _p = CharScale * scale * animal;                              // 角色设计单位 → 像素
        _fx = Math.Max(scale, _p);                                    // 特效的像素倍数：不随角色缩小，保证看得清
        _cellW = (int)Math.Max(Math.Max(170 * _p, 2 * (24 * _p + 42 * _fx)), 160 * scale);   // 放得下身体、特效和名字
        _anchor = (int)(114 * _p + 52 * _fx) + 2;                     // 头顶以上留出特效的高度
        _animH = _anchor + (int)(7 * _p) + 2;
        _nameH = (int)(21 * scale);
        _cellH = _animH + _nameH;
        _atlas = new Bitmap(_cellW * Cols, _cellH * Rows, PixelFormat.Format32bppPArgb);
        _g = Graphics.FromImage(_atlas);
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        _nameFont = new Font("Microsoft YaHei UI", 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _layoutDirty = true;

        if (_tex != 0) GL.DeleteTextures(1, ref _tex);
        GL.GenTextures(1, out _tex);
        GL.BindTexture(GL.TEXTURE_2D, _tex);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_EDGE);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_EDGE);
        GL.TexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA8, _atlas.Width, _atlas.Height, 0, GL.BGRA, GL.UNSIGNED_BYTE, IntPtr.Zero);
    }

    /// <summary>
    /// 每帧先调用一次：取出角色的当前状态，必要时重画图集。返回是否有角色要画。
    /// 之后用 <see cref="Draw"/> 分两次贴到屏幕上（云的下面一次，云的上面一次）。
    /// </summary>
    public bool Prepare(World world, int w, int h, float scale, float animal)
    {
        _w = w; _h = h;
        _sprites.Clear();
        lock (world.Sync)
        {
            foreach (Actor a in world.Actors)
            {
                if (a.Look == null || a.Id == null || _sprites.Count >= Cols * Rows) continue;
                _sprites.Add(new Sprite
                {
                    Id = a.Id, Look = a.Look, Anim = a.Anim, AnimT = a.AnimT, FaceLeft = a.FaceLeft, IsLocal = a.IsLocal,
                    X = a.X, Y = a.Y, Level = a.Level, Name = a.Name ?? "",
                });
            }
        }
        if (_sprites.Count == 0) { _cells.Clear(); return false; }
        // 靠下的后画（挡住靠上的）；同一摞里层数高的后画
        _sprites.Sort((p, q) => p.Y != q.Y ? p.Y.CompareTo(q.Y) : p.Level.CompareTo(q.Level));

        Ensure(scale, animal);

        // 给每个角色分配固定的格子；离开的角色把格子让出来
        if (_cells.Count > _sprites.Count || _layoutDirty)
        {
            var gone = _cells.Keys.Where(id => !_sprites.Any(s => s.Id == id)).ToList();
            foreach (string id in gone) _cells.Remove(id);
        }
        for (int i = 0; i < _sprites.Count; i++)
        {
            Sprite s = _sprites[i];
            if (!_cells.TryGetValue(s.Id, out int cell))
            {
                for (cell = 0; cell < Cols * Rows && _cells.ContainsValue(cell); cell++) { }
                _cells[s.Id] = cell;
                _layoutDirty = true;
            }
            s.Cell = cell;
            if (_cellName[cell] != s.Name) _layoutDirty = true;
            _sprites[i] = s;
        }

        // 位置每帧都更新；重画角色（身体动画）比较耗 CPU：每秒最多 120 次，并且最多占用四分之一的时间
        double now = _clock.Elapsed.TotalSeconds;
        if (_layoutDirty || now - _lastRaster >= _rasterInterval)
        {
            Rasterize();
            double cost = _clock.Elapsed.TotalSeconds - now;
            _rasterInterval = Math.Clamp(cost * 4, 1.0 / 120, 1.0 / 30) - 0.0005;
            _lastRaster = now;
            _layoutDirty = false;
        }
        return true;
    }

    void Rasterize()
    {
        int maxRow = 0;
        _g.ResetTransform();
        _g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using var clear = new SolidBrush(Color.Transparent);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center };

        foreach (Sprite s in _sprites)
        {
            int ox = s.Cell % Cols * _cellW, oy = s.Cell / Cols * _cellH;
            maxRow = Math.Max(maxRow, s.Cell / Cols);
            _g.SetClip(new Rectangle(ox, oy, _cellW, _cellH));
            _g.CompositingMode = CompositingMode.SourceCopy;
            _g.FillRectangle(clear, ox, oy, _cellW, _cellH);
            _g.CompositingMode = CompositingMode.SourceOver;

            // 角色本体
            _g.SetClip(new Rectangle(ox, oy, _cellW, _animH));
            GraphicsState st = _g.Save();
            _g.TranslateTransform(ox + _cellW * 0.5f, oy + _anchor);
            _g.ScaleTransform(_p, _p);
            SheepPainter.Draw(_g, s.Look, s.Anim, s.AnimT, s.FaceLeft, shadow: s.Level == 0, effectScale: _fx / _p);
            _g.Restore(st);

            // 名字条：深色描边 + 浅色字，在草地和云上都看得清；自己的名字用黄色
            _g.SetClip(new Rectangle(ox, oy + _animH, _cellW, _nameH));
            using var path = new GraphicsPath();
            path.AddString(s.Name, _nameFont.FontFamily, (int)FontStyle.Bold, _nameFont.Size, new PointF(ox + _cellW * 0.5f, oy + _animH + 2 * _scale), fmt);
            using var pen = new Pen(Color.FromArgb(210, 30, 45, 30), 3f * _scale) { LineJoin = LineJoin.Round };
            using var fill = new SolidBrush(s.IsLocal ? Color.FromArgb(255, 0xFF, 0xE8, 0x7A) : Color.White);
            _g.DrawPath(pen, path);
            _g.FillPath(fill, path);
            _cellName[s.Cell] = s.Name;
            _nameW[s.Cell] = path.GetBounds().Width + 4 * _scale;
        }
        _g.ResetClip();

        int rows = (maxRow + 1) * _cellH;
        GL.BindTexture(GL.TEXTURE_2D, _tex);
        BitmapData bits = _atlas.LockBits(new Rectangle(0, 0, _atlas.Width, rows), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        GL.TexSubImage2D(GL.TEXTURE_2D, 0, 0, 0, _atlas.Width, rows, GL.BGRA, GL.UNSIGNED_BYTE, bits.Scan0);
        _atlas.UnlockBits(bits);
    }

    void Quad(float left, float top, float width, float height, float u0, float v0, float u1, float v1)
    {
        left = MathF.Round(left); top = MathF.Round(top);
        float x0 = left / _w * 2 - 1, x1 = (left + width) / _w * 2 - 1;
        float y0 = 1 - top / _h * 2, y1 = 1 - (top + height) / _h * 2;
        GL.TexCoord2f(u0, v0); GL.Vertex2f(x0, y0);
        GL.TexCoord2f(u1, v0); GL.Vertex2f(x1, y0);
        GL.TexCoord2f(u1, v1); GL.Vertex2f(x1, y1);
        GL.TexCoord2f(u0, v1); GL.Vertex2f(x0, y1);
    }

    /// <summary>
    /// 把层数在 [minLevel, maxLevel] 之间的角色贴到屏幕上。
    /// 调用后绑定的纹理和着色器会被改掉，调用方负责恢复。
    /// </summary>
    public void Draw(int minLevel, int maxLevel)
    {
        bool any = false;
        foreach (Sprite s in _sprites) if (s.Level >= minLevel && s.Level <= maxLevel) { any = true; break; }
        if (!any) return;

        GL.UseProgram(_prog);
        GL.BindTexture(GL.TEXTURE_2D, _tex);
        GL.Enable(GL.BLEND);
        GL.BlendFunc(GL.ONE, GL.ONE_MINUS_SRC_ALPHA);   // 图集是预乘透明度的

        float aw = _atlas.Width, ah = _atlas.Height, step = StackStep * _p;

        // 角色本体：内部清晰，只有轮廓那一圈柔和地淡出（范围约 1 个多像素，角色越大略宽一点）
        float soft = _scale * (0.9f + 0.3f * _animal);
        GL.Uniform2f(_uStep, soft / aw, soft / ah);
        GL.Begin(GL.QUADS);
        foreach (Sprite s in _sprites)
        {
            if (s.Level < minLevel || s.Level > maxLevel) continue;
            float ox = s.Cell % Cols * _cellW, oy = s.Cell / Cols * _cellH;
            Quad(s.X * _w - _cellW * 0.5f, s.Y * _h - s.Level * step - _anchor, _cellW, _animH,
                ox / aw, oy / ah, (ox + _cellW) / aw, (oy + _animH) / ah);
        }
        GL.End();

        // 名字：不模糊。站在地上的写在脚下；叠在上面的写在身体右侧，免得一摞名字挤在一起
        GL.Uniform2f(_uStep, 0, 0);
        GL.Begin(GL.QUADS);
        foreach (Sprite s in _sprites)
        {
            if (s.Level < minLevel || s.Level > maxLevel) continue;
            float ox = s.Cell % Cols * _cellW, oy = s.Cell / Cols * _cellH + _animH;
            float left = s.X * _w - _cellW * 0.5f, top = s.Y * _h + 3 * _scale;
            if (s.Level > 0)
            {
                left = s.X * _w + 40 * _p - (_cellW - _nameW[s.Cell]) * 0.5f;
                top = s.Y * _h - s.Level * step - 34 * _p - _nameH * 0.5f;
            }
            Quad(left, top, _cellW, _nameH, ox / aw, oy / ah, (ox + _cellW) / aw, (oy + _nameH) / ah);
        }
        GL.End();
        GL.Disable(GL.BLEND);
    }
}
