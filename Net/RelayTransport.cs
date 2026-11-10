using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NBitcoin.Secp256k1;

namespace CloudWallpaper.Net;

/// <summary>
/// 互联网：通过免费的公共 Nostr 中继转发。每个加密包是一条「临时事件」（中继只转发不保存），
/// 带一个由房间码推导的话题标签；同一房间的人订阅同一个话题。不需要自己的服务器，成员之间也看不到彼此的 IP。
/// 中继要求事件有签名，所以每次进房间随机生成一把一次性的签名钥匙；真正的保密靠房间自己的加密。
/// </summary>
internal sealed class RelayTransport : IDisposable
{
    /// <summary>与 lolPing 相同的中继列表（它们用探测工具筛选过：接受临时事件、不限流、长连接稳定）。</summary>
    static readonly string[] Relays =
    {
        "wss://nos.lol", "wss://nostr.mom", "wss://relay.snort.social",
        "wss://relay.agentry.com", "wss://relay.routstr.com", "wss://nostr.rblb.it",
    };
    /// <summary>临时事件区间 20000–29999 里我们自己用的编号。</summary>
    const int EventKind = 24891;

    sealed class Conn
    {
        public ClientWebSocket Ws;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    readonly string _topic;
    readonly ECPrivKey _key;
    readonly string _pubkey;
    readonly CancellationTokenSource _cts = new();
    readonly List<Conn> _conns = new();
    readonly HashSet<string> _seen = new();
    readonly Queue<string> _seenOrder = new();

    public event Action<byte[]> Packet;
    /// <summary>当前连着的中继数量。</summary>
    public int Live { get { lock (_conns) return _conns.Count; } }
    public int Total => Relays.Length;

    public RelayTransport(string topic)
    {
        _topic = topic;
        var secret = new byte[32];
        do RandomNumberGenerator.Fill(secret);
        while (!ECPrivKey.TryCreate(secret, out _key));
        _pubkey = Convert.ToHexString(_key.CreateXOnlyPubKey().ToBytes()).ToLowerInvariant();
    }

    public void Start()
    {
        foreach (string url in Relays) _ = Task.Run(() => RunAsync(url));
    }

    async Task RunAsync(string url)
    {
        CancellationToken ct = _cts.Token;
        int backoff = 2000;
        var buf = new byte[16384];
        while (!ct.IsCancellationRequested)
        {
            var conn = new Conn { Ws = new ClientWebSocket() };
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(10000);
                    await conn.Ws.ConnectAsync(new Uri(url), connectCts.Token);
                }
                long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;
                await SendAsync(conn, $"[\"REQ\",\"room\",{{\"kinds\":[{EventKind}],\"#x\":[\"{_topic}\"],\"since\":{since},\"limit\":0}}]");
                lock (_conns) _conns.Add(conn);
                backoff = 2000;

                using var ms = new MemoryStream();
                while (conn.Ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult r = await conn.Ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                    if (ms.Length > 65536) break;                       // 不正常的中继，断开重连
                    if (!r.EndOfMessage) continue;
                    Handle(ms.GetBuffer().AsSpan(0, (int)ms.Length));
                    ms.SetLength(0);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch { /* 连不上或断线：稍后重试 */ }
            finally
            {
                lock (_conns) _conns.Remove(conn);
                try { conn.Ws.Dispose(); } catch { }
            }
            try { await Task.Delay(backoff, ct); } catch { break; }
            backoff = Math.Min(backoff * 2, 30000);
        }
    }

    static async Task SendAsync(Conn conn, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        await conn.Gate.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(5000);
            await conn.Ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, cts.Token);
        }
        finally { conn.Gate.Release(); }
    }

    bool MarkSeen(string id)
    {
        lock (_seen)
        {
            if (!_seen.Add(id)) return false;
            _seenOrder.Enqueue(id);
            if (_seenOrder.Count > 4000) _seen.Remove(_seenOrder.Dequeue());
            return true;
        }
    }

    void Handle(ReadOnlySpan<byte> json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json.ToArray());
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3 || root[0].GetString() != "EVENT") return;
            JsonElement ev = root[2];
            string id = ev.GetProperty("id").GetString();
            string content = ev.GetProperty("content").GetString();
            if (id == null || content == null || content.Length > 1700 || !MarkSeen(id)) return;   // 同一事件会从多个中继各来一份
            Packet?.Invoke(Convert.FromBase64String(content));
        }
        catch { /* 格式不对的消息直接丢弃 */ }
    }

    /// <summary>把一个加密包发给房间里所有人：签成一条事件，发到每个连着的中继。</summary>
    public void Broadcast(byte[] packet)
    {
        Conn[] conns;
        lock (_conns) conns = _conns.ToArray();
        if (conns.Length == 0) return;

        string content = Convert.ToBase64String(packet);
        long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // NIP-01 规定的序列化形式，它的 SHA-256 就是事件 id
        string serialized = $"[0,\"{_pubkey}\",{createdAt},{EventKind},[[\"x\",\"{_topic}\"]],\"{content}\"]";
        byte[] id = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var sig = new byte[64];
        _key.SignBIP340(id).WriteToSpan(sig);
        string idHex = Convert.ToHexString(id).ToLowerInvariant();
        MarkSeen(idHex);   // 中继会把自己的事件回显回来，不用再处理
        string text = $"[\"EVENT\",{{\"id\":\"{idHex}\",\"pubkey\":\"{_pubkey}\",\"created_at\":{createdAt},\"kind\":{EventKind}," +
                      $"\"tags\":[[\"x\",\"{_topic}\"]],\"content\":\"{content}\",\"sig\":\"{Convert.ToHexString(sig).ToLowerInvariant()}\"}}]";

        foreach (Conn c in conns)
            _ = Task.Run(async () => { try { await SendAsync(c, text); } catch { try { c.Ws.Abort(); } catch { } } });
    }

    public void Dispose()
    {
        _cts.Cancel();
        Conn[] conns;
        lock (_conns) conns = _conns.ToArray();
        foreach (Conn c in conns) try { c.Ws.Abort(); } catch { }
    }
}
