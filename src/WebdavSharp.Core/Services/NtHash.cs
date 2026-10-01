using System.Buffers.Binary;
using System.Text;

namespace WebdavSharp.Core.Services;

/// <summary>
/// NT 哈希（= MD4(UTF-16LE(口令))）与 NTLM 派生密钥。
/// <para>
/// <b>为什么必须存它</b>：NTLM 协议下服务端要验证客户端响应，必须持有 NT 哈希或明文口令
/// （无法用 PBKDF2 这类不可逆哈希完成）。Samba/AD 存的也是 NT 哈希。
/// WebDAV/FTP 仍用 PBKDF2；NT 哈希仅在启用 SMB 登录时需要。
/// </para>
/// <para>
/// <b>安全提示</b>：NT 哈希为无盐 MD4，库泄露可被快速爆破——
/// 因此仅对需要 SMB 登录的用户保留该列。
/// </para>
/// <para>这里自带 MD4 实现（单文件 ~70 行），避免 Core 依赖 SMBLibrary，也便于单测。</para>
/// </summary>
public static class NtHash
{
    /// <summary>计算 NT 哈希（16 字节）。</summary>
    public static byte[] Compute(string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password ?? string.Empty); // UTF-16LE
        return Md4.HashData(bytes);
    }

    /// <summary>计算 NT 哈希并返回大写十六进制（入库格式）。</summary>
    public static string ComputeHex(string password) => Convert.ToHexString(Compute(password));

    /// <summary>解析入库的十六进制 NT 哈希；非法/空返回 null。</summary>
    public static byte[]? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        try
        {
            var bytes = Convert.FromHexString(hex.Trim());
            return bytes.Length == 16 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>NTOWFv2 = HMAC_MD5(NTOWFv1, Uppercase(user) + domain)（MS-NLMP 3.3.2）。</summary>
    public static byte[] NtOwfV2(byte[] ntHash, string user, string domain)
    {
        var suffix = Encoding.Unicode.GetBytes(user.ToUpperInvariant() + domain);
        return HmacMd5(ntHash, suffix);
    }

    /// <summary>NTProofStr = HMAC_MD5(NTOWFv2, serverChallenge + blob)。</summary>
    public static byte[] NtProofStr(byte[] ntOwfV2, byte[] serverChallenge, byte[] blob)
    {
        var data = new byte[serverChallenge.Length + blob.Length];
        Buffer.BlockCopy(serverChallenge, 0, data, 0, serverChallenge.Length);
        Buffer.BlockCopy(blob, 0, data, serverChallenge.Length, blob.Length);
        return HmacMd5(ntOwfV2, data);
    }

    /// <summary>SessionBaseKey = HMAC_MD5(NTOWFv2, NTProofStr)。</summary>
    public static byte[] SessionBaseKey(byte[] ntOwfV2, byte[] ntProofStr) => HmacMd5(ntOwfV2, ntProofStr);

    /// <summary>HMAC-MD5（Keyed-MD5）；MD5 的 HMAC 即 NTLM 规范所用的 HMAC_MD5。</summary>
    public static byte[] HmacMd5(byte[] key, byte[] data)
    {
        using var hmac = new System.Security.Cryptography.HMACMD5(key);
        return hmac.ComputeHash(data);
    }

    /// <summary>定时安全比较（避免通过响应时间侧信道猜哈希）。</summary>
    public static bool FixedTimeEquals(byte[] a, byte[] b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>MD4（RFC 1320）。仅用于 NT 哈希，不用于其它密码学用途。</summary>
    private static class Md4
    {
        public static byte[] HashData(byte[] message)
        {
            uint a = 0x67452301, b = 0xEFCDAB89, c = 0x98BADCFE, d = 0x10325476;

            var padded = Pad(message);
            for (var offset = 0; offset < padded.Length; offset += 64)
            {
                var x = new uint[16];
                for (var i = 0; i < 16; i++)
                    x[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(offset + i * 4, 4));

                var (aa, bb, cc, dd) = (a, b, c, d);

                // Round 1：F(x,y,z) = (x & y) | (~x & z)
                int[] s1 = [3, 7, 11, 19];
                for (var i = 0; i < 16; i++)
                {
                    var k = i;
                    var s = s1[i % 4];
                    a = Rotl(a + ((b & c) | (~b & d)) + x[k], s);
                    (a, b, c, d) = (d, a, b, c);
                }

                // Round 2：G(x,y,z) = (x & y) | (x & z) | (y & z)，常数 0x5A827999
                int[] s2 = [3, 5, 9, 13];
                int[] order2 = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];
                for (var i = 0; i < 16; i++)
                {
                    var k = order2[i];
                    var s = s2[i % 4];
                    a = Rotl(a + ((b & c) | (b & d) | (c & d)) + x[k] + 0x5A827999u, s);
                    (a, b, c, d) = (d, a, b, c);
                }

                // Round 3：H(x,y,z) = x ^ y ^ z，常数 0x6ED9EBA1
                int[] s3 = [3, 9, 11, 15];
                int[] order3 = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];
                for (var i = 0; i < 16; i++)
                {
                    var k = order3[i];
                    var s = s3[i % 4];
                    a = Rotl(a + (b ^ c ^ d) + x[k] + 0x6ED9EBA1u, s);
                    (a, b, c, d) = (d, a, b, c);
                }

                a += aa; b += bb; c += cc; d += dd;
            }

            var result = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), a);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), b);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), c);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), d);
            return result;
        }

        private static uint Rotl(uint v, int n) => (v << n) | (v >> (32 - n));

        /// <summary>补位：0x80 + 零填充至 56 mod 64 + 64 位小端比特长度。</summary>
        private static byte[] Pad(byte[] message)
        {
            var originalLength = message.Length;
            var padLength = 64 - ((originalLength + 9) % 64);
            if (padLength == 64) padLength = 0;
            var total = originalLength + 1 + padLength + 8;

            var buffer = new byte[total];
            Buffer.BlockCopy(message, 0, buffer, 0, originalLength);
            buffer[originalLength] = 0x80;
            var bitLength = (ulong)originalLength * 8;
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(total - 8, 8), bitLength);
            return buffer;
        }
    }
}
