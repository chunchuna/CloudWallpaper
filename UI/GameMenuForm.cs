using CloudWallpaper.Game;
using CloudWallpaper.Net;

namespace CloudWallpaper.UI;

/// <summary>
/// 游戏风格的主菜单：左边是悬空岛和站在上面的角色，右边是四页内容——角色（捏角色）、联机、主题、关于。
/// 所有修改立即生效并保存。
/// </summary>
internal sealed class GameMenuForm : Form
{
    const int W = 940, H = 580, PanelX = 552, PanelW = 376;

    readonly IslandView _island = new();
    readonly GameButton[] _tabs;
    readonly Panel[] _pages;
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    bool _loading;

    // 角色页
    TextBox _charName;
    Label _charIndex;
    GameButton _deleteChar;
    GameButton[] _species, _horns, _accessories;
    SwatchRow _body, _face, _hornColor, _spot, _accColor;

    // 联机页
    TextBox _playerName, _roomCode;
    Panel _lobby, _inRoom;
    Label _joinHint, _roomCodeLabel, _status, _fighter, _membersCaption;
    MemberList _members;

    // 主题页
    GameButton[] _themes, _styles;
    GameButton _motion, _extra, _shadow, _fpsAuto;
    TextBox _fpsBox;
    Label _themeNote, _animalScale;
    Panel _styleBox;

    public GameMenuForm(Icon icon)
    {
        Text = "云朵动态壁纸";
        Icon = icon;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(W, H);
        BackColor = Ui.Paper;
        Font = new Font(Ui.FontName, 9f);
        DoubleBuffered = true;

        _island.Bounds = new Rectangle(0, 0, 540, H);
        _island.Character = () => Settings.ActiveCharacter;
        _island.DragStart += DragWindow;
        Controls.Add(_island);

        // ---- 顶部：页签和关闭按钮 ----
        string[] names = { "角色", "联机", "主题", "关于" };
        _tabs = new GameButton[names.Length];
        _pages = new Panel[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            _tabs[i] = new GameButton(names[i], PanelX + i * 82, 16, 76, 38, 10.5f) { Accent = Ui.Orange };
            _tabs[i].Click += (_, _) => ShowPage(index);
            _pages[i] = new Panel { Bounds = new Rectangle(PanelX, 70, PanelW, H - 82), BackColor = Ui.Paper, Visible = false };
            Controls.Add(_tabs[i]);
            Controls.Add(_pages[i]);
        }
        var close = new GameButton("✕", W - 46, 16, 34, 38, 10f) { Accent = Ui.Red };
        close.Click += (_, _) => Close();
        Controls.Add(close);

        BuildCharacterPage(_pages[0]);
        BuildOnlinePage(_pages[1]);
        BuildThemePage(_pages[2]);
        BuildAboutPage(_pages[3]);

        LoadCharacter();
        RefreshOnline();
        RefreshTheme();
        ShowPage(Math.Clamp(Program.OpenPage, 0, _pages.Length - 1));

        Session.Changed += OnSessionChanged;
        _refresh.Tick += (_, _) => { if (Visible) RefreshOnline(); };
        _refresh.Start();
    }

