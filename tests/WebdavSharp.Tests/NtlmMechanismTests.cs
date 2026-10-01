using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;
using WebdavSharp.Server.Smb;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>
/// SMB/NTLM 认证机制端到端单测（**不需要 445 端口**）。
/// <para>
/// 本机 445/139 被 Windows 自带文件共享占用，无法起真实 SMB 服务做联调；
/// 这里改为在测试内**模拟 NTLM 客户端握手**：
/// 用已知口令按 MS-NLMP 算出 NTLMv2 响应，交给我们的机制验证，
/// 从而覆盖「用存储的 NT 哈希验证响应」这条核心路径。
/// </para>
/// </summary>
public class NtlmMechanismTests
{
    private const string Password = "P@ssw0rd!";

    private static (DavNtlmMechanism Mechanism, GSSProvider Gss) NewMechanism(string password = Password,
        bool enabled = true, string username = "alice")
    {
        var fsql = new FreeSql.FreeSqlBuilder()
            .UseConnectionString(FreeSql.DataType.Sqlite, "Data Source=:memory:")
            .Build();
        fsql.CodeFirst.SyncStructure<DavUser>();
        fsql.CodeFirst.SyncStructure<DavRole>();
        fsql.CodeFirst.SyncStructure<DavUserRole>();
        fsql.CodeFirst.SyncStructure<DavMount>();
        fsql.CodeFirst.SyncStructure<DavRule>();

        var hasher = new PasswordHasher();
        fsql.Insert(new DavUser
        {
            Username = username,
            IsEnabled = enabled,
            PasswordHash = hasher.Hash(password),
            NtHash = NtHash.ComputeHex(password),
        }).ExecuteAffrows();

        var opts = Options.Create(new DavServerOptions { Directory = Path.GetTempPath() });
        var access = new DavAccessService(fsql, opts, hasher);
        var mech = new DavNtlmMechanism(access, NullLogger<DavNtlmMechanism>.Instance);
        return (mech, new GSSProvider(mech));
    }

    /// <summary>模拟 Windows 客户端：按 MS-NLMP 生成 AuthenticateMessage 字节。</summary>
    private static byte[] BuildClientAuthenticate(
        byte[] serverChallenge, string user, string domain, string password, byte[]? clientChallenge = null)
    {
        var ntHash = NtHash.Compute(password);
        var ntOwfV2 = NtHash.NtOwfV2(ntHash, user, domain);

        // blob = NTLMv2_CLIENT_CHALLENGE 的简化版：固定 8 字节 client challenge + 时间戳等占位
        var blob = new byte[8 + 8 + 8];
        (clientChallenge ?? new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).CopyTo(blob, 0);
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(blob, 8);

        var proof = NtHash.NtProofStr(ntOwfV2, serverChallenge, blob);
        var ntResponse = new byte[proof.Length + blob.Length];
        proof.CopyTo(ntResponse, 0);
        blob.CopyTo(ntResponse, proof.Length);

        var auth = new AuthenticateMessage
        {
            Signature = "NTLMSSP\0",
            MessageType = MessageTypeName.Authenticate,
            DomainName = domain,
            UserName = user,
            WorkStation = "TESTPC",
            NegotiateFlags = NegotiateFlags.UnicodeEncoding | NegotiateFlags.ExtendedSessionSecurity,
            NtChallengeResponse = ntResponse,
            LmChallengeResponse = [],
        };
        return auth.GetBytes();
    }

    private static byte[] BuildNegotiate()
    {
        var negotiate = new NegotiateMessage
        {
            Signature = "NTLMSSP\0",
            MessageType = MessageTypeName.Negotiate,
            DomainName = "WORKGROUP",
            Workstation = "TESTPC",
            NegotiateFlags = NegotiateFlags.UnicodeEncoding
                | NegotiateFlags.NTLMSessionSecurity
                | NegotiateFlags.ExtendedSessionSecurity,
        };
        return negotiate.GetBytes();
    }

    private static byte[] ServerChallengeFrom(byte[] challengeBytes)
        => new ChallengeMessage(challengeBytes).ServerChallenge;

