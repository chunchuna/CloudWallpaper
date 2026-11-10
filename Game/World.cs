namespace CloudWallpaper.Game;

internal sealed class Actor
{
    public string Id, Name;
    public Look Look;
    public bool IsLocal;

    /// <summary>位置和目的地，都是屏幕的 0–1 比例，每个人按自己的分辨率换算。</summary>
    public float X, Y, TX, TY;
    public bool Moving, FaceLeft;
    /// <summary>移动方式：0 走，1 跑（去撞人），2 被撞飞后的滑行。</summary>
    public int Gait;

    public int Anim;
    public float AnimT;

    /// <summary>正在追的角色，以及追上后要做什么（0 攻击，1 站到头顶）。</summary>
    public string ChaseId;
    public int ChaseKind;
    public float ChaseWait;

    /// <summary>正站在谁的头顶（没有则为 null）。站在别人头上的角色不能自己走，跟着最下面的那只移动。</summary>
    public string RideId;
    /// <summary>叠罗汉里的层数：0 是站在地上，1 是站在别人头上，以此类推。每帧由 World 计算。</summary>
    public int Level;

    /// <summary>状态值，0–100。目前不会下降，以后再做。</summary>
    public int Hp = 100, Hunger = 100;

    /// <summary>最后一次收到这个角色消息的时间（Environment.TickCount64）。</summary>
    public long LastSeen;

    public void Play(int anim) { Anim = anim; AnimT = 0; }
}

/// <summary>
/// 房间里所有角色的状态。各端只同步「去哪、做什么」，行走过程由每台电脑自己模拟。
/// 渲染线程、界面线程、网络线程都会访问，统一用 Sync 加锁。
/// </summary>
internal sealed class World
{
    /// <summary>速度按固定的 16:9 画布计算，这样不同分辨率的电脑上走得一样快。</summary>
    public const float Aspect = 16f / 9f;
    const float WalkSpeed = 0.15f, RunSpeed = 0.34f, SlideSpeed = 0.42f;
    public const float Reach = 0.05f;
    /// <summary>叠到这一层及以上的角色会从云里冒出来（画在云的上面）。</summary>
    public const int PierceLevel = 3;

    public readonly object Sync = new();
    public readonly List<Actor> Actors = new();
    public Actor Me;

    public void Reset(string id, string name, Look look, Random rng)
    {
        Actors.Clear();
        Me = new Actor
        {
            Id = id, Name = name, Look = look, IsLocal = true,
            X = 0.35f + (float)rng.NextDouble() * 0.4f, Y = 0.40f + (float)rng.NextDouble() * 0.35f,
        };
        Me.TX = Me.X; Me.TY = Me.Y;
        Actors.Add(Me);
    }

    public void Clear() { Actors.Clear(); Me = null; }

    public Actor Find(string id)
    {
        if (id == null) return null;
        foreach (Actor a in Actors) if (a.Id == id) return a;
        return null;
    }

