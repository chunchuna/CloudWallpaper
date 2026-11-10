using System.Security.Cryptography;
using System.Text;

namespace CloudWallpaper.Net;

internal sealed class RoomKeys
{
    public string Code;
    /// <summary>加密所有房间消息的密钥，只由房间码推导，不出本机。</summary>
    public byte[] MsgKey;
    /// <summary>中继上的话题：64 位十六进制，没有房间码就看不出任何信息。</summary>
    public string Topic;
}

/// <summary>
/// 房间码 → 密钥 → 加密包。做法参考 lolPing：慢哈希让人无法从话题反推房间码，HKDF 给每个用途一把钥匙，
/// 每个包是 [0x01][12 字节随机数][AES-256-GCM 密文 + 16 字节校验]。
/// </summary>
internal static class RoomCrypto
{
    public const int MaxPacket = 1200;
    const byte Format = 0x01;
    const int Nonce = 12, Tag = 16;
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford base32，没有易混的 I L O U

    public static string NewCode()
    {
        Span<byte> rnd = stackalloc byte[10];
        RandomNumberGenerator.Fill(rnd);
        var sb = new StringBuilder("SHEEP-");
        for (int i = 0; i < 10; i++)
        {
            if (i == 5) sb.Append('-');
            sb.Append(Alphabet[rnd[i] & 31]);
        }
        return sb.ToString();
    }

    /// <summary>规整用户输入的房间码；太短则返回 null。自己随便起的房间码也可以用。</summary>
    public static string Normalize(string input)
    {
        if (input == null) return null;
        var sb = new StringBuilder();
        foreach (char c in input.Trim().ToUpperInvariant())
            if (!char.IsWhiteSpace(c) && !char.IsControl(c)) sb.Append(c);
        return sb.Length is >= 4 and <= 40 ? sb.ToString() : null;
    }

    public static RoomKeys Derive(string code)
    {
        byte[] master = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code),
            Encoding.UTF8.GetBytes("cloudwallpaper/room/v1"), 200_000, HashAlgorithmName.SHA256, 32);
        byte[] Sub(string info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, null, Encoding.UTF8.GetBytes(info));
        return new RoomKeys { Code = code, MsgKey = Sub("msg"), Topic = Convert.ToHexString(Sub("topic")).ToLowerInvariant() };
    }

    public static byte[] Seal(byte[] key, byte[] plain)
    {
        var packet = new byte[1 + Nonce + plain.Length + Tag];
        if (packet.Length > MaxPacket) throw new InvalidOperationException("房间消息过大");
        packet[0] = Format;
        RandomNumberGenerator.Fill(packet.AsSpan(1, Nonce));
        using var aes = new AesGcm(key, Tag);
        aes.Encrypt(packet.AsSpan(1, Nonce), plain, packet.AsSpan(1 + Nonce, plain.Length),
            packet.AsSpan(1 + Nonce + plain.Length, Tag), packet.AsSpan(0, 1));
        return packet;
    }

    /// <summary>解出明文；不是用这把钥匙加密的包返回 null。</summary>
    public static byte[] Open(byte[] key, byte[] packet)
    {
        if (packet == null || packet.Length < 1 + Nonce + Tag || packet.Length > MaxPacket || packet[0] != Format) return null;
        int len = packet.Length - 1 - Nonce - Tag;
        var plain = new byte[len];
        try
        {
            using var aes = new AesGcm(key, Tag);
            aes.Decrypt(packet.AsSpan(1, Nonce), packet.AsSpan(1 + Nonce, len), packet.AsSpan(1 + Nonce + len, Tag),
                plain, packet.AsSpan(0, 1));
            return plain;
        }
        catch (CryptographicException) { return null; }
    }
}
