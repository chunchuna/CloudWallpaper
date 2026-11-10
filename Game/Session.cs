using CloudWallpaper.Net;

namespace CloudWallpaper.Game;

/// <summary>当前的联机状态：是否在房间里、房间里的世界，以及本地玩家发出的指令。</summary>
internal static class Session
{
    public static readonly World World = new();
    static readonly Random Rng = new();
    static volatile Room _room;

    public static Room Room => _room;
    public static bool InRoom => _room != null;

    /// <summary>进出房间、成员或连接状态变化时触发（可能在任意线程）。</summary>
    public static event Action Changed;

    static void Raise() => Changed?.Invoke();

    /// <summary>进入房间。房间码需已规整，名字需已填写。</summary>
    public static void Join(string code)
    {
        Leave();
        CharacterDef ch = Settings.ActiveCharacter;
        lock (World.Sync) World.Reset(Room.NewPeerId(), Room.CleanName(Settings.PlayerName), ch.Look.Clone(), Rng);
        var room = new Room(code, World);
        room.Changed += Raise;
        _room = room;
        if (Program.AutoJoin == null)   // 用 --join 测试时不改动用户的设置
        {
            Settings.LastRoom = code;
            Settings.Save();
        }
        Raise();
    }

    public static void Leave()
    {
        Room room = _room;
        if (room == null) return;
        _room = null;
        room.Changed -= Raise;
        room.Dispose();
        lock (World.Sync) World.Clear();
        Raise();
    }

    /// <summary>名字或角色外观改了：更新房间里的自己，并马上告诉其他人。</summary>
    public static void RefreshMe()
    {
        Room room = _room;
        if (room == null) return;
        lock (World.Sync)
        {
            if (World.Me == null) return;
            World.Me.Name = Room.CleanName(Settings.PlayerName);
            World.Me.Look = Settings.ActiveCharacter.Look.Clone();
        }
        room.SendPresence();
    }

    public static void MoveTo(float x, float y)
    {
        Room room = _room;
        if (room == null) return;
        bool ok, off;
        lock (World.Sync)
        {
            // 站在别人头上时不能自己走：这一下点击是「跳下来」，落在旁边
            off = World.DismountMe();
            ok = !off && World.MoveMe(x, y);
        }
        if (off) room.SendDismount();
        else if (ok) room.SendMove();
    }

    /// <summary>本地角色是否正站在别人头上。</summary>
    public static bool Riding { get { lock (World.Sync) return World.MeRiding; } }

    public static void Act(int anim)
    {
        Room room = _room;
        if (room == null) return;
        bool ok;
        lock (World.Sync) ok = World.ActMe(anim);
        if (ok) room.SendAction(anim);
    }

    /// <summary>走向另一个角色：kind 0 攻击（撞过去），1 站到它头顶。</summary>
    public static void Interact(string targetId, int kind)
    {
        Room room = _room;
        if (room == null) return;
        bool ok;
        lock (World.Sync) ok = World.ChaseMe(targetId, kind);
        if (ok) room.SendChase(targetId, kind);
    }

    /// <summary>渲染线程每帧调用。</summary>
    public static void Tick(float dt) => _room?.Tick(dt);

    // ---------------- 机器人（测试用）----------------

    /// <summary>--bot 房间码 [名字]：不显示壁纸，作为一个自动乱逛的角色加入房间，把收到的事件写到日志。</summary>
    public static void RunBot(string code, string name, string logFile)
    {
        var world = new World();
        var rng = new Random();
        world.Reset(Room.NewPeerId(), name, Look.Random(rng), rng);
        using var room = new Room(code, world);
        void Log(string s) { try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] {s}\r\n"); } catch { } }
        room.Trace = Log;
        Log($"机器人 {name} 进入房间 {code}，id={room.MyId}");

        // 环境变量 CW_BOT_RIDE=1：机器人一有机会就往别人头上站（测试叠罗汉用）
        int rideChance = Environment.GetEnvironmentVariable("CW_BOT_RIDE") == "1" ? 90 : 22;
        long next = Environment.TickCount64 + 3000, lastStatus = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        while (true)
        {
            Thread.Sleep(33);
            double now = clock.Elapsed.TotalSeconds;
            room.Tick((float)(now - last));
            last = now;

            long ticks = Environment.TickCount64;
            if (ticks - lastStatus > 5000)
            {
                lastStatus = ticks;
                string members;
                lock (world.Sync) members = string.Join(", ", world.Actors.Select(a => $"{a.Name}({a.X:0.00},{a.Y:0.00})"));
                Log($"状态：局域网={(room.LanOk ? "开" : "关")} 中继={room.RelayLive}/{room.RelayTotal} 成员=[{members}]");
            }
            if (ticks < next) continue;
            next = ticks + rng.Next(3500, 6500);

            Actor other;
            bool riding;
            lock (world.Sync) { other = world.Actors.FirstOrDefault(a => !a.IsLocal); riding = world.MeRiding; }
            int roll = rng.Next(100);
            bool ok;
            if (riding)
            {
                // 站在别人头上：大多数时候待着（偶尔做个动作），有时跳下来
                if (roll < 25 && rideChance < 50) { lock (world.Sync) ok = world.DismountMe(); if (ok) { room.SendDismount(); Log("我从头顶跳下来"); } }
                else if (roll < 55)
                {
                    int anim = Anim.Wheel[rng.Next(Anim.Wheel.Length)].Anim;
                    lock (world.Sync) ok = world.ActMe(anim);
                    if (ok) { room.SendAction(anim); Log($"我在头顶做动作 {anim}"); }
                }
            }
            else if (other != null && roll < rideChance)
            {
                int kind = rideChance > 50 || rng.Next(2) == 1 ? 1 : 0;
                lock (world.Sync) ok = world.ChaseMe(other.Id, kind);
                if (ok) { room.SendChase(other.Id, kind); Log($"我去{(kind == 0 ? "撞" : "站头顶")} {other.Name}"); }
            }
            else if (roll < 50)
            {
                int anim = Anim.Wheel[rng.Next(Anim.Wheel.Length)].Anim;
                lock (world.Sync) ok = world.ActMe(anim);
                if (ok) { room.SendAction(anim); Log($"我做动作 {anim}"); }
            }
            else
            {
                float x = 0.3f + (float)rng.NextDouble() * 0.6f, y = 0.2f + (float)rng.NextDouble() * 0.7f;
                lock (world.Sync) ok = world.MoveMe(x, y);
                if (ok) { room.SendMove(); Log($"我走向 ({x:0.00}, {y:0.00})"); }
            }
        }
    }
}
