using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CloudWallpaper.Net;

/// <summary>
/// 局域网：一个 UDP 端口，所有包都广播出去。没有连接，丢包或换网络都会自己恢复。
/// 流量很小（每人每秒几个不到 1 KB 的包），所以不区分广播和单播。
/// </summary>
internal sealed class LanTransport : IDisposable
{
    public const int Port = 47480;

    readonly CancellationTokenSource _cts = new();
    UdpClient _udp;
    IPEndPoint[] _targets = Array.Empty<IPEndPoint>();
    long _targetsAt;

    public event Action<byte[]> Packet;
    public bool Ok { get; private set; }

    public void Start()
    {
        if (Program.NoLan) return;
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            // 允许同一台电脑上开多个实例（测试用），广播包会送到每一个
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            _udp.EnableBroadcast = true;
            Ok = true;
            _ = ReceiveLoop();
        }
        catch (Exception ex) { Program.Log(ex); Ok = false; }
    }

    async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult r = await _udp.ReceiveAsync(_cts.Token);
                if (r.Buffer.Length <= RoomCrypto.MaxPacket) Packet?.Invoke(r.Buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { /* Windows 会把 ICMP 不可达当成接收错误，忽略 */ }
            catch (Exception ex) { Program.Log(ex); }
        }
    }

    /// <summary>每块网卡的定向广播地址：Windows 的 255.255.255.255 只会从一块网卡发出去。</summary>
    static IPEndPoint[] BroadcastTargets()
    {
        var set = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                    byte[] ip = u.Address.GetAddressBytes(), mask = u.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                    set.Add(new IPAddress(ip));
                }
            }
        }
        catch { }
        return set.Select(a => new IPEndPoint(a, Port)).ToArray();
    }

    public void Broadcast(byte[] packet)
    {
        if (!Ok) return;
        long now = Environment.TickCount64;
        if (now - _targetsAt > 5000 || _targets.Length == 0) { _targets = BroadcastTargets(); _targetsAt = now; }
        foreach (IPEndPoint ep in _targets)
        {
            try { _udp.Send(packet, packet.Length, ep); }
            catch { /* 网卡不可用等：丢一个包没关系 */ }
        }
    }

    public void Dispose()
    {
        Ok = false;
        _cts.Cancel();
        try { _udp?.Dispose(); } catch { }
    }
}
