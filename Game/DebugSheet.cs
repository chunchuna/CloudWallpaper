using System.Drawing.Imaging;

namespace CloudWallpaper.Game;

/// <summary>调试用：--sheet &lt;png&gt; 把所有种类 × 动作画成一张对照图。</summary>
internal static class DebugSheet
{
    static readonly string[] AnimNames =
        { "站立", "走", "跑", "打招呼", "跳舞", "睡觉", "吃草", "跳跃", "坐下", "比心", "生气", "撞击", "被撞", "被撞后", "拥抱" };
    static readonly (int Anim, float T)[] Frames =
    {
        (Anim.Idle, 0.5f), (Anim.Walk, 0.14f), (Anim.Run, 0.08f), (Anim.Wave, 1f), (Anim.Dance, 0.75f), (Anim.Sleep, 1f),
        (Anim.Eat, 1f), (Anim.Jump, 0.3f), (Anim.Sit, 1f), (Anim.Love, 1.2f), (Anim.Angry, 0.5f), (Anim.Ram, 0.25f),
        (Anim.Hit, 0.3f), (Anim.Hit, 1.2f), (Anim.Hug, 1f),
    };

    public static void Render(string file)
    {
        var looks = new List<Look>();
        for (int s = 0; s < Look.SpeciesNames.Length; s++) looks.Add(Look.Default(s));
        looks.Add(new Look { S = Look.Sheep, B = 0xFFD7E0, F = 0xF7C9B6, HS = 2, H = 0xFFD24A, A = 2, AC = 0x4D8DF0 });
        looks.Add(new Look { S = Look.Sheep, B = 0x3A3533, F = 0xEFE6D8, HS = 1, A = 5, AC = 0xE8554E });
        looks.Add(new Look { S = Look.Goat, B = 0x5B4636, F = 0x5B4636, HS = 2, H = 0xF1E7D2, A = 3, AC = 0x6CC56A });
        looks.Add(new Look { S = Look.Cow, B = 0xC9A27A, F = 0xF3E9D8, HS = 0, P = 0xFFFFFF, A = 4, AC = 0xFF8FA3 });

        const int cw = 150, ch = 150, scale = 2;
        using var bmp = new Bitmap(cw * Frames.Length, ch * looks.Count + 24, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(0x8F, 0xBF, 0x5A));
            using var font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
            for (int c = 0; c < Frames.Length; c++)
            {
                g.DrawString(AnimNames[c], font, Brushes.White, c * cw + 6, 4);
                for (int r = 0; r < looks.Count; r++)
                {
                    var st = g.Save();
                    g.TranslateTransform(c * cw + cw / 2, 24 + r * ch + ch - 22);
                    g.ScaleTransform(scale * 0.75f, scale * 0.75f);
                    SheepPainter.Draw(g, looks[r], Frames[c].Anim, Frames[c].T, (r & 1) == 1 && c == 1);
                    g.Restore(st);
                }
            }
        }
        bmp.Save(file, ImageFormat.Png);

        // 动作圆盘和攻击/拥抱菜单：放在草地色背景上导出
        using var ui = new Bitmap(640, 400, PixelFormat.Format32bppArgb);
        using var wheel = new Bitmap(360, 360, PixelFormat.Format32bppArgb);
        using var menu = new Bitmap(212, 112, PixelFormat.Format32bppArgb);
        UI.ActionWheel.Render(wheel, looks[0], 1, 1.3f, 1f);
        UI.InteractMenu.Render(menu, "小明", 1, 1f);
        using (Graphics g = Graphics.FromImage(ui))
        {
            g.Clear(Color.FromArgb(0x8F, 0xBF, 0x5A));
            g.DrawImage(wheel, 10, 20);
            g.DrawImage(menu, 410, 120);
        }
        ui.Save(Path.ChangeExtension(file, null) + "_ui.png", ImageFormat.Png);
    }
}
