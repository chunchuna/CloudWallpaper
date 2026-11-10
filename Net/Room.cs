using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudWallpaper.Game;

namespace CloudWallpaper.Net;

/// <summary>
/// 一个联机房间：把本地角色的动作发给其他人，把收到的消息应用到 World。
/// 同一条消息会同时走局域网和中继，谁先到用谁，靠每人递增的序号去重。
///
/// 消息（JSON，加密后发送），公共字段 t 类型、id 发送者、n 序号：
///   p 状态心跳（名字、外观、位置、目的地、动作）   m 走向某处   a 做动作
///   c 开始追某人（k: 0 攻击 1 拥抱）               h 追上了：撞击/拥抱生效   b 离开
/// </summary>
internal sealed class Room : IDisposable
{
    const int MaxMembers = 16;
    const long TimeoutMs = 12000, PresenceMs = 2000;

    public readonly string Code;
    public readonly string MyId;
    readonly RoomKeys _keys;
    readonly World _world;
    readonly LanTransport _lan = new();
    readonly RelayTransport _relay;
    readonly Dictionary<string, long> _lastSeq = new();
    readonly System.Threading.Timer _timer;
    long _seq, _lastPresence, _lastMoveSent;
    double _relayTokens = 10;
    long _relayRefill = Environment.TickCount64;
    bool _disposed;

    /// <summary>成员或连接状态变化（可能在任意线程触发）。</summary>
    public event Action Changed;
    /// <summary>调试日志（机器人模式用）。</summary>
    public Action<string> Trace;

    public bool LanOk => _lan.Ok;
    public int RelayLive => _relay.Live;
    public int RelayTotal => _relay.Total;

    public Room(string code, World world)
    {
        Code = code;
        _world = world;
        _keys = RoomCrypto.Derive(code);
        MyId = world.Me.Id;
        _relay = new RelayTransport(_keys.Topic);
        _lan.Packet += OnPacket;
        _relay.Packet += OnPacket;
        _lan.Start();
        _relay.Start();
        _timer = new System.Threading.Timer(_ => Housekeeping(), null, 300, 500);
    }

    public static string NewPeerId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    // ---------------- 发送 ----------------

    void Send(Action<Utf8JsonWriter> body, string type, bool important = true)
    {
        if (_disposed) return;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", type);
            w.WriteString("id", MyId);
            w.WriteNumber("n", Interlocked.Increment(ref _seq));
            body?.Invoke(w);
            w.WriteEndObject();
        }
        byte[] packet;
        try { packet = RoomCrypto.Seal(_keys.MsgKey, ms.ToArray()); }
        catch (Exception ex) { Program.Log(ex); return; }

        _lan.Broadcast(packet);

