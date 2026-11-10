using System.Text.Json;
using CloudWallpaper.Game;

namespace CloudWallpaper;

internal sealed class CharacterDef
{
    public string Name { get; set; } = "小羊";
    public Look Look { get; set; } = new();
}

/// <summary>用户设置，保存在 %AppData%\CloudWallpaper\settings.json，渲染线程每帧读取，修改即时生效。</summary>
internal static class Settings
{
    public const int ThemeSky = 0, ThemeGrass = 1;

    /// <summary>当前主题：0 = 天空，1 = 草原（默认）。联机时固定显示草原。</summary>
    public static volatile int Theme = ThemeGrass;
    /// <summary>天空主题样式：false = 图标所在位置有云，true = 没有图标的地方有云。</summary>
    public static volatile bool Invert;

    // ---- 主题的详细配置 ----
    /// <summary>云朵是否流动（边缘翻涌、小云飘移）。</summary>
    public static volatile bool CloudMotion = true;
    /// <summary>是否生成不在图标下的小云（草原上自由飘动的云、天空里远处的薄云）。</summary>
    public static volatile bool ExtraClouds = true;
    /// <summary>草原上云是否在地面投下影子。</summary>
    public static volatile bool Shadows = true;
    /// <summary>动物在桌面上的大小倍数，1 为默认。</summary>
    public static volatile float AnimalScale = 1f;
    public const float AnimalScaleMin = 0.5f, AnimalScaleMax = 3f;
    /// <summary>帧率上限；0 表示跟随显示器刷新率。</summary>
    public static volatile int FpsLimit;
    public const int FpsMin = 10, FpsMax = 1000;

    /// <summary>是否已经提示过托盘图标的位置（只在第一次使用时提示）。</summary>
    public static bool TrayTipShown;

    /// <summary>联机时显示的名字；输入一次后记住。</summary>
    public static string PlayerName = "";
    /// <summary>上次使用的房间码。</summary>
    public static string LastRoom = "";
    /// <summary>捏好的角色，至少有一个。</summary>
    public static List<CharacterDef> Characters = new() { new CharacterDef() };
    /// <summary>联机使用的角色下标。</summary>
    public static int Active;

    public static CharacterDef ActiveCharacter => Characters[Math.Clamp(Active, 0, Characters.Count - 1)];

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloudWallpaper", "settings.json");

    sealed class Data
    {
        public int Theme { get; set; } = ThemeGrass;
        public bool Invert { get; set; }
        public bool CloudMotion { get; set; } = true;
        public bool ExtraClouds { get; set; } = true;
        public bool Shadows { get; set; } = true;
        public float AnimalScale { get; set; } = 1f;
        public int FpsLimit { get; set; }
        public bool TrayTipShown { get; set; }
        public string PlayerName { get; set; }
        public string LastRoom { get; set; }
        public List<CharacterDef> Characters { get; set; }
        public int Active { get; set; }
        /// <summary>设置文件版本：1 起默认主题改为草原。</summary>
        public int Version { get; set; }
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            Data d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
            if (d == null) return;
            // 旧版设置文件（Version 0）统一切到新的默认主题：草原
            Theme = d.Version >= 1 && d.Theme == ThemeSky ? ThemeSky : ThemeGrass;
            Invert = d.Invert;
            CloudMotion = d.CloudMotion; ExtraClouds = d.ExtraClouds; Shadows = d.Shadows;
            AnimalScale = float.IsFinite(d.AnimalScale) ? Math.Clamp(d.AnimalScale, AnimalScaleMin, AnimalScaleMax) : 1f;
            FpsLimit = d.FpsLimit <= 0 ? 0 : Math.Clamp(d.FpsLimit, FpsMin, FpsMax);
            TrayTipShown = d.TrayTipShown;
            PlayerName = (d.PlayerName ?? "").Trim();
            LastRoom = d.LastRoom ?? "";
            if (d.Characters != null && d.Characters.Count > 0)
            {
                Characters = d.Characters;
                foreach (CharacterDef c in Characters)
                {
                    c.Name = string.IsNullOrWhiteSpace(c.Name) ? "小羊" : c.Name.Trim();
                    c.Look = (c.Look ?? new Look()).Sanitized();
                }
            }
            Active = Math.Clamp(d.Active, 0, Characters.Count - 1);
        }
        catch (Exception ex) { Program.Log(ex); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var d = new Data
            {
                Theme = Theme, Invert = Invert, CloudMotion = CloudMotion, ExtraClouds = ExtraClouds, Shadows = Shadows,
                AnimalScale = AnimalScale, FpsLimit = FpsLimit, TrayTipShown = TrayTipShown, PlayerName = PlayerName, LastRoom = LastRoom,
                Characters = Characters, Active = Active, Version = 1,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Program.Log(ex); }
    }
}