    [Fact]
    public void CorrectPassword_IsAccepted()
    {
        var (mech, _) = NewMechanism();

        var status = mech.GetChallengeMessage(out var ctx, BuildNegotiate(), out var challengeBytes);
        // 发 Challenge 后必须返回 CONTINUE_NEEDED（还需要客户端的 Authenticate）；
        // 若返回 SUCCESS，服务端会跳过 Authenticate（联调中真实踩过，见 DavNtlmMechanism 注释）。
        Assert.Equal(NTStatus.SEC_I_CONTINUE_NEEDED, status);

        var parsedChallenge = new ChallengeMessage(challengeBytes);
        Assert.Equal((byte)0x0F, parsedChallenge.Version.NTLMRevisionCurrent);
        Assert.False(string.IsNullOrWhiteSpace(parsedChallenge.TargetName));
        var serverChallenge = parsedChallenge.ServerChallenge;
        var authBytes = BuildClientAuthenticate(serverChallenge, "alice", "WORKGROUP", Password);

        Assert.Equal(NTStatus.STATUS_SUCCESS, mech.Authenticate(ctx, authBytes));
        Assert.Equal("alice", mech.GetContextAttribute(ctx, GSSAttributeName.UserName));
        Assert.NotNull(mech.GetContextAttribute(ctx, GSSAttributeName.SessionKey));
    }

    [Fact]
    public void KeyExchange_ReturnsExportedSessionKey()
    {
        // 模拟要求密钥交换的客户端（如部分 Java/Android 客户端）：
        // 送 EncryptedRandomSessionKey = RC4(SessionBaseKey, 随机16字节），
        // 服务端必须解密并返回它作为 SessionKey（否则客户端报
        // "Server does not support extended NTLMv2 key exchange" 或后续签名对不上）。
        var (mech, _) = NewMechanism();
        mech.GetChallengeMessage(out var ctx, BuildNegotiate(), out var challengeBytes);
        var serverChallenge = ServerChallengeFrom(challengeBytes);

        var ntHash = NtHash.Compute(Password);
        var ntOwfV2 = NtHash.NtOwfV2(ntHash, "alice", "WORKGROUP");
        var blob = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 };
        var proof = NtHash.NtProofStr(ntOwfV2, serverChallenge, blob);
        var sessionBaseKey = NtHash.SessionBaseKey(ntOwfV2, proof);
        var clientRandomKey = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var encryptedKey = System.Security.Cryptography.RC4.Encrypt(sessionBaseKey, clientRandomKey);

        var ntResponse = new byte[proof.Length + blob.Length];
        proof.CopyTo(ntResponse, 0);
        blob.CopyTo(ntResponse, proof.Length);
        var auth = new AuthenticateMessage
        {
            Signature = "NTLMSSP\0",
            MessageType = MessageTypeName.Authenticate,
            DomainName = "WORKGROUP",
            UserName = "alice",
            WorkStation = "TESTPC",
            NegotiateFlags = NegotiateFlags.UnicodeEncoding
                | NegotiateFlags.ExtendedSessionSecurity
                | NegotiateFlags.KeyExchange,
            NtChallengeResponse = ntResponse,
            LmChallengeResponse = [],
            EncryptedRandomSessionKey = encryptedKey,
        };

