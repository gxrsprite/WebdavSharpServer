using System.Text;
using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>
/// NT 哈希与 NTLM 派生值单测。用**公开标准测试向量**验证，
/// 而不是自证——MD4/NTLM 实现错了会直接导致 Windows 客户端登录失败，必须靠权威向量锁定。
/// </summary>
public class NtHashTests
{
    [Theory]
    [InlineData("", "31D6CFE0D16AE931B73C59D7E0C089C0")]                 // RFC 1320 / 通用 NT 空口令
    [InlineData("password", "8846F7EAEE8FB117AD06BDD830B7586C")]       // 经典 NT 向量
    [InlineData("123456", "32ED87BDB5FDC5E9CBA88547376818D4")]
    public void NtHash_MatchesKnownVectors(string password, string expectedHex)
    {
        Assert.Equal(expectedHex, NtHash.ComputeHex(password), ignoreCase: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("password")]
    [InlineData("admin")]
    [InlineData("P@ssw0rd!")]
    [InlineData("correct horse battery staple")]
    [InlineData("中文口令🧪")]
    public void NtHash_MatchesIndependentImplementation(string password)
    {
        // 与 SMBLibrary 自带的另一份 NT 哈希实现交叉验证（不同代码、同规范）：
        // 前面的公开向量锁定正确性，这条锁定“实现与线上库一致”（含 Unicode 编码细节）。
        var expected = SMBLibrary.Authentication.NTLM.NTLMCryptography.NTOWFv1(password);
        Assert.Equal(expected, NtHash.Compute(password));
    }

    [Fact]
    public void Md4_MatchesRfc1320Vectors()
    {
        // 通过 NT 哈希间接验证 MD4：MD4("") 的 ASCII 向量是 31D6CFE0D16AE931B73C59D7E0C089C0，
        // 与 NT 空口令一致（空字符串 UTF-16LE 也是空字节序列）—— 这条能真正锁定 MD4 实现正确。
        Assert.Equal("31D6CFE0D16AE931B73C59D7E0C089C0", NtHash.ComputeHex(""));
    }

    [Fact]
    public void ParseHex_ValidAndInvalid()
    {
        Assert.NotNull(NtHash.ParseHex("8846F7EAEE8FB117AD06BDD830B7586C"));
        Assert.NotNull(NtHash.ParseHex("8846f7eaee8fb117ad06bd d830b7586c".Replace(" ", "")));
        Assert.Null(NtHash.ParseHex(null));
        Assert.Null(NtHash.ParseHex(""));
        Assert.Null(NtHash.ParseHex("ZZZZ"));
        Assert.Null(NtHash.ParseHex("00")); // 长度不足 16 字节
    }

    [Fact]
    public void NtOwfV2_IsDeterministicAndCaseInsensitiveOnUser()
    {
        var nt = NtHash.Compute("password");
        var a = NtHash.NtOwfV2(nt, "alice", "WORKGROUP");
        var b = NtHash.NtOwfV2(nt, "ALICE", "WORKGROUP"); // 用户名大写化
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);

        // 域不同必须产生不同密钥
        var c = NtHash.NtOwfV2(nt, "alice", "OTHER");
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void HmacMd5_MatchesKnownVector()
    {
        // RFC 2104 测试向量：key=0x0b*16, data="Hi There" -> 9294727A3638BB1C13F48EF8158BFC9D
        var key = Enumerable.Repeat((byte)0x0b, 16).ToArray();
        var data = Encoding.ASCII.GetBytes("Hi There");
        Assert.Equal("9294727A3638BB1C13F48EF8158BFC9D",
            Convert.ToHexString(NtHash.HmacMd5(key, data)), ignoreCase: true);
    }

    [Fact]
    public void FixedTimeEquals_Behaves()
    {
        var a = NtHash.Compute("x");
        Assert.True(NtHash.FixedTimeEquals(a, a));
        Assert.False(NtHash.FixedTimeEquals(a, NtHash.Compute("y")));
    }
}
