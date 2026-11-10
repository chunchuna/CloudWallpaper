namespace CloudWallpaper.Game;

/// <summary>角色外观。颜色为 0xRRGGBB。属性名很短是因为它会原样放进联机消息里。</summary>
internal sealed class Look
{
    public const int Sheep = 0, Goat = 1, Cow = 2, Bull = 3;
    public static readonly string[] SpeciesNames = { "绵羊", "山羊", "奶牛", "公牛" };
    public static readonly string[] HornNames = { "无角", "小角", "卷角", "长角" };
    public static readonly string[] AccessoryNames = { "无", "铃铛", "蝴蝶结", "围巾", "小花", "草帽" };

    /// <summary>种类。</summary>
    public int S { get; set; }
    /// <summary>毛色 / 体色。</summary>
    public int B { get; set; } = 0xFDFBF5;
    /// <summary>脸和腿的颜色。</summary>
    public int F { get; set; } = 0x4A3F3A;
    /// <summary>角的样式。</summary>
    public int HS { get; set; }
    /// <summary>角的颜色。</summary>
    public int H { get; set; } = 0xE2C48C;
    /// <summary>花纹颜色（奶牛、公牛的斑块）。</summary>
    public int P { get; set; } = 0x3A3533;
    /// <summary>饰品。</summary>
    public int A { get; set; }
    /// <summary>饰品颜色。</summary>
    public int AC { get; set; } = 0xE8554E;

    public Look Clone() => (Look)MemberwiseClone();

    /// <summary>联机收到的外观要先规整，避免越界值。</summary>
    public Look Sanitized()
    {
        Look l = Clone();
        l.S = Math.Clamp(l.S, 0, SpeciesNames.Length - 1);
        l.HS = Math.Clamp(l.HS, 0, HornNames.Length - 1);
        l.A = Math.Clamp(l.A, 0, AccessoryNames.Length - 1);
        l.B &= 0xFFFFFF; l.F &= 0xFFFFFF; l.H &= 0xFFFFFF; l.P &= 0xFFFFFF; l.AC &= 0xFFFFFF;
        return l;
    }

    /// <summary>每个种类的默认长相。</summary>
    public static Look Default(int species) => species switch
    {
        Goat => new Look { S = Goat, B = 0xEFE6D8, F = 0xEFE6D8, HS = 3, H = 0x8A7560 },
        Cow => new Look { S = Cow, B = 0xFFFFFF, F = 0xFFFFFF, HS = 1, H = 0xE9DDC6, P = 0x2F2B2A, A = 1, AC = 0xD9534F },
        Bull => new Look { S = Bull, B = 0x8A5A3C, F = 0x8A5A3C, HS = 3, H = 0xF1E7D2, P = 0x6B4129 },
        _ => new Look { S = Sheep },
    };

    public static Look Random(Random rng)
    {
        Look l = Default(rng.Next(SpeciesNames.Length));
        l.HS = rng.Next(HornNames.Length);
        l.A = rng.Next(AccessoryNames.Length);
        l.AC = Palette.Bright[rng.Next(Palette.Bright.Length)];
        if (rng.Next(2) == 0) l.B = Palette.Body[rng.Next(Palette.Body.Length)];
        return l;
    }
}

internal static class Palette
{
    public static readonly int[] Body =
    {
        0xFDFBF5, 0xF3E9D8, 0xE8D2B0, 0xC9A27A, 0x8A5A3C, 0x5B4636, 0x3A3533, 0xBFC3C9,
        0xFFD7E0, 0xFFE9A8, 0xCDEBFF, 0xD6F2C8, 0xE2D4FF,
    };
    public static readonly int[] Face =
    {
        0x4A3F3A, 0x2B2726, 0x7A5C48, 0xC9A27A, 0xEFE6D8, 0xFFFFFF, 0xF7C9B6, 0xBFC3C9, 0x8A5A3C,
    };
    public static readonly int[] Horn =
    {
        0xE2C48C, 0xF1E7D2, 0x8A7560, 0x4A3F3A, 0xFFD24A, 0xFF8FA3, 0x8FD3FF, 0xB69CFF, 0xFFFFFF,
    };
    public static readonly int[] Spot =
    {
        0x3A3533, 0x2F2B2A, 0x6B4129, 0xA5683A, 0xBFC3C9, 0xFFFFFF, 0xF2A6B3, 0x7FB2E5,
    };
    public static readonly int[] Bright =
    {
        0xE8554E, 0xFF8FA3, 0xFFB03A, 0xFFD24A, 0x6CC56A, 0x3FB6C6, 0x4D8DF0, 0x8E6CF0, 0xFFFFFF, 0x3A3533,
    };
}

/// <summary>角色动作。编号会出现在联机消息里，只能往后追加。</summary>
internal static class Anim
{
    public const int Idle = 0, Walk = 1, Run = 2, Wave = 3, Dance = 4, Sleep = 5, Eat = 6, Jump = 7,
        Sit = 8, Love = 9, Angry = 10, Ram = 11, Hit = 12, Hug = 13;
    public const int Count = 14;

    /// <summary>动作持续时间（秒）；0 表示一直保持，直到被别的动作打断。</summary>
    public static float Duration(int anim) => anim switch
    {
        Wave => 2.6f, Dance => 6f, Eat => 4.5f, Jump => 2.25f, Love => 3.2f, Angry => 3f,
        Ram => 0.55f, Hit => 1.6f, Hug => 2.8f,
        _ => 0f,
    };

    /// <summary>动作圆盘上的选项，顺序即扇区顺序（从正上方顺时针）。</summary>
    public static readonly (int Anim, string Label)[] Wheel =
    {
        (Wave, "打招呼"), (Dance, "跳舞"), (Jump, "跳跃"), (Love, "比心"),
        (Sleep, "睡觉"), (Sit, "坐下"), (Eat, "吃草"), (Angry, "生气"),
    };
}