        Assert.Equal(NTStatus.STATUS_SUCCESS, mech.Authenticate(ctx, auth.GetBytes()));
        Assert.Equal(clientRandomKey,
            (byte[])mech.GetContextAttribute(ctx, GSSAttributeName.SessionKey));
    }

    [Fact]
    public void KeyExchange_MissingEncryptedKey_IsRejected()
    {
        // 协商了 KeyExchange 却不送加密会话密钥：报文自相矛盾，必须拒绝。
        var (mech, _) = NewMechanism();
        mech.GetChallengeMessage(out var ctx, BuildNegotiate(), out var challengeBytes);
        var serverChallenge = ServerChallengeFrom(challengeBytes);

        var ntHash = NtHash.Compute(Password);
        var ntOwfV2 = NtHash.NtOwfV2(ntHash, "alice", "WORKGROUP");
        var blob = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 };
        var proof = NtHash.NtProofStr(ntOwfV2, serverChallenge, blob);
        var ntResponse = new byte[proof.Length + blob.Length];
        proof.CopyTo(ntResponse, 0);
        blob.CopyTo(ntResponse, proof.Length);
        var auth = new AuthenticateMessage
        {
            Signature = "NTLMSSP\0",
            MessageType = MessageTypeName.Authenticate,
            DomainName = "WORKGROUP",
            UserName = "alice",
            WorkStation = "TESTPC",
            NegotiateFlags = NegotiateFlags.UnicodeEncoding
                | NegotiateFlags.ExtendedSessionSecurity
                | NegotiateFlags.KeyExchange,
            NtChallengeResponse = ntResponse,
            LmChallengeResponse = [],
            EncryptedRandomSessionKey = [],
        };

        Assert.Equal(NTStatus.STATUS_LOGON_FAILURE, mech.Authenticate(ctx, auth.GetBytes()));
    }

    [Fact]
    public void NoKeyExchange_ReturnsBaseKey()
    {
        // 不协商密钥交换时，SessionKey 即 SessionBaseKey（旧行为保持）。
        // 注意：从发出的报文中反解 proof/blob 再算期望值，避免与构造时的随机时间戳竞态。
        var (mech, _) = NewMechanism();
        mech.GetChallengeMessage(out var ctx, BuildNegotiate(), out var challengeBytes);
        var serverChallenge = ServerChallengeFrom(challengeBytes);
        var authBytes = BuildClientAuthenticate(serverChallenge, "alice", "WORKGROUP", Password);

        Assert.Equal(NTStatus.STATUS_SUCCESS, mech.Authenticate(ctx, authBytes));

        var sent = new AuthenticateMessage(authBytes);
        var sentProof = sent.NtChallengeResponse![..16];
        var sentBlob = sent.NtChallengeResponse![16..];
        var ntHash = NtHash.Compute(Password);
        var ntOwfV2 = NtHash.NtOwfV2(ntHash, "alice", "WORKGROUP");
        var expected = NtHash.SessionBaseKey(ntOwfV2, sentProof);
        // 顺带确认：用报文中原样 blob 重算 proof，应与报文中一致（自洽性）
        Assert.Equal(sentProof, NtHash.NtProofStr(ntOwfV2, serverChallenge, sentBlob));
        Assert.Equal(expected, (byte[])mech.GetContextAttribute(ctx, GSSAttributeName.SessionKey));
    }

    [Fact]
    public void WrongPassword_IsRejected()
    {
        var (mech, _) = NewMechanism();
        mech.GetChallengeMessage(out var ctx, BuildNegotiate(), out var challengeBytes);
        var serverChallenge = ServerChallengeFrom(challengeBytes);

        var authBytes = BuildClientAuthenticate(serverChallenge, "alice", "WORKGROUP", "wrong-password");
        Assert.Equal(NTStatus.STATUS_LOGON_FAILURE, mech.Authenticate(ctx, authBytes));
    }

    [Fact]
    public void UnknownUser_IsRejected()
    {
        var (mech, _) = NewMechanism();
        mech.GetChallengeMessage(out var ctx, BuildNegotiate(), out var challengeBytes);
        var serverChallenge = ServerChallengeFrom(challengeBytes);

        var authBytes = BuildClientAuthenticate(serverChallenge, "ghost", "WORKGROUP", Password);
        Assert.Equal(NTStatus.STATUS_LOGON_FAILURE, mech.Authenticate(ctx, authBytes));
    }

    [Fact]
    public void DisabledUser_IsRejected()
    {
        var (mech, _) = NewMechanism(enabled: false);
        mech.GetChallengeMessage(out var ctx, BuildNegotiate(), out var challengeBytes);
        var serverChallenge = ServerChallengeFrom(challengeBytes);

        var authBytes = BuildClientAuthenticate(serverChallenge, "alice", "WORKGROUP", Password);
        Assert.Equal(NTStatus.STATUS_LOGON_FAILURE, mech.Authenticate(ctx, authBytes));
    }

    [Fact]
    public void ReplayWithDifferentServerChallenge_IsRejected()
    {
        // 用第一次握手的响应去应答第二次挑战（server challenge 不同）必须失败——
        // 否则存在重放风险。
        var (mech, _) = NewMechanism();
        mech.GetChallengeMessage(out var ctx1, BuildNegotiate(), out var challenge1);
        var authBytes = BuildClientAuthenticate(ServerChallengeFrom(challenge1), "alice", "WORKGROUP", Password);

        mech.GetChallengeMessage(out var ctx2, BuildNegotiate(), out _);
        Assert.Equal(NTStatus.STATUS_LOGON_FAILURE, mech.Authenticate(ctx2, authBytes));
    }

    [Fact]
    public void TamperedProof_IsRejected()
    {
        var (mech, _) = NewMechanism();
        mech.GetChallengeMessage(out var ctx, BuildNegotiate(), out var challengeBytes);
        var serverChallenge = ServerChallengeFrom(challengeBytes);

        var authBytes = BuildClientAuthenticate(serverChallenge, "alice", "WORKGROUP", Password);
        // 篡改响应首字节（NTProofStr 起始）
        var auth = new AuthenticateMessage(authBytes);
        auth.NtChallengeResponse![0] ^= 0xFF;
        Assert.Equal(NTStatus.STATUS_LOGON_FAILURE, mech.Authenticate(ctx, auth.GetBytes()));
    }

    [Fact]
    public void ShortNonNtlmV2Response_IsRejected()
    {
        var (mech, _) = NewMechanism();
        mech.GetChallengeMessage(out var ctx, BuildNegotiate(), out _);

        var auth = new AuthenticateMessage
        {
            Signature = "NTLMSSP\0",
            MessageType = MessageTypeName.Authenticate,
            DomainName = "WORKGROUP",
            UserName = "alice",
            LmChallengeResponse = [], // 置空：库 GetBytes() 遇到 null 会 NRE（与线上行为无关，见注释）
            NtChallengeResponse = [1, 2, 3], // 长度不足 16，非 NTLMv2
        };
        // 断言「必须被拒绝」而非某个具体 NTStatus：长度不足可能走「非 NTLMv2」分支（LOGON_FAILURE），
        // 也可能序列化后无法解析（SEC_E_INVALID_TOKEN）。真正要保证的是**不放行**。
        var status = mech.Authenticate(ctx, auth.GetBytes());
        Assert.True(status != NTStatus.STATUS_SUCCESS, $"无效响应竟然被接受，status={status}");
    }

    [Fact]
    public void GssProvider_CanBeConstructedWithMechanism()
    {
        var (_, gss) = NewMechanism();
        Assert.NotNull(gss);
    }

    [Fact]
    public void Challenge_TargetInfo_HasFullAvSequence()
    {
        // 线上必须有完整 AV 序列（MS-NLMP 2.2.2.1）：impacket 在 SMB2/3 登录时
        // 硬要 AV_DNS_HOSTNAME（id 3），缺了它客户端解析 challenge 直接崩
        // （曾因切最小序列导致 smb-impacket-probe 全灭）。id 必须与规范一致，
        // 特别地 Timestamp = 7（vendored 枚举曾误写作 6，与 Flags 冲突）。
        var (mech, _) = NewMechanism();
        mech.GetChallengeMessage(out var _, BuildNegotiate(), out var challengeBytes);
        var parsed = new ChallengeMessage(challengeBytes);

        var ids = parsed.TargetInfo.Select(kv => (ushort)kv.Key).ToList();
        Assert.Equal([1, 2, 3, 4, 7, 6], ids);

        var byId = parsed.TargetInfo.ToDictionary(kv => (ushort)kv.Key, kv => kv.Value);
        var expectedComputer = (Environment.MachineName ?? string.Empty).Trim().ToUpperInvariant();
        Assert.Equal(expectedComputer, System.Text.Encoding.Unicode.GetString(byId[1]));
        Assert.Equal("WORKGROUP", System.Text.Encoding.Unicode.GetString(byId[2]));
        Assert.False(string.IsNullOrEmpty(System.Text.Encoding.Unicode.GetString(byId[3])));
        Assert.Equal(8, byId[7].Length); // FILETIME
        Assert.Equal(2u, BitConverter.ToUInt32(byId[6], 0));
    }
}
