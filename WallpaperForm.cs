using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text;
using CloudWallpaper.Game;
using CloudWallpaper.UI;

namespace CloudWallpaper;

internal sealed class WallpaperForm : Form
{
    const int FieldDownscale = 4;        // 密度场相对屏幕的缩小倍数
    const double SceneHz = 60;           // 背景和云每秒重算的次数（角色不受此限制，跟显示器刷新率走）
    const float BlobEdge = 0.61f;        // 单个云团在阈值处的半径 / 影响半径

    readonly NotifyIcon _tray;
    Thread _renderThread, _scanThread;
    volatile bool _running = true, _paused;
    volatile int _refreshHz = Native.MaxRefreshRate();   // 所有显示器里最高的刷新率
    IntPtr _host;

    readonly object _iconLock = new();
    RECT[] _icons = Array.Empty<RECT>();
    bool _iconsDirty = true;

    public WallpaperForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 16, 16);
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开菜单（角色 / 联机 / 主题）", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出云朵壁纸", null, (_, _) => Close());
        _tray = new NotifyIcon { Icon = MakeTrayIcon(), Text = "云朵动态壁纸", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    GameMenuForm _settings;

    public void ShowSettings()
    {
        if (_settings == null || _settings.IsDisposed)
        {
            _settings = new GameMenuForm(_tray.Icon);
            // 关掉窗口只是收起来，壁纸还在运行：每次都提醒一下去哪里找它
            _settings.FormClosed += (_, _) =>
            {
                if (_running)
                    _tray.ShowBalloonTip(5000, "已收到任务栏图标里", "壁纸仍在运行。双击右下角的云朵图标可以重新打开窗口，右键可以退出。", ToolTipIcon.Info);
            };
        }
        _settings.Show();
        if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
        _settings.Activate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x0020;                     // CS_OWNDC（OpenGL 需要）
            cp.ExStyle |= 0x00000080 | 0x08000000;       // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Attach();
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "Render" };
        _scanThread = new Thread(ScanLoop) { IsBackground = true, Name = "IconScan" };
        _renderThread.Start();
        _scanThread.Start();

        // 联机时才监听桌面上的鼠标操作
        _input = new DesktopInput(this, () => { lock (_iconLock) return _icons; });
        Session.Changed += OnSessionChanged;
        if (Program.AutoJoin != null) Session.Join(Program.AutoJoin);   // 没填名字时显示为「无名小羊」，不改动设置
        // 自动更新：新版本下载并替换好之后，挑一个不打扰的时机（不在房间里、窗口没开着）悄悄重启
        _updateTimer.Tick += (_, _) =>
        {
            if (!Updater.RestartPending || !_running) return;
            bool menuOpen = _settings != null && !_settings.IsDisposed && _settings.Visible;
            Program.Trace($"update pending: inRoom={Session.InRoom} menuOpen={menuOpen}");
            if (!Session.InRoom && !menuOpen) { _updateTimer.Stop(); Updater.Restart(showWindow: false); }
            else if (!_updateTold)
            {
                _updateTold = true;
                _tray.ShowBalloonTip(5000, "新版本已下载", "离开房间并关闭窗口后会自动换成新版本，也可以在「关于」页点「立即重启」。", ToolTipIcon.Info);
            }
        };
        _updateTimer.Start();

        // 启动即应用壁纸并显示窗口（--hidden 时只应用壁纸）
        if (!Program.Hidden) BeginInvoke(ShowSettings);

        // 第一次使用：用气泡指出托盘图标在哪里
        if (!Settings.TrayTipShown && !Program.Hidden)
        {
            Settings.TrayTipShown = true;
            Settings.Save();
            BeginInvoke(() => _tray.ShowBalloonTip(8000, "云朵动态壁纸在这里", "壁纸已经应用。我住在右下角任务栏的这个云朵图标里：双击打开窗口，右键可以退出。", ToolTipIcon.Info));
        }
    }

    DesktopInput _input;
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 20000 };
    bool _updateTold;

    void OnSessionChanged()
    {
        if (!_running || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (_running) _input.SetEnabled(Session.InRoom); }); }
        catch (InvalidOperationException) { }
    }

    /// <summary>把窗口挂到桌面图标层后面，并铺满整个桌面。</summary>
    void Attach()
    {
        _host = DesktopIcons.FindWallpaperHost(out _);
        if (Native.GetAncestor(Handle, 1) != _host) Native.SetParent(Handle, _host);   // 1 = GA_PARENT
        Native.GetClientRect(_host, out RECT rc);
        Native.GetClientRect(Handle, out RECT mine);
        if (mine.Width != rc.Width || mine.Height != rc.Height || !Native.IsWindowVisible(Handle))
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, rc.Width, rc.Height, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_SHOWWINDOW);
    }

    // ---------------- 图标扫描线程 ----------------
    void ScanLoop()
    {
        using var desktop = new DesktopIcons();
        IntPtr hwnd = Handle;
        int tick = 0;
        while (_running)
        {
            try
            {
                // 资源管理器重启 / 分辨率变化后重新挂载；顺便更新显示器刷新率
                if (tick++ % 8 == 0 && _running && !_paused)
                {
                    BeginInvoke(() => { if (_running) Attach(); });
                    _refreshHz = Native.MaxRefreshRate();
                }

                // 桌面被其他窗口（网页、游戏……）完全挡住时一个像素都看不到：停止渲染，也不再读取图标位置
                bool covered = !Program.NoPause && Native.DesktopFullyCovered();
                _paused = covered;
                if (covered) { Thread.Sleep(500); continue; }

                RECT[] rects = desktop.Read(hwnd);
                if (rects != null)
                {
                    lock (_iconLock)
                    {
                        if (!Same(rects, _icons)) { _icons = rects; _iconsDirty = true; }
                    }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            Thread.Sleep(250);
        }
    }

    static bool Same(RECT[] a, RECT[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Left != b[i].Left || a[i].Top != b[i].Top || a[i].Right != b[i].Right || a[i].Bottom != b[i].Bottom) return false;
        return true;
    }

    // ---------------- 渲染线程 ----------------
    void RenderLoop()
    {
        IntPtr hwnd = Handle, hdc = IntPtr.Zero, ctx = IntPtr.Zero;
        try
        {
            hdc = Native.GetDC(hwnd);
            var pfd = new PIXELFORMATDESCRIPTOR
            {
                nSize = 40, nVersion = 1,
                dwFlags = 0x4 | 0x20 | 0x1,   // DRAW_TO_WINDOW | SUPPORT_OPENGL | DOUBLEBUFFER
                cColorBits = 32,
            };
            int fmt = Native.ChoosePixelFormat(hdc, ref pfd);
            if (fmt == 0 || !Native.SetPixelFormat(hdc, fmt, ref pfd)) throw new Exception("无法设置像素格式");
            ctx = GL.wglCreateContext(hdc);
            if (ctx == IntPtr.Zero || !GL.wglMakeCurrent(hdc, ctx)) throw new Exception("无法创建 OpenGL 上下文");

            GL.LoadExtensions();
            GL.SwapInterval(1);
            Native.timeBeginPeriod(1);   // 让 Sleep(1) 真的只睡约 1 毫秒，高刷新率下限帧才准
            IntPtr preciseTimer = Native.CreatePreciseTimer();
            uint prog = GL.BuildProgram(Shaders.Vertex, Shaders.Fragment);
            GL.UseProgram(prog);
            int uRes = GL.GetUniformLocation(prog, "uRes"), uTime = GL.GetUniformLocation(prog, "uTime");
            int uScale = GL.GetUniformLocation(prog, "uScale");
            int uTheme = GL.GetUniformLocation(prog, "uTheme"), uInvert = GL.GetUniformLocation(prog, "uInvert");
            int uBlobCount = GL.GetUniformLocation(prog, "uBlobCount"), uBlobs = GL.GetUniformLocation(prog, "uBlobs");
            int uPass = GL.GetUniformLocation(prog, "uPass"), uCloudTime = GL.GetUniformLocation(prog, "uCloudTime");
            int uExtra = GL.GetUniformLocation(prog, "uExtra"), uShadow = GL.GetUniformLocation(prog, "uShadow");
            uint sceneFbo = 0, cloudFbo = 0, sceneTex = 0, cloudTex = 0;
            double sceneAt = -1;
            bool sceneInRoom = false;
            double cloudTime = 0, fpsAt = 0;
            int frames = 0;
            string fpsLog = Environment.GetEnvironmentVariable("CW_FPSLOG") == "1" ? Path.Combine(Path.GetTempPath(), "cw_fps.log") : null;
            var drifting = new DriftingClouds();
            var sprites = new SpriteLayer();

            static void Quad()
            {
                GL.Begin(GL.QUADS);
                GL.Vertex2f(-1, -1); GL.Vertex2f(1, -1); GL.Vertex2f(1, 1); GL.Vertex2f(-1, 1);
                GL.End();
            }
            GL.Uniform1i(GL.GetUniformLocation(prog, "uField"), 0);

            GL.GenTextures(1, out uint tex);
            GL.BindTexture(GL.TEXTURE_2D, tex);
            GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR);
            GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
            GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_EDGE);
            GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_EDGE);
            GL.PixelStorei(GL.UNPACK_ALIGNMENT, 1);

            int w = 0, h = 0, fw = 0, fh = 0;
            float[] target = null, current = null;
            bool settling = false;
            double time = 0;
            var clock = Stopwatch.StartNew();
            double last = 0;

            while (_running)
            {
                if (_paused)
                {
                    // 桌面被挡住：不渲染、不占显卡。只有在房间里时才每秒推进几次世界
                    // （走路、追上别人后的结算），这点计算量可以忽略
                    bool room = Session.InRoom;
                    Thread.Sleep(room ? 200 : 500);
                    double t2 = clock.Elapsed.TotalSeconds;
                    if (room) Session.Tick((float)Math.Min(t2 - last, 0.25));
                    last = t2;
                    continue;
                }

                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(now - last, 0.1);
                last = now;
                time += dt;

                Native.GetClientRect(hwnd, out RECT rc);
                if (rc.Width < 1 || rc.Height < 1) { Thread.Sleep(100); continue; }

                bool rebuild = false;
                if (rc.Width != w || rc.Height != h)
                {
                    w = rc.Width; h = rc.Height;
                    fw = Math.Max(1, w / FieldDownscale); fh = Math.Max(1, h / FieldDownscale);
                    target = new float[fw * fh];
                    current = new float[fw * fh];
                    GL.Viewport(0, 0, w, h);
                    rebuild = true;
                }

                RECT[] icons = null;
                lock (_iconLock)
                {
                    if (_iconsDirty || rebuild) { icons = _icons; _iconsDirty = false; }
                }
                if (icons != null) { BuildField(target, fw, fh, icons, w, h); settling = true; }

                // 当前密度场平滑地逼近目标：图标移动后旧云消散、新云生长
                if (settling)
                {
                    float k = 1f - MathF.Exp(-dt * 3.5f), maxDiff = 0f;
                    for (int i = 0; i < current.Length; i++)
                    {
                        float diff = target[i] - current[i];
                        if (diff == 0f) continue;
                        float ad = MathF.Abs(diff);
                        if (ad < 0.002f) current[i] = target[i];
                        else { current[i] += diff * k; if (ad > maxDiff) maxDiff = ad; }
                    }
                    settling = maxDiff > 0f;
                    GL.TexImage2D(GL.TEXTURE_2D, 0, (int)GL.R32F, fw, fh, 0, GL.RED, GL.FLOAT, current);
                }

                GL.Uniform2f(uRes, w, h);
                GL.Uniform1f(uTime, (float)time);
                float scale = Native.GetDpiForWindow(hwnd) / 96f;
                bool inRoom = Session.InRoom;
                int theme = inRoom ? Settings.ThemeGrass : Settings.Theme;   // 联机固定在草原
                if (Program.ThemeOverride >= 0) theme = Program.ThemeOverride;
                GL.Uniform1f(uScale, scale);
                GL.Uniform1i(uTheme, theme);
                GL.Uniform1i(uInvert, theme == Settings.ThemeSky && (Settings.Invert || Program.InvertOverride) ? 1 : 0);

                // 主题的详细配置
                bool motion = Settings.CloudMotion, extra = Settings.ExtraClouds;
                if (motion) cloudTime += dt;               // 关闭「云朵流动」后云的时间停住，草地的风照常
                GL.Uniform1f(uCloudTime, (float)cloudTime);
                GL.Uniform1i(uExtra, extra ? 1 : 0);
                GL.Uniform1i(uShadow, Settings.Shadows ? 1 : 0);
                if (theme == Settings.ThemeGrass && extra)
                {
                    // 草原主题：图标之外自由飘动的云
                    drifting.Update(motion ? dt : 0f, w, h, scale);
                    GL.Uniform4fv(uBlobs, DriftingClouds.MaxBlobs, drifting.Blobs);
                    GL.Uniform1i(uBlobCount, drifting.Count);
                }
                else GL.Uniform1i(uBlobCount, 0);

                // ---- 场景：草地/天空和云的着色器很重，而它们变化很慢，所以每秒只重算 SceneHz 次，
                //      结果存进两张离屏纹理（背景、云层）；显示器每一帧只做便宜的贴图合成 ----
                if (rebuild)
                {
                    if (sceneFbo == 0) { sceneFbo = GL.GenFramebuffer(); cloudFbo = GL.GenFramebuffer(); }
                    GL.AttachColorTexture(sceneFbo, ref sceneTex, w, h);
                    GL.AttachColorTexture(cloudFbo, ref cloudTex, w, h);
                    GL.BindTexture(GL.TEXTURE_2D, tex);
                }
                if (rebuild || inRoom != sceneInRoom || now - sceneAt >= 1.0 / SceneHz - 0.0005)
                {
                    sceneAt = now; sceneInRoom = inRoom;
                    GL.BindFramebuffer(sceneFbo);
                    GL.Uniform1i(uPass, inRoom ? 1 : 0);      // 联机时背景和云分开画，角色夹在中间
                    Quad();
                    if (inRoom)
                    {
                        GL.BindFramebuffer(cloudFbo);
                        GL.ClearColor(0, 0, 0, 0);
                        GL.Clear(GL.COLOR_BUFFER_BIT);
                        GL.Uniform1i(uPass, 2);
                        Quad();
                    }
                    GL.BindFramebuffer(0);
                }

                // ---- 合成：每个显示帧都做，角色的位置和动画跟显示器刷新率走 ----
                sprites.Blit(sceneTex, blend: false);
                if (inRoom)
                {
                    // 先背景，再角色，最后把云叠上去——角色走到云下面会被遮住
                    Session.Tick(dt);
                    bool any = sprites.Prepare(Session.World, w, h, scale, Settings.AnimalScale);
                    if (any) sprites.Draw(0, World.PierceLevel - 1);
                    sprites.Blit(cloudTex, blend: true);
                    if (any) sprites.Draw(World.PierceLevel, int.MaxValue);   // 叠得够高的从云里冒出来
                }
                GL.UseProgram(prog);
                GL.BindTexture(GL.TEXTURE_2D, tex);
                Native.SwapBuffers(hdc);

                // 调试：环境变量 CW_FPSLOG=1 时每秒把实际帧率写到临时目录
                if (fpsLog != null)
                {
                    frames++;
                    if (now - fpsAt >= 1)
                    {
                        try { File.AppendAllText(fpsLog, $"{DateTime.Now:HH:mm:ss} fps={frames / (now - fpsAt):0} hz={_refreshHz} inRoom={inRoom}\r\n"); } catch { }
                        frames = 0; fpsAt = now;
                    }
                }

                // 帧率跟显示器刷新率走（例如 360 Hz）。垂直同步正常时 SwapBuffers 已经等够了；
                // 没等够（垂直同步没生效）才在这里补齐，避免空转跑到几千帧
                // 不在房间里时画面只有慢慢流动的云，按场景的频率刷新就够了，省电
                // 设置里可以自定义帧率上限；不设则跟随显示器刷新率
                int limit = Settings.FpsLimit;
                double targetHz = inRoom ? (limit > 0 ? limit : Math.Max(30, _refreshHz)) : (limit > 0 ? Math.Min(limit, SceneHz) : SceneHz);
                double frameEnd = now + 1.0 / targetHz - 0.0002;
                while (true)
                {
                    double remain = frameEnd - clock.Elapsed.TotalSeconds;
                    if (remain <= 0) break;
                    if (remain > 0.0006) Native.PreciseSleep(preciseTimer, remain - 0.0004);   // 高精度定时器，不空转
                    else Thread.SpinWait(20);
                }
            }
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            if (_running)
                BeginInvoke(() => { MessageBox.Show(ex.Message, "云朵动态壁纸", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); });
        }
        finally
        {
            Native.timeEndPeriod(1);
            GL.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            if (ctx != IntPtr.Zero) GL.wglDeleteContext(ctx);
            if (hdc != IntPtr.Zero) Native.ReleaseDC(hwnd, hdc);
        }
    }

    /// <summary>根据图标矩形生成云朵密度场：每个图标一团扁平云体 + 顶部几个云泡，相邻的自然叠加融合。</summary>
    static void BuildField(float[] f, int fw, int fh, RECT[] icons, int w, int h)
    {
        Array.Clear(f);
        float sx = (float)fw / w, sy = (float)fh / h;
        foreach (RECT r in icons)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            float u = r.Width;
            float cx = r.Left + r.Width * 0.5f;
            float cy = r.Top + r.Height * 0.5f;
            float rx = 0.82f * u, ry = Math.Max(0.78f * u, 0.64f * r.Height);

            // 主云体：比图标格子大一圈，保证相邻图标之间的密度始终高于「内部」阈值，融合后没有接缝
            Stamp(f, fw, fh, sx, sy, cx, cy, rx, ry);

            // 四周一圈小云泡：只影响外轮廓，让边缘有起伏（由位置决定的稳定随机量）
            for (int k = 0; k < 5; k++)
            {
                float ang = (k + Hash(r.Left, r.Top, k) * 0.8f) * (MathF.PI * 2f / 5f);
                float pr = (0.26f + 0.12f * Hash(r.Left, r.Top, 10 + k)) * u;
                Stamp(f, fw, fh, sx, sy, cx + MathF.Cos(ang) * rx * 0.72f, cy + MathF.Sin(ang) * ry * 0.72f, pr, pr);
            }
        }
    }

    static float Hash(int x, int y, int salt)
    {
        uint n = (uint)(x * 374761393 + y * 668265263 + salt * 1274126177);
        n = (n ^ (n >> 13)) * 1274126177u;
        n ^= n >> 16;
        return (n & 0xFFFFFF) / (float)0x1000000;
    }

    /// <summary>叠加一个椭圆云团。cx/cy/rx/ry 为屏幕像素，rx/ry 是可见半径。</summary>
    static void Stamp(float[] f, int fw, int fh, float sx, float sy, float cx, float cy, float rx, float ry)
    {
        cx *= sx; cy *= sy;
        float ix = rx * sx / BlobEdge, iy = ry * sy / BlobEdge;
        int x0 = Math.Max(0, (int)(cx - ix)), x1 = Math.Min(fw - 1, (int)(cx + ix) + 1);
        int y0 = Math.Max(0, (int)(cy - iy)), y1 = Math.Min(fh - 1, (int)(cy + iy) + 1);
        for (int y = y0; y <= y1; y++)
        {
            float dy = (y + 0.5f - cy) / iy;
            int row = y * fw;
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f - cx) / ix;
                float d2 = dx * dx + dy * dy;
                if (d2 >= 1f) continue;
                float v = 1f - d2;
                f[row + x] += v * v;
            }
        }
    }

    // ---------------- 退出 ----------------
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Program.Trace("closing: begin, reason=" + e.CloseReason);
        _running = false;
        Session.Changed -= OnSessionChanged;
        _input?.Dispose();
        Session.Leave();
        _renderThread?.Join(1500);
        _scanThread?.Join(1500);
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
        Program.Trace("closing: end, cancel=" + e.Cancel);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        // 重新应用原壁纸，清掉残留画面
        var sb = new StringBuilder(520);
        if (Native.SystemParametersInfo(0x0073, (uint)sb.Capacity, sb, 0))
            Native.SystemParametersInfo(0x0014, 0, sb, 0);
        Program.Trace("closed");
    }

    static Icon MakeTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var sky = new SolidBrush(Color.FromArgb(40, 130, 235));
            g.FillEllipse(sky, 0, 0, 31, 31);
            g.FillEllipse(Brushes.White, 5, 14, 22, 11);
            g.FillEllipse(Brushes.White, 8, 9, 10, 10);
            g.FillEllipse(Brushes.White, 15, 11, 9, 9);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
