using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace CloudWallpaper;

/// <summary>
/// 自动更新。发布者把两个文件放到任意一个能用网址直接下载的位置（对象存储、自己的网站、代码托管的发行页等）：
///   latest.json         {"version":"1.2.0","file":"CloudWallpaper.exe","sha256":"…","size":123,"notes":"…"}
///   CloudWallpaper.exe  新版本的程序（file 可以是相对 latest.json 的路径，也可以是完整网址）
/// 程序启动后以及之后每隔几小时读取 latest.json；发现版本更高就下载、核对 SHA-256、替换自己：
/// 正在运行的 exe 不能覆盖但可以改名，所以把自己改名为 .old，把新文件放到原来的位置，下次启动（或立即重启）就是新版本。
/// 清单网址在编译时写进程序（见 CloudWallpaper.csproj 的 UpdateUrl），没填则不检查。
/// </summary>
internal static class Updater
{
    const int MaxDownloadBytes = 400 * 1024 * 1024;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    static readonly SemaphoreSlim Gate = new(1, 1);
    static System.Threading.Timer _timer;

    public static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    /// <summary>清单网址。只接受 https（本机回环地址允许 http，测试用）。</summary>
    public static readonly string ManifestUrl = ReadUrl();

    /// <summary>给界面看的一句话状态。</summary>
    public static string Status { get; private set; } = "";
    /// <summary>新版本已经替换好，重启后生效。</summary>
    public static bool RestartPending { get; private set; }
    /// <summary>状态变化（可能在任意线程触发）。</summary>
    public static event Action Changed;

    public static bool Enabled => ManifestUrl != null;

    static string ReadUrl()
    {
        string url = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateUrl")?.Value?.Trim();
        return IsAllowed(url) ? url : null;
    }

    static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri u) && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    static void Set(string status) { Status = status; Changed?.Invoke(); }

    /// <summary>启动后调用一次：清理上次更新留下的旧文件，然后定时检查。</summary>
    public static void Start()
    {
        try
        {
            string old = Environment.ProcessPath + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* 旧进程可能还没退出干净，下次再删 */ }

        // 重启要用到的代码现在就加载好：程序是单文件的，更新时会把自己改名，
        // 改名之后再去按需加载还没用过的程序集可能失败
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(typeof(Updater).GetMethod(nameof(Restart)).MethodHandle);
            using Process self = Process.GetCurrentProcess();
            _ = new ProcessStartInfo(Environment.ProcessPath ?? "");
        }
        catch (Exception ex) { Program.Log(ex); }

        if (!Enabled) { Status = "未设置更新地址"; return; }
        Status = "已是最新版本";
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromHours(4));
    }

    /// <summary>检查并安装更新。返回是否装好了新版本。</summary>
    public static async Task<bool> CheckAsync()
    {
        if (!Enabled || RestartPending) return RestartPending;
        if (!await Gate.WaitAsync(0)) return false;      // 已经有一次检查在进行
        try
        {
            Set("正在检查更新…");
            string json = await Http.GetStringAsync(ManifestUrl + (ManifestUrl.Contains('?') ? "&" : "?") + "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement m = doc.RootElement;
            if (!Version.TryParse(m.GetProperty("version").GetString(), out Version latest)) throw new Exception("清单里的版本号无效");
            if (latest <= Current) { Set($"已是最新版本（{CurrentText}）"); return false; }

            string sha = m.GetProperty("sha256").GetString()?.Trim().ToLowerInvariant();
            string file = m.GetProperty("file").GetString();
            var fileUri = new Uri(new Uri(ManifestUrl), file);
            if (sha == null || sha.Length != 64 || !IsAllowed(fileUri.AbsoluteUri)) throw new Exception("清单内容无效");

            string exe = Environment.ProcessPath ?? throw new Exception("找不到自身路径");
            string fresh = exe + ".new";
            Set($"正在下载新版本 {latest}…");
            await DownloadAsync(fileUri, fresh);

            // 下载的内容必须和清单里写的完全一致，否则丢弃
            string actual;
            await using (FileStream fs = File.OpenRead(fresh)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
            if (actual != sha) { File.Delete(fresh); throw new Exception("下载的文件校验不一致"); }

            // 换上新文件：运行中的 exe 可以改名，不能覆盖
            string old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(fresh, exe); }
            catch { File.Move(old, exe); throw; }       // 换不上就把原来的放回去

            RestartPending = true;
            Set($"新版本 {latest} 已就绪，重启后生效");
            return true;
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            Set("检查更新失败：" + ex.Message);
            return false;
        }
        finally { Gate.Release(); }
    }

    static async Task DownloadAsync(Uri uri, string path)
    {
        using HttpResponseMessage resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using Stream src = await resp.Content.ReadAsStreamAsync();
        await using FileStream dst = File.Create(path);
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            total += n;
            if (total > MaxDownloadBytes) throw new Exception("更新文件过大");
            await dst.WriteAsync(buf.AsMemory(0, n));
        }
    }

    /// <summary>启动新版本并让当前进程退出。只在界面线程调用。</summary>
    public static void Restart(bool showWindow)
    {
        try
        {
            Program.Trace("restart: starting new process");
            Process.Start(new ProcessStartInfo(Environment.ProcessPath, showWindow ? "--after-update" : "--after-update --hidden") { UseShellExecute = false });
            Application.Exit();
            Program.Trace("restart: Application.Exit returned");
        }
        catch (Exception ex) { Program.Log(ex); }
    }
}