    // ---------------- 窗口外观 ----------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (IsHandleCreated) ApplyRegion();
    }

    void ApplyRegion()
    {
        using var path = SheepPainter.RoundRect(new RectangleF(0, 0, Width, Height), 20 * DeviceDpi / 96f);
        Region = new Region(path);
    }

    void DragWindow()
    {
        Native.ReleaseCapture();
        Native.SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < 66 * DeviceDpi / 96f) DragWindow();
    }

    void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visible = i == index;
            _tabs[i].Selected = i == index;
        }
        if (index == 1) RefreshOnline();
        if (index == 2) RefreshTheme();
    }

    // ---------------- 角色页 ----------------

    void BuildCharacterPage(Panel p)
    {
        p.Controls.Add(Ui.Caption("我的角色（联机时使用当前选中的这个）", 0, 0));
        var prev = new GameButton("◀", 0, 22, 34, 34);
        _charName = Ui.Input(40, 25, 128, 8);
        var next = new GameButton("▶", 174, 22, 34, 34);
        _charIndex = Ui.Text("", 212, 30, 40, 20);
        _charIndex.ForeColor = Ui.Soft;
        var add = new GameButton("新建", 254, 22, 58, 34) { Solid = true };
        _deleteChar = new GameButton("删除", 318, 22, 58, 34) { Accent = Ui.Red };
        prev.Click += (_, _) => SwitchCharacter(-1);
        next.Click += (_, _) => SwitchCharacter(1);
        add.Click += (_, _) =>
        {
            if (Settings.Characters.Count >= 12) return;
            Settings.Characters.Add(new CharacterDef { Name = "小羊" + (Settings.Characters.Count + 1), Look = Look.Default(Look.Sheep) });
            Settings.Active = Settings.Characters.Count - 1;
            CharacterChanged();
            LoadCharacter();
        };
        _deleteChar.Click += (_, _) =>
        {
            if (Settings.Characters.Count <= 1) return;
            Settings.Characters.RemoveAt(Settings.Active);
            Settings.Active = Math.Min(Settings.Active, Settings.Characters.Count - 1);
            CharacterChanged();
            LoadCharacter();
        };
        _charName.TextChanged += (_, _) =>
        {
            if (_loading) return;
            Settings.ActiveCharacter.Name = string.IsNullOrWhiteSpace(_charName.Text) ? "小羊" : _charName.Text.Trim();
            Settings.Save();
        };
        p.Controls.AddRange(new Control[] { prev, _charName, next, _charIndex, add, _deleteChar });

        p.Controls.Add(Ui.Caption("种类", 0, 70));
        _species = Chips(p, Look.SpeciesNames, 0, 90, 90, i =>
        {
            // 换种类时换成该种类的默认长相，保留饰品
            Look old = Settings.ActiveCharacter.Look, l = Look.Default(i);
            l.A = old.A; l.AC = old.AC;
            Settings.ActiveCharacter.Look = l;
            CharacterChanged();
            LoadCharacter();
        });

        p.Controls.Add(Ui.Caption("毛色", 0, 134));
        _body = Swatches(p, Palette.Body, 152, (l, c) => l.B = c);
        p.Controls.Add(Ui.Caption("脸和腿的颜色", 0, 186));
        _face = Swatches(p, Palette.Face, 204, (l, c) => l.F = c);

        p.Controls.Add(Ui.Caption("角", 0, 240));
        _horns = Chips(p, Look.HornNames, 0, 260, 90, i => Edit(l => l.HS = i));
        _hornColor = Swatches(p, Palette.Horn, 302, (l, c) => l.H = c);

        p.Controls.Add(Ui.Caption("花纹颜色（奶牛、公牛）", 0, 338));
        _spot = Swatches(p, Palette.Spot, 356, (l, c) => l.P = c);

        p.Controls.Add(Ui.Caption("饰品", 0, 392));
        _accessories = Chips(p, Look.AccessoryNames, 0, 412, 59, i => Edit(l => l.A = i), 8.5f);
        _accColor = Swatches(p, Palette.Bright, 454, (l, c) => l.AC = c);
    }

    GameButton[] Chips(Panel p, string[] labels, int x, int y, int w, Action<int> pick, float fontSize = 9.5f)
    {
        var chips = new GameButton[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            chips[i] = new GameButton(labels[i], x + i * (w + 4), y, w, 34, fontSize);
            chips[i].Click += (_, _) => pick(index);
            p.Controls.Add(chips[i]);
        }
        return chips;
    }

    SwatchRow Swatches(Panel p, int[] colors, int y, Action<Look, int> set)
    {
        var row = new SwatchRow(colors, 0, y, PanelW);
        row.Picked += c => Edit(l => set(l, c));
        p.Controls.Add(row);
        return row;
    }

    void Edit(Action<Look> change)
    {
        if (_loading) return;
        change(Settings.ActiveCharacter.Look);
        CharacterChanged();
        LoadCharacter();
    }

    void SwitchCharacter(int delta)
    {
        int n = Settings.Characters.Count;
        Settings.Active = ((Settings.Active + delta) % n + n) % n;
        CharacterChanged();
        LoadCharacter();
        _island.Play(Anim.Wave);
    }

    /// <summary>角色有变化：保存，并让房间里的其他人马上看到。</summary>
    void CharacterChanged()
    {
        Settings.Save();
        Session.RefreshMe();
    }

    void LoadCharacter()
    {
        _loading = true;
        CharacterDef ch = Settings.ActiveCharacter;
        Look l = ch.Look;
        if (_charName.Text != ch.Name) _charName.Text = ch.Name;
        _charIndex.Text = $"{Settings.Active + 1}/{Settings.Characters.Count}";
        _deleteChar.Enabled = Settings.Characters.Count > 1;
        for (int i = 0; i < _species.Length; i++) _species[i].Selected = i == l.S;
        for (int i = 0; i < _horns.Length; i++) _horns[i].Selected = i == l.HS;
        for (int i = 0; i < _accessories.Length; i++) _accessories[i].Selected = i == l.A;
        _body.Value = l.B; _face.Value = l.F; _hornColor.Value = l.H; _spot.Value = l.P; _accColor.Value = l.AC;
        _hornColor.Enabled = l.HS != 0;
        _spot.Enabled = l.S >= Look.Cow;
        _accColor.Enabled = l.A != 0;
        if (_fighter != null) _fighter.Text = "出战角色：" + ch.Name + "（在「角色」页切换）";
        _loading = false;
    }

    // ---------------- 联机页 ----------------

    void BuildOnlinePage(Panel p)
    {
        p.Controls.Add(Ui.Caption("我的名字（显示在角色下方，只需填一次）", 0, 0));
        _playerName = Ui.Input(0, 22, 200, 12);
        _playerName.Text = Settings.PlayerName;
        _playerName.TextChanged += (_, _) =>
        {
            Settings.PlayerName = _playerName.Text.Trim();
            Settings.Save();
            Session.RefreshMe();
            _joinHint.Text = "";
        };
        p.Controls.Add(_playerName);
        _fighter = Ui.Text("", 0, 58, PanelW, 20);
        _fighter.ForeColor = Ui.Soft;
        p.Controls.Add(_fighter);

        // 还没进房间
        _lobby = new Panel { Bounds = new Rectangle(0, 92, PanelW, 400), BackColor = Ui.Paper };
        _lobby.Controls.Add(Ui.Caption("房间码（和朋友输入同一个就能见面）", 0, 0));
        _roomCode = Ui.Input(0, 22, 232, 40);
        _roomCode.CharacterCasing = CharacterCasing.Upper;
        _roomCode.Text = Settings.LastRoom;
        var paste = new GameButton("粘贴", 240, 19, 62, 34);
        paste.Click += (_, _) => { try { if (Clipboard.ContainsText()) _roomCode.Text = Clipboard.GetText().Trim(); } catch { } };
        var join = new GameButton("加入房间", 0, 66, 180, 44, 11f) { Solid = true };
        var create = new GameButton("创建新房间", 190, 66, 186, 44, 11f) { Solid = true, Accent = Ui.Orange };
        join.Click += (_, _) => Join(_roomCode.Text);
        create.Click += (_, _) => Join(RoomCrypto.NewCode());
        _roomCode.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Join(_roomCode.Text); } };
        _joinHint = Ui.Text("", 0, 118, PanelW, 22);
        _joinHint.ForeColor = Ui.Red;
        _lobby.Controls.AddRange(new Control[] { _roomCode, paste, join, create, _joinHint });
        _lobby.Controls.Add(Ui.Text(
            "· 创建房间后把房间码发给朋友，对方输入后点「加入房间」。\n" +
            "· 同一个局域网直接连通；不在一起时通过公共中继转发，\n  不需要服务器，内容用房间码加密。\n" +
            "· 联机时壁纸固定为草原，大家变成草原上的小羊（或者牛）。\n" +
            "· 进房间后再打开这个窗口，可以看到每位伙伴的头像和状态。", 0, 156, PanelW, 130));
        p.Controls.Add(_lobby);

        // 已在房间里
        _inRoom = new Panel { Bounds = new Rectangle(0, 92, PanelW, 400), BackColor = Ui.Paper, Visible = false };
        _inRoom.Controls.Add(Ui.Caption("当前房间", 0, 0));
        _roomCodeLabel = Ui.Text("", 0, 20, PanelW, 34, 15f, bold: true);
        var copy = new GameButton("复制房间码", 0, 60, 180, 40, 10.5f) { Solid = true };
        var leave = new GameButton("离开房间", 190, 60, 186, 40, 10.5f) { Solid = true, Accent = Ui.Red };
        copy.Click += (_, _) => { try { Clipboard.SetText(Session.Room?.Code ?? ""); copy.Text = "已复制"; } catch { } };
        copy.MouseLeave += (_, _) => copy.Text = "复制房间码";
        leave.Click += (_, _) => { Session.Leave(); RefreshOnline(); };
        _status = Ui.Text("", 0, 112, PanelW, 22);
        _status.ForeColor = Ui.Soft;
        _membersCaption = Ui.Caption("房间里的伙伴", 0, 138);
        _inRoom.Controls.Add(_membersCaption);
        // 成员多了可以滚动
        var scroll = new Panel { Bounds = new Rectangle(0, 158, PanelW, 172), BackColor = Ui.Paper, AutoScroll = true };
        _members = new MemberList { Bounds = new Rectangle(0, 0, PanelW - 20, 10) };
        scroll.Controls.Add(_members);
        _inRoom.Controls.AddRange(new Control[] { _roomCodeLabel, copy, leave, _status, scroll });
        Label how = Ui.Text(
            "· 点击桌面空白处走过去；站在别人头上时，点一下是跳下来\n" +
            "· 在桌面上按住鼠标中键，打开动作圆盘\n" +
            "· 点击别人的角色：攻击（撞过去），或站到它头顶搭人梯", 0, 338, PanelW, 62, 8.5f);
        how.ForeColor = Ui.Soft;
        _inRoom.Controls.Add(how);
        p.Controls.Add(_inRoom);
    }

    void Join(string input)
    {
        if (string.IsNullOrWhiteSpace(Settings.PlayerName))
        {
            _joinHint.Text = "先给自己起个名字，才能进入房间";
            _playerName.Focus();
            return;
        }
        string code = RoomCrypto.Normalize(input);
        if (code == null)
        {
            _joinHint.Text = "房间码至少 4 个字符";
            _roomCode.Focus();
            return;
        }
        _joinHint.Text = "";
        try { Session.Join(code); }
        catch (Exception ex) { Program.Log(ex); _joinHint.Text = "进入房间失败：" + ex.Message; }
        RefreshOnline();
    }

    void OnSessionChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(RefreshOnline); } catch (InvalidOperationException) { }
    }

    void RefreshOnline()
    {
        Room room = Session.Room;
        _lobby.Visible = room == null;
        _inRoom.Visible = room != null;
        if (room == null) { RefreshTheme(); return; }

        _roomCodeLabel.Text = room.Code;
        string net = room.RelayLive > 0 ? $"互联网已连接（{room.RelayLive}/{room.RelayTotal} 个中继）" : "互联网连接中…";
        _status.Text = (room.LanOk ? "局域网已开启 · " : "局域网不可用 · ") + net;
        var rows = new List<MemberList.Row>();
        lock (Session.World.Sync)
        {
            World world = Session.World;
            foreach (Actor a in world.Actors.OrderByDescending(a => a.IsLocal))
            {
                rows.Add(new MemberList.Row
                {
                    Name = a.Name ?? "…", Look = a.Look, Hp = a.Hp, Hunger = a.Hunger, Me = a.IsLocal,
                    Status = StatusOf(world, a),
                });
            }
        }
        _membersCaption.Text = $"房间里的伙伴（{rows.Count}）";
        _members.SetRows(rows);
        RefreshTheme();
    }

    /// <summary>一句话描述角色正在做什么。调用时需已持有 World 的锁。</summary>
    static string StatusOf(World world, Actor a)
    {
        if (a.RideId != null) return $"站在 {world.Find(a.RideId)?.Name ?? "别人"} 头顶";
        int carried = world.Actors.Count(o => o != a && world.IsAbove(o, a));
        if (carried > 0) return a.Moving ? $"驮着 {carried} 位赶路" : $"驮着 {carried} 位";
        return a.Anim switch
        {
            Anim.Walk => "散步中", Anim.Run => "冲刺中", Anim.Wave => "打招呼", Anim.Dance => "跳舞",
            Anim.Sleep => "睡觉", Anim.Eat => "吃草", Anim.Jump => "蹦跳", Anim.Sit => "坐着",
            Anim.Love => "比心", Anim.Angry => "生气", Anim.Ram => "撞人", Anim.Hit => "被撞晕了",
            _ => "发呆",
        };
    }

    // ---------------- 主题页 ----------------

    void BuildThemePage(Panel p)
    {
        p.Controls.Add(Ui.Caption("主题", 0, 0));
        string[] names = { "草原", "天空", "星空", "海洋", "雪山" };
        int[] ids = { Settings.ThemeGrass, Settings.ThemeSky, -1, -1, -1 };
        _themes = new GameButton[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int id = ids[i];
            _themes[i] = new GameButton(id >= 0 ? names[i] : names[i] + "\n待开发", i % 3 * 126, 22 + i / 3 * 62, 120, 54, id >= 0 ? 11f : 9f) { Tag = id };
            if (id < 0) _themes[i].Enabled = false;
            else _themes[i].Click += (_, _) => { Settings.Theme = id; Settings.Save(); RefreshTheme(); };
            p.Controls.Add(_themes[i]);
        }
        _themeNote = Ui.Text("", 0, 152, PanelW, 44);
        _themeNote.ForeColor = Ui.Soft;
        p.Controls.Add(_themeNote);

        // ---- 详细配置：绿色表示开启，点一下切换 ----
        p.Controls.Add(Ui.Caption("详细配置（绿色为开启）", 0, 200));
        _motion = new GameButton("云朵流动", 0, 222, 120, 40);
        _extra = new GameButton("无图标的小云", 126, 222, 124, 40);
        _shadow = new GameButton("地面云影", 256, 222, 120, 40);
        _motion.Click += (_, _) => { Settings.CloudMotion = !Settings.CloudMotion; Settings.Save(); RefreshTheme(); };
        _extra.Click += (_, _) => { Settings.ExtraClouds = !Settings.ExtraClouds; Settings.Save(); RefreshTheme(); };
        _shadow.Click += (_, _) => { Settings.Shadows = !Settings.Shadows; Settings.Save(); RefreshTheme(); };
        p.Controls.AddRange(new Control[] { _motion, _extra, _shadow });

        p.Controls.Add(Ui.Caption("动物在桌面上的大小（联机时）", 0, 276));
        var smaller = new GameButton("－", 0, 298, 44, 38, 12f);
        _animalScale = Ui.Text("", 48, 306, 76, 24, 11f, bold: true);
        _animalScale.TextAlign = ContentAlignment.MiddleCenter;
        var bigger = new GameButton("＋", 128, 298, 44, 38, 12f);
        var reset = new GameButton("恢复默认", 184, 298, 96, 38);
        void SetScale(float v)
        {
            Settings.AnimalScale = Math.Clamp(MathF.Round(v * 4) / 4, Settings.AnimalScaleMin, Settings.AnimalScaleMax);   // 每档 25%
            Settings.Save();
            RefreshTheme();
        }
        smaller.Click += (_, _) => SetScale(Settings.AnimalScale - 0.25f);
        bigger.Click += (_, _) => SetScale(Settings.AnimalScale + 0.25f);
        reset.Click += (_, _) => SetScale(1f);
        p.Controls.AddRange(new Control[] { smaller, _animalScale, bigger, reset });

        // 帧率：默认跟随显示器刷新率，也可以填一个数字
        p.Controls.Add(Ui.Caption("帧率上限（联机时动物的移动和动画）", 0, 346));
        _fpsAuto = new GameButton("跟随显示器", 0, 366, 110, 38);
        _fpsBox = Ui.Input(120, 371, 70, 4);
        _fpsBox.TextAlign = HorizontalAlignment.Center;
        var fpsUnit = Ui.Text("帧/秒", 194, 375, 50, 22);
        fpsUnit.ForeColor = Ui.Soft;
        var fps60 = new GameButton("60", 250, 366, 58, 38);
        var fps144 = new GameButton("144", 314, 366, 62, 38);
        void SetFps(int v)
        {
            Settings.FpsLimit = v <= 0 ? 0 : Math.Clamp(v, Settings.FpsMin, Settings.FpsMax);
            Settings.Save();
            RefreshTheme();
        }
        _fpsAuto.Click += (_, _) => SetFps(0);
        fps60.Click += (_, _) => SetFps(60);
        fps144.Click += (_, _) => SetFps(144);
        // 输入数字后按回车或点到别处生效
        void Commit() { if (!_loading) SetFps(int.TryParse(_fpsBox.Text.Trim(), out int v) ? v : 0); }
        _fpsBox.KeyPress += (_, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
        _fpsBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); } };
        _fpsBox.Leave += (_, _) => Commit();
        p.Controls.AddRange(new Control[] { _fpsAuto, _fpsBox, fpsUnit, fps60, fps144 });

        _styleBox = new Panel { Bounds = new Rectangle(0, 416, PanelW, 76), BackColor = Ui.Paper };
        _styleBox.Controls.Add(Ui.Caption("云朵样式（天空主题）", 0, 0));
        _styles = new[]
        {
            new GameButton("有图标的地方有云", 0, 22, 184, 40),
            new GameButton("没有图标的地方有云", 192, 22, 184, 40),
        };
        _styles[0].Click += (_, _) => { Settings.Invert = false; Settings.Save(); RefreshTheme(); };
        _styles[1].Click += (_, _) => { Settings.Invert = true; Settings.Save(); RefreshTheme(); };
        _styleBox.Controls.AddRange(_styles);
        p.Controls.Add(_styleBox);
    }

    void RefreshTheme()
    {
        if (_themes == null) return;
        bool inRoom = Session.InRoom;
        int theme = Settings.Theme;
        foreach (GameButton b in _themes)
        {
            int id = (int)b.Tag;
            if (id < 0) continue;
            b.Selected = id == theme;
        }
        _themeNote.Text = inRoom
            ? "正在联机：壁纸固定显示草原。离开房间后恢复为这里选的主题。"
            : theme == Settings.ThemeGrass ? "俯瞰草地，图标在云朵上，云在地面投下影子，还有自由飘动的云。"
            : "晴朗的蓝天，图标托在云朵上，相邻的云会连成一片。";
        _motion.Selected = Settings.CloudMotion;
        _extra.Selected = Settings.ExtraClouds;
        _shadow.Selected = Settings.Shadows;
        _shadow.Enabled = theme == Settings.ThemeGrass || inRoom;   // 云影只有草原有
        _animalScale.Text = $"{Settings.AnimalScale * 100:0}%";
        int fps = Settings.FpsLimit;
        _fpsAuto.Selected = fps == 0;
        string fpsText = fps == 0 ? "" : fps.ToString();
        if (_fpsBox.Text != fpsText && !_fpsBox.Focused) { _loading = true; _fpsBox.Text = fpsText; _loading = false; }
        _styleBox.Visible = theme == Settings.ThemeSky;
        _styles[0].Selected = !Settings.Invert;
        _styles[1].Selected = Settings.Invert;
    }

    // ---------------- 关于页 ----------------

    void BuildAboutPage(Panel p)
    {
        // 版本与自动更新
        p.Controls.Add(Ui.Caption("版本", 0, 300));
        p.Controls.Add(Ui.Text(Updater.CurrentText, 0, 320, 120, 28, 12f, bold: true));
        _updateStatus = Ui.Text(Updater.Status, 0, 352, PanelW, 40);
        _updateStatus.ForeColor = Ui.Soft;
        _updateButton = new GameButton("检查更新", 236, 314, 140, 38) { Solid = true, Enabled = Updater.Enabled };
        _updateButton.Click += async (_, _) =>
        {
            if (Updater.RestartPending) { Updater.Restart(showWindow: true); return; }
            _updateButton.Enabled = false;
            await Updater.CheckAsync();
            if (!IsDisposed) { _updateButton.Enabled = true; RefreshUpdate(); }
        };
        p.Controls.AddRange(new Control[] { _updateStatus, _updateButton });
        Updater.Changed += OnUpdaterChanged;
        RefreshUpdate();

        p.Controls.Add(Ui.Text("云朵动态壁纸", 0, 6, PanelW, 40, 18f, bold: true));
        p.Controls.Add(Ui.Text("实时识别桌面图标的位置，在图标下生成云朵；\n还可以和朋友进同一个房间，在草原上当一只小羊。", 0, 56, PanelW, 48));
        p.Controls.Add(Ui.Caption("作者", 0, 124));
        p.Controls.Add(Ui.Text("chunchun", 0, 146, PanelW, 30, 13f, bold: true));
        p.Controls.Add(Ui.Text("Claude Fable 5.2", 0, 178, PanelW, 30, 13f, bold: true));
        Label thanks = Ui.Text("联机方式参考了开源项目 lolPing：局域网广播 + 公共 Nostr 中继，\n房间码派生密钥，端到端加密，不需要自建服务器。", 0, 232, PanelW, 48);
        thanks.ForeColor = Ui.Soft;
        p.Controls.Add(thanks);
    }

    Label _updateStatus;
    GameButton _updateButton;

    void OnUpdaterChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(RefreshUpdate); } catch (InvalidOperationException) { }
    }

    void RefreshUpdate()
    {
        _updateStatus.Text = Updater.Enabled ? Updater.Status : "这个版本没有设置更新地址，不会自动更新。";
        _updateButton.Text = Updater.RestartPending ? "立即重启" : "检查更新";
        _updateButton.Accent = Updater.RestartPending ? Ui.Orange : Ui.Green;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Updater.Changed -= OnUpdaterChanged;
            Session.Changed -= OnSessionChanged;
            _refresh.Dispose();
        }
        base.Dispose(disposing);
    }
}