        // 公共中继要省着用：平均每秒不超过 5 条，允许短时间连发 10 条；不重要的消息超了就只走局域网
        lock (_lastSeq)
        {
            long now = Environment.TickCount64;
            _relayTokens = Math.Min(10, _relayTokens + (now - _relayRefill) / 1000.0 * 5);
            _relayRefill = now;
            if (_relayTokens < 1 && !important) return;
            _relayTokens = Math.Max(_relayTokens - 1, -5);
        }
        _relay.Broadcast(packet);
    }

    static float R(float v) => MathF.Round(v, 4);

    public void SendPresence()
    {
        Actor me; string name, ride; Look look; float x, y, tx, ty; bool mv, fl; int anim, hp, hunger;
        lock (_world.Sync)
        {
            me = _world.Me;
            if (me == null) return;
            name = me.Name; look = me.Look; x = me.X; y = me.Y; tx = me.TX; ty = me.TY; mv = me.Moving && me.ChaseId == null; fl = me.FaceLeft; anim = me.Anim;
            ride = me.RideId ?? ""; hp = me.Hp; hunger = me.Hunger;
        }
        _lastPresence = Environment.TickCount64;
        Send(w =>
        {
            w.WriteString("nm", name);
            w.WritePropertyName("lk");
            JsonSerializer.Serialize(w, look);
            w.WriteNumber("x", R(x)); w.WriteNumber("y", R(y)); w.WriteNumber("tx", R(tx)); w.WriteNumber("ty", R(ty));
            w.WriteBoolean("mv", mv); w.WriteBoolean("fl", fl); w.WriteNumber("a", anim);
            w.WriteString("rd", ride); w.WriteNumber("hp", hp); w.WriteNumber("hg", hunger);
        }, "p", important: false);
    }

    /// <summary>本地角色走向某处（已经在 World 里生效）。连点时限制发送频率，最后一次点击由心跳补上。</summary>
    public void SendMove()
    {
        long now = Environment.TickCount64;
        if (now - _lastMoveSent < 180) { _lastPresence = 0; return; }
        _lastMoveSent = now;
        float x, y, tx, ty;
        lock (_world.Sync) { Actor me = _world.Me; if (me == null) return; x = me.X; y = me.Y; tx = me.TX; ty = me.TY; }
        Send(w => { w.WriteNumber("x", R(x)); w.WriteNumber("y", R(y)); w.WriteNumber("tx", R(tx)); w.WriteNumber("ty", R(ty)); }, "m");
    }

    public void SendAction(int anim)
    {
        float x, y;
        lock (_world.Sync) { Actor me = _world.Me; if (me == null) return; x = me.X; y = me.Y; }
        Send(w => { w.WriteNumber("a", anim); w.WriteNumber("x", R(x)); w.WriteNumber("y", R(y)); }, "a");
    }

    public void SendChase(string targetId, int kind) =>
        Send(w => { w.WriteString("to", targetId); w.WriteNumber("k", kind); }, "c");

    /// <summary>本地角色从别人头顶跳下来了（已经在 World 里生效）。</summary>
    public void SendDismount()
    {
        float x, y;
        lock (_world.Sync) { Actor me = _world.Me; if (me == null) return; x = me.X; y = me.Y; }
        Send(w => { w.WriteNumber("x", R(x)); w.WriteNumber("y", R(y)); }, "d");
    }

    /// <summary>每帧调用：推进世界；本地角色追上目标时结算（撞击，或站到头顶）并通知所有人。</summary>
    public void Tick(float dt)
    {
        string to = null; int kind = 0; float ax = 0, ay = 0, vx = 0, vy = 0, kx = 0, ky = 0;
        lock (_world.Sync)
        {
            Actor victim = _world.Update(dt);
            Actor me = _world.Me;
            if (victim != null && me != null)
            {
                kind = me.ChaseKind;
                ax = me.X; ay = me.Y; vx = victim.X; vy = victim.Y;
                if (kind == 1)
                {
                    // 站到这一摞最顶上那只的头上
                    Actor carrier = _world.TopOf(victim);
                    if (_world.Mount(me, carrier)) to = carrier.Id;
                    else me.ChaseId = null;
                }
                else
                {
                    to = victim.Id;
                    float dx = (vx - ax) * World.Aspect, dy = vy - ay, d = MathF.Max(0.0001f, MathF.Sqrt(dx * dx + dy * dy));
                    kx = Math.Clamp(vx + dx / d * 0.11f / World.Aspect, 0.03f, 0.97f);
                    ky = Math.Clamp(vy + dy / d * 0.11f, 0.08f, 0.96f);
                    _world.ApplyHit(me, victim, ax, ay, vx, vy, kx, ky);
                }
            }
        }
        if (to == null) return;
        Send(w =>
        {
            w.WriteString("to", to); w.WriteNumber("k", kind);
            w.WriteNumber("ax", R(ax)); w.WriteNumber("ay", R(ay)); w.WriteNumber("vx", R(vx)); w.WriteNumber("vy", R(vy));
            w.WriteNumber("kx", R(kx)); w.WriteNumber("ky", R(ky));
        }, "h");
    }

    // ---------------- 接收 ----------------

    static float F(JsonElement e, string name, float fallback = 0)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetSingle(out float f) || !float.IsFinite(f)) return fallback;
        return Math.Clamp(f, 0f, 1f);
    }
    static int I(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
    static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool B(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    /// <summary>名字来自别人的电脑：去掉控制字符，限制长度。</summary>
    public static string CleanName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "无名小羊";
        var sb = new StringBuilder();
        foreach (char c in s.Trim())
            if (!char.IsControl(c) && c is not ('\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069')) sb.Append(c);
        return sb.Length > 12 ? sb.ToString(0, 12) : sb.ToString();
    }

    void OnPacket(byte[] packet)
    {
        if (_disposed) return;
        byte[] plain = RoomCrypto.Open(_keys.MsgKey, packet);
        if (plain == null) return;                       // 别的房间或别的程序的包
        bool changed = false, greet = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(plain);
            JsonElement m = doc.RootElement;
            string type = S(m, "t"), id = S(m, "id");
            if (type == null || id == null || id.Length != 16 || id == MyId) return;
            long n = m.TryGetProperty("n", out JsonElement nv) && nv.TryGetInt64(out long nn) ? nn : 0;

            lock (_world.Sync)
            {
                // 同一条消息会从局域网和多个中继各到一次；旧的或重复的都丢掉
                lock (_lastSeq)
                {
                    if (_lastSeq.TryGetValue(id, out long last) && n <= last) return;
                    _lastSeq[id] = n;
                }

                Actor a = _world.Find(id);
                if (type == "b")
                {
                    if (a != null) { _world.Actors.Remove(a); changed = true; Trace?.Invoke($"{a.Name} 离开"); }
                    lock (_lastSeq) _lastSeq[id] = long.MaxValue;   // 离开之后迟到的消息不再让他「复活」
                    return;
                }

                if (a == null)
                {
                    // 只有带名字和外观的心跳能让新成员出现
                    if (type != "p" || _world.Actors.Count >= MaxMembers) return;
                    a = new Actor { Id = id, X = F(m, "x", 0.5f), Y = F(m, "y", 0.5f) };
                    a.TX = a.X; a.TY = a.Y;
                    _world.Actors.Add(a);
                    changed = greet = true;
                }
                a.LastSeen = Environment.TickCount64;

                switch (type)
                {
                    case "p":
                    {
                        string name = CleanName(S(m, "nm"));
                        if (a.Name != name) { a.Name = name; changed = true; }
                        if (m.TryGetProperty("lk", out JsonElement lk) && lk.ValueKind == JsonValueKind.Object)
                        {
                            try { a.Look = (lk.Deserialize<Look>() ?? new Look()).Sanitized(); } catch { a.Look ??= new Look(); }
                        }
                        a.Look ??= new Look();
                        if (greet) Trace?.Invoke($"{a.Name} 加入");

                        int anim = I(m, "a");
                        a.Hp = Math.Clamp(I(m, "hp", 100), 0, 100);
                        a.Hunger = Math.Clamp(I(m, "hg", 100), 0, 100);

                        // 站在谁头上：靠心跳同步给后进房间的人，也纠正丢掉的消息
                        string rd = S(m, "rd");
                        if (string.IsNullOrEmpty(rd)) { if (a.RideId != null) World.Dismount(a, F(m, "x", a.X), F(m, "y", a.Y)); }
                        else if (rd != a.RideId) _world.Mount(a, _world.Find(rd));

                        if (a.RideId != null)
                        {
                            bool hold = anim is Anim.Sleep or Anim.Sit;
                            if (hold && a.Anim != anim) a.Play(anim);
                        }
                        else if (a.ChaseId == null && a.Anim != Anim.Hit && a.Anim != Anim.Ram)
                        {
                            float x = F(m, "x", a.X), y = F(m, "y", a.Y);
                            if (World.Distance(a.X, a.Y, x, y) > 0.08f) { a.X = x; a.Y = y; }   // 偏差太大才纠正，小偏差靠各自走到同一个目的地消除
                            a.TX = F(m, "tx", a.TX); a.TY = F(m, "ty", a.TY);
                            a.Moving = B(m, "mv") || World.Distance(a.X, a.Y, a.TX, a.TY) > 0.01f;
                            if (a.Moving) a.Gait = 0;
                            else
                            {
                                a.FaceLeft = B(m, "fl");
                                // 一直保持的动作（睡觉、坐下）靠心跳同步给后进房间的人
                                bool hold = anim is Anim.Sleep or Anim.Sit;
                                if (hold && a.Anim != anim) a.Play(anim);
                                else if (!hold && a.Anim is Anim.Sleep or Anim.Sit && anim == Anim.Idle) a.Play(Anim.Idle);
                            }
                        }
                        break;
                    }
                    case "d":
                        World.Dismount(a, F(m, "x", a.X), F(m, "y", a.Y));
                        if (a.Anim is Anim.Walk or Anim.Run) a.Play(Anim.Idle);
                        Trace?.Invoke($"{a.Name} 从头顶下来");
                        break;
                    case "m":
                        a.ChaseId = null; a.RideId = null;
                        if (World.Distance(a.X, a.Y, F(m, "x", a.X), F(m, "y", a.Y)) > 0.08f) { a.X = F(m, "x", a.X); a.Y = F(m, "y", a.Y); }
                        a.TX = F(m, "tx", a.X); a.TY = F(m, "ty", a.Y);
                        a.Moving = true; a.Gait = 0;
                        Trace?.Invoke($"{a.Name} 走向 ({a.TX:0.00}, {a.TY:0.00})");
                        break;
                    case "a":
                    {
                        int anim = I(m, "a");
                        if (anim is < 0 or >= Anim.Count or Anim.Ram or Anim.Hit or Anim.Hug) break;
                        a.ChaseId = null; a.Moving = false;
                        if (a.RideId == null) { a.X = a.TX = F(m, "x", a.X); a.Y = a.TY = F(m, "y", a.Y); }
                        a.Play(anim);
                        Trace?.Invoke($"{a.Name} 做动作 {anim}");
                        break;
                    }
                    case "c":
                    {
                        string to = S(m, "to");
                        if (to == null || _world.Find(to) == null) break;
                        a.RideId = null;
                        World.StartChase(a, to, I(m, "k") == 1 ? 1 : 0);
                        Trace?.Invoke($"{a.Name} 走向 {_world.Find(to).Name}（{(a.ChaseKind == 0 ? "攻击" : "站头顶")}）");
                        break;
                    }
                    case "h":
                    {
                        Actor victim = _world.Find(S(m, "to"));
                        if (victim == null || victim == a) break;
                        if (I(m, "k") == 1)
                        {
                            // 站到头顶：消息里的对象已经是那一摞最顶上的那只
                            if (_world.Mount(a, victim)) Trace?.Invoke($"{a.Name} 站到了 {victim.Name} 头顶");
                            break;
                        }
                        _world.ApplyHit(a, victim, F(m, "ax", a.X), F(m, "ay", a.Y), F(m, "vx", victim.X), F(m, "vy", victim.Y),
                            F(m, "kx", victim.X), F(m, "ky", victim.Y));
                        Trace?.Invoke($"{a.Name} 撞了 {victim.Name}");
                        break;
                    }
                }
            }
        }
        catch (Exception ex) { Program.Log(ex); }

        if (greet) _lastPresence = 0;   // 有新人：尽快发一次自己的状态，让他马上看到我们
        if (changed) Changed?.Invoke();
    }

    int _lastRelayLive = -1;

    void Housekeeping()
    {
        if (_disposed) return;
        try
        {
            long now = Environment.TickCount64;
            if (now - _lastPresence >= PresenceMs) SendPresence();

            bool changed = false;
            lock (_world.Sync)
                changed = _world.Actors.RemoveAll(a => !a.IsLocal && now - a.LastSeen > TimeoutMs) > 0;
            int live = RelayLive;
            if (live != _lastRelayLive) { _lastRelayLive = live; changed = true; if (live > 0) _lastPresence = 0; }
            if (changed) Changed?.Invoke();
        }
        catch (Exception ex) { Program.Log(ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Send(null, "b");
        _disposed = true;
        _timer.Dispose();
        _lan.Dispose();
        // 给「离开」消息一点时间发到中继，再断开
        Task.Delay(400).ContinueWith(_ => _relay.Dispose());
    }
}