    public static float Distance(float x1, float y1, float x2, float y2)
    {
        float dx = (x2 - x1) * Aspect, dy = y2 - y1;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ---------------- 叠罗汉 ----------------

    /// <summary>直接站在 a 头上的角色（没有则为 null）。</summary>
    public Actor RiderOf(Actor a)
    {
        foreach (Actor r in Actors) if (r.RideId == a.Id && r != a) return r;
        return null;
    }

    /// <summary>a 所在这一摞最顶上的角色：新来的要站到它头上。</summary>
    public Actor TopOf(Actor a)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            Actor r = RiderOf(a);
            if (r == null) break;
            a = r;
        }
        return a;
    }

    /// <summary>a 是否（直接或隔着几层）站在 below 的上面。用来防止两只互相站成一个圈。</summary>
    public bool IsAbove(Actor a, Actor below)
    {
        for (int guard = 0; guard < 64 && a?.RideId != null; guard++)
        {
            a = Find(a.RideId);
            if (a == below) return true;
        }
        return false;
    }

    /// <summary>让 rider 站到 carrier 头上；会形成圈或对象无效时不做。</summary>
    public bool Mount(Actor rider, Actor carrier)
    {
        if (rider == null || carrier == null || rider == carrier || IsAbove(carrier, rider)) return false;
        rider.RideId = carrier.Id;
        rider.ChaseId = null; rider.Moving = false;
        if (rider.Anim is Anim.Walk or Anim.Run) rider.Play(Anim.Idle);
        return true;
    }

    /// <summary>从头顶下来，落在这一摞旁边。</summary>
    public static void Dismount(Actor a, float x, float y)
    {
        a.RideId = null; a.Level = 0;
        a.X = a.TX = Math.Clamp(x, 0.02f, 0.98f); a.Y = a.TY = Math.Clamp(y, 0.06f, 0.97f);
        a.Moving = false;
    }

    // ---------------- 本地角色的指令 ----------------

    /// <summary>被撞飞翻滚的那一小段时间里不能操作。</summary>
    bool MeBusy => Me == null || (Me.Anim == Anim.Hit && Me.AnimT < 0.9f);

    public bool MeRiding => Me?.RideId != null;

    public bool MoveMe(float x, float y)
    {
        if (MeBusy || MeRiding) return false;     // 站在别人头上时不能自己走
        Me.ChaseId = null;
        Me.TX = Math.Clamp(x, 0.02f, 0.98f); Me.TY = Math.Clamp(y, 0.06f, 0.97f);
        Me.Moving = true; Me.Gait = 0;
        return true;
    }

    /// <summary>本地角色从头顶跳下来，落在旁边。</summary>
    public bool DismountMe()
    {
        if (!MeRiding) return false;
        Dismount(Me, Me.X + (Me.FaceLeft ? -0.035f : 0.035f), Me.Y + 0.015f);
        Me.Play(Anim.Idle);
        return true;
    }

    public bool ActMe(int anim)
    {
        if (MeBusy) return false;
        Me.ChaseId = null; Me.Moving = false;
        Me.TX = Me.X; Me.TY = Me.Y;
        Me.Play(anim);
        return true;
    }

    public bool ChaseMe(string targetId, int kind)
    {
        Actor target = Find(targetId);
        if (MeBusy || MeRiding || target == null || target == Me) return false;
        if (kind == 1 && IsAbove(target, Me)) return false;      // 不能站到自己头上那一摞上去
        StartChase(Me, targetId, kind);
        return true;
    }

    public static void StartChase(Actor a, string targetId, int kind)
    {
        a.ChaseId = targetId; a.ChaseKind = kind; a.ChaseWait = 0;
        a.Moving = true; a.Gait = kind == 0 ? 1 : 0;
    }

    /// <summary>撞击生效的那一刻：所有电脑按同一组数据摆好双方的位置和动作。</summary>
    public void ApplyHit(Actor attacker, Actor victim, float ax, float ay, float vx, float vy, float kx, float ky)
    {
        attacker.ChaseId = null; attacker.Moving = false;
        attacker.X = attacker.TX = ax; attacker.Y = attacker.TY = ay;
        attacker.FaceLeft = vx < ax;
        attacker.Play(Anim.Ram);

        // 被撞的从头顶掉下来飞出去；站在它头上的那些跟着它一起飞
        victim.ChaseId = null; victim.RideId = null; victim.Level = 0;
        victim.X = vx; victim.Y = vy;
        victim.Play(Anim.Hit);
        victim.TX = kx; victim.TY = ky; victim.Moving = true; victim.Gait = 2;
        victim.FaceLeft = ax < vx;          // 脸朝着撞过来的方向，向后飞出去
    }

    /// <summary>推进一帧。返回本地角色刚刚追上的目标（由调用方结算并发消息），没有则为 null。</summary>
    public Actor Update(float dt)
    {
        Actor contact = null;
        foreach (Actor a in Actors)
        {
            a.AnimT += dt;
            if (a.RideId != null)
            {
                // 站在别人头上：自己不走，位置在下面统一跟随
                a.Moving = false; a.ChaseId = null;
                float d = Anim.Duration(a.Anim);
                if ((d > 0 && a.AnimT >= d) || a.Anim is Anim.Walk or Anim.Run) a.Play(Anim.Idle);
                continue;
            }
            a.Level = 0;

            bool chasing = false;
            if (a.ChaseId != null)
            {
                Actor target = Find(a.ChaseId);
                if (target == null || target == a) { a.ChaseId = null; a.Moving = false; }
                else { a.TX = target.X; a.TY = target.Y; a.Moving = true; chasing = true; }
            }

            if (a.Moving)
            {
                float dx = (a.TX - a.X) * Aspect, dy = a.TY - a.Y;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float stop = chasing ? Reach : 0.003f;
                if (dist <= stop)
                {
                    if (chasing)
                    {
                        if (a.IsLocal) contact = Find(a.ChaseId);
                        else if ((a.ChaseWait += dt) > 4f) a.ChaseId = null;   // 对方的消息丢了，不再等
                        if (a.Anim is Anim.Walk or Anim.Run) a.Play(Anim.Idle);
                    }
                    else
                    {
                        a.Moving = false;
                        if (a.Anim is Anim.Walk or Anim.Run) a.Play(Anim.Idle);
                    }
                }
                else
                {
                    float speed = a.Gait == 2 ? SlideSpeed : a.Gait == 1 ? RunSpeed : WalkSpeed;
                    float step = Math.Min(speed * dt, dist - stop * 0.5f);
                    a.X += dx / dist * step / Aspect;
                    a.Y += dy / dist * step;
                    if (a.Gait != 2)
                    {
                        if (MathF.Abs(dx) > 0.004f) a.FaceLeft = dx < 0;
                        int gaitAnim = a.Gait == 1 ? Anim.Run : Anim.Walk;
                        if (a.Anim != gaitAnim) a.Play(gaitAnim);
                    }
                }
            }
            else
            {
                float dur = Anim.Duration(a.Anim);
                if ((dur > 0 && a.AnimT >= dur) || a.Anim is Anim.Walk or Anim.Run) a.Play(Anim.Idle);
            }
        }

        // 叠罗汉：每个站在别人头上的角色跟着最下面那只走，并算出自己在第几层
        foreach (Actor a in Actors)
        {
            if (a.RideId == null) continue;
            Actor root = a;
            int level = 0;
            while (root.RideId != null && level < 64)
            {
                Actor next = Find(root.RideId);
                if (next == null || next == a) { level = -1; break; }   // 下面那只离开了，或者成了圈
                root = next;
                level++;
            }
            if (level <= 0 || level >= 64) { Dismount(a, a.X, a.Y); continue; }
            a.Level = level;
            a.X = a.TX = root.X; a.Y = a.TY = root.Y;
            a.FaceLeft = root.FaceLeft;
        }
        return contact;
    }

    /// <summary>屏幕像素位置上的其他玩家角色（点击判定）。stepPx 是叠罗汉每一层在屏幕上抬高的像素数。</summary>
    public Actor HitTest(float px, float py, int w, int h, float scale, float stepPx)
    {
        Actor best = null;
        foreach (Actor a in Actors)
        {
            if (a.IsLocal) continue;
            float ax = a.X * w, ay = a.Y * h - a.Level * stepPx;
            if (MathF.Abs(px - ax) >= 46 * scale || py <= ay - 96 * scale || py >= ay + 12 * scale) continue;
            // 重叠时选画在最前面的：层数高的优先，其次是靠下的
            if (best == null || a.Level > best.Level || (a.Level == best.Level && a.Y > best.Y)) best = a;
        }
        return best;
    }
}
