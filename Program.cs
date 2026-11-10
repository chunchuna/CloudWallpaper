namespace CloudWallpaper;

internal static class Program
{
    /// <summary>--nopause：桌面被全屏窗口遮挡时也继续渲染（调试用）。</summary>
    public static bool NoPause;
    /// <summary>--hidden：启动时只应用壁纸，不显示窗口。</summary>
    public static bool Hidden;
    /// <summary>--page N：菜单打开时显示第 N 页（调试用）。</summary>
    public static int OpenPage;
    /// <summary>环境变量 CW_THEME=0/1、CW_INVERT=1：只在本次运行里换主题，不改动设置（截图用）。</summary>
    public static readonly int ThemeOverride = int.TryParse(Environment.GetEnvironmentVariable("CW_THEME"), out int th) ? th : -1;
    public static readonly bool InvertOverride = Environment.GetEnvironmentVariable("CW_INVERT") == "1";
    /// <summary>--join 指定的房间码。</summary>
    public static string AutoJoin;
    /// <summary>环境变量 CW_NOLAN=1：不使用局域网，只走中继（测试互联网通路用）。</summary>
    public static readonly bool NoLan = Environment.GetEnvironmentVariable("CW_NOLAN") == "1";

    [STAThread]
    static void Main(string[] args)
    {
        NoPause = args.Contains("--nopause");
        Hidden = args.Contains("--hidden");
        int page = Array.IndexOf(args, "--page");
        if (page >= 0 && page + 1 < args.Length) int.TryParse(args[page + 1], out OpenPage);
        int sheet = Array.IndexOf(args, "--sheet");
        if (sheet >= 0 && sheet + 1 < args.Length) { Game.DebugSheet.Render(args[sheet + 1]); return; }

        // --bot 房间码 [名字] [日志文件]：测试用的自动角色，不显示壁纸
        int bot = Array.IndexOf(args, "--bot");
        if (bot >= 0 && bot + 1 < args.Length)
        {
            string code = Net.RoomCrypto.Normalize(args[bot + 1]);
            if (code == null) return;
            string name = bot + 2 < args.Length ? args[bot + 2] : "机器羊";
            string log = bot + 3 < args.Length ? args[bot + 3] : Path.Combine(Path.GetTempPath(), "cw_bot.log");
            try { Game.Session.RunBot(code, name, log); } catch (Exception ex) { Log(ex); }
            return;
        }
        // --join 房间码：启动后自动进入房间（测试用）
        int join = Array.IndexOf(args, "--join");
        if (join >= 0 && join + 1 < args.Length) AutoJoin = Net.RoomCrypto.Normalize(args[join + 1]);

        // 只允许运行一个。自动更新重启时（--after-update）旧进程还在退出，等它一会儿
        using var mutex = new Mutex(false, "CloudWallpaper.SingleInstance");
        bool got;
        try { got = mutex.WaitOne(args.Contains("--after-update") ? 15000 : 0); }
        catch (AbandonedMutexException) { got = true; }   // 上一个进程被强行结束
        if (!got) return;   // 已经在运行

        // 任何没被接住的异常都记到 error.log，而不是弹出系统的错误对话框或悄悄卡住
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log(ex); };

        ApplicationConfiguration.Initialize();
        Settings.Load();
        Updater.Start();
        Trace("start " + string.Join(" ", args) + " v" + Updater.CurrentText);
        try { Application.Run(new WallpaperForm()); }
        catch (Exception ex) { Log(ex); }
        finally { try { mutex.ReleaseMutex(); } catch { } }
        Trace("main returns");
    }

    static readonly string TraceFile = Environment.GetEnvironmentVariable("CW_TRACE") == "1" ? Path.Combine(Path.GetTempPath(), "cw_trace.log") : null;

    /// <summary>调试：环境变量 CW_TRACE=1 时把关键步骤写到临时目录的 cw_trace.log。</summary>
    public static void Trace(string step)
    {
        if (TraceFile == null) return;
        try { File.AppendAllText(TraceFile, $"[{DateTime.Now:HH:mm:ss.fff}] pid={Environment.ProcessId} {step}\r\n"); } catch { }
    }

    public static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n");
        }
        catch { }
    }
}
