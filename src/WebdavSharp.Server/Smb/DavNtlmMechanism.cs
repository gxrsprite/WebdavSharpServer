using System.Security.Cryptography;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using Utilities;
using WebdavSharp.Core.Services;

namespace WebdavSharp.Server.Smb;

/// <summary>
/// 基于**已存 NT 哈希**的 NTLM 认证机制（SMB 服务端用）。
/// <para>
/// 为什么不直接用 SMBLibrary 自带的 <c>IndependentNTLMAuthenticationProvider</c>：
/// 它的 <c>GetUserPassword</c> 委托要求返回**明文口令**（库内部再算 NT 哈希）。
/// 我们库里只有 PBKDF2 哈希与 NT 哈希、没有明文，因此必须自己验证。
/// </para>
/// <para>
/// 验证流程（MS-NLMP 3.3.2，NTLMv2）：
/// <list type="number">
///   <item>NTOWFv2 = HMAC_MD5(NTOWFv1, Uppercase(User) + Domain)</item>
///   <item>NTProofStr = HMAC_MD5(NTOWFv2, ServerChallenge + Blob)</item>
///   <item>与客户端 NtChallengeResponse 前 16 字节比对</item>
///   <item>SessionBaseKey = HMAC_MD5(NTOWFv2, NTProofStr)</item>
///   <item>
///   密钥交换（KEY_EXCH，MS-NLMP 3.4.3）：若客户端协商了 KeyExchange 并送来
///   EncryptedRandomSessionKey，则 ExportedSessionKey = RC4(SessionBaseKey, EncryptedRandomSessionKey)；
///   否则 ExportedSessionKey = SessionBaseKey。返回的 SessionKey 必须是导出密钥，
///   否则要求密钥交换的客户端（如部分 Java/Android 客户端）会直接拒绝连接
///   （"Server does not support extended NTLMv2 key exchange"），或后续 SMB 签名对不上。
///   </item>
/// </list>
/// 相比明文方案，这里全程只用 NT 哈希，明文口令永不进入进程。
/// </para>
/// </summary>
public sealed class DavNtlmMechanism(
    DavAccessService access,
    Microsoft.Extensions.Logging.ILogger<DavNtlmMechanism> logger,
    string? serverName = null)
    : NTLMAuthenticationProviderBase
{
    // 挑战与 AV 序列里广播的机器名。默认本机名；生产由托管服务传入 Dav:SmbServerName。
    private readonly string _serverName =
        string.IsNullOrWhiteSpace(serverName) ? Environment.MachineName : serverName;

    private sealed class Session
    {
        public byte[] ServerChallenge = [];
        public string UserName = string.Empty;
        public string DomainName = string.Empty;
        public byte[]? SessionKey;
        public NegotiateFlags Flags;
    }

    /// <summary>生成 Challenge（随机 8 字节 server challenge）。</summary>
    public override NTStatus GetChallengeMessage(out object context, byte[] negotiateMessageBytes, out byte[] challengeMessageBytes)
    {
        challengeMessageBytes = [];
        context = null!;
        try
        {
            var negotiate = new NegotiateMessage(negotiateMessageBytes);
            var session = new Session
            {
                ServerChallenge = RandomNumberGenerator.GetBytes(8),
                Flags = negotiate.NegotiateFlags,
            };
            context = session;

            // Follow SMBLibrary's reference provider: advertise only capabilities
            // that the client requested, plus the mandatory server/target fields.
            // Windows is stricter than Impacket about inconsistent NTLM flags.
            var flags = NegotiateFlags.TargetTypeServer
                | NegotiateFlags.TargetNameNegotiated
                | NegotiateFlags.TargetInfo
                | NegotiateFlags.Version
                | NegotiateFlags.NTLMSessionSecurity;
            if ((negotiate.NegotiateFlags & NegotiateFlags.UnicodeEncoding) != 0)
                flags |= NegotiateFlags.UnicodeEncoding;
            else if ((negotiate.NegotiateFlags & NegotiateFlags.OEMEncoding) != 0)
                flags |= NegotiateFlags.OEMEncoding;
            if ((negotiate.NegotiateFlags & NegotiateFlags.ExtendedSessionSecurity) != 0)
                flags |= NegotiateFlags.ExtendedSessionSecurity;
            if ((negotiate.NegotiateFlags & NegotiateFlags.Sign) != 0)
                flags |= NegotiateFlags.Sign;
            if ((negotiate.NegotiateFlags & NegotiateFlags.Seal) != 0)
                flags |= NegotiateFlags.Seal;
            if ((negotiate.NegotiateFlags & NegotiateFlags.Use56BitEncryption) != 0)
                flags |= NegotiateFlags.Use56BitEncryption;
            if ((negotiate.NegotiateFlags & NegotiateFlags.Use128BitEncryption) != 0)
                flags |= NegotiateFlags.Use128BitEncryption;
            if ((negotiate.NegotiateFlags & NegotiateFlags.KeyExchange) != 0)
                flags |= NegotiateFlags.KeyExchange;

            var challenge = new ChallengeMessage
            {
                Signature = "NTLMSSP\0",
                MessageType = MessageTypeName.Challenge,
                TargetName = _serverName,
                NegotiateFlags = flags,
                ServerChallenge = session.ServerChallenge,
                // Match SMBLibrary's reference provider: the client uses this
                // minimal NetBIOS AV sequence to construct its NTLMv2 response.
                TargetInfo = BuildTargetInfo(_serverName),
                // NTLMSSP_REVISION_W2K3 (0x0F) is required by strict Windows clients.
                 // A zero revision is tolerated by Impacket/CX but rejected by
                 // Windows before it sends the Type-3 Authenticate message.
                 Version = new NTLMVersion(10, 0, 19045, NTLMVersion.NTLMSSP_REVISION_W2K3),
            };
            challengeMessageBytes = challenge.GetBytes();
            // 关键：发 Challenge 后必须返回 CONTINUE_NEEDED（还需要客户端的 Authenticate），
            // 若返回 SUCCESS，服务端会认为认证已完成、跳过 Authenticate（表现为能登录但 UserName 为空）。
            return NTStatus.SEC_I_CONTINUE_NEEDED;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMB 生成 NTLM Challenge 失败");
            return NTStatus.SEC_E_INVALID_TOKEN;
        }
    }

    /// <summary>
    /// 构造完整的目标信息 AV 序列（MS-NLMP 2.2.2.1）。
    /// 含 Nb/DNS 机器名与域名、时间戳（FILETIME）、标志。每个挑战现算，
    /// 时间戳取当前时间（客户端一般只做宽松检查或直接沿用）。
    /// </summary>
    internal static KeyValuePairList<AVPairKey, byte[]> BuildTargetInfo(string serverName)
    {
        var computer = (string.IsNullOrWhiteSpace(serverName) ? Environment.MachineName : serverName).Trim();
        // 完整 AV 序列（MS-NLMP 2.2.2.1）：Nb/DNS 机器名与域名 + 时间戳 + 标志。
        // 注意：不能退回最小序列（仅 Nb 两项）——impacket 等客户端在 SMB2/3 登录时
        // 硬要 AV_DNS_HOSTNAME（id 3），缺了它客户端在解析 challenge 时直接崩。
        // 9/21 曾为 Windows 严格客户端切到最小序列，但那次真正的 Windows 修的是
        // flags 回声 + Version 修订（见 GetChallengeMessage），AV 最小化是误绑的；
        // 完整序列才是规范要求，impacket/pysmb/CX 在此全过。
        var pairs = new KeyValuePairList<AVPairKey, byte[]>();
        pairs.Add(AVPairKey.NbComputerName, System.Text.Encoding.Unicode.GetBytes(computer.ToUpperInvariant()));
        pairs.Add(AVPairKey.NbDomainName, System.Text.Encoding.Unicode.GetBytes("WORKGROUP"));
        pairs.Add(AVPairKey.DnsComputerName, System.Text.Encoding.Unicode.GetBytes(computer.ToLowerInvariant()));
        pairs.Add(AVPairKey.DnsDomainName, System.Text.Encoding.Unicode.GetBytes("workgroup"));
        pairs.Add(AVPairKey.Timestamp, BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()));
        pairs.Add(AVPairKey.Flags, BitConverter.GetBytes(2u));
        return pairs;
    }

    /// <summary>验证 Authenticate：用存储的 NT 哈希重算 NTLMv2 proof 并比对。</summary>
    public override NTStatus Authenticate(object context, byte[] authenticateMessageBytes)
    {
        if (context is not Session session)
            return NTStatus.SEC_E_INVALID_TOKEN;

        AuthenticateMessage auth;
        try
        {
            auth = new AuthenticateMessage(authenticateMessageBytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMB 解析 Authenticate 消息失败");
            return NTStatus.SEC_E_INVALID_TOKEN;
        }

        var user = auth.UserName ?? string.Empty;
        var domain = auth.DomainName ?? string.Empty;

        var ntHash = access.GetNtHashAsync(user).GetAwaiter().GetResult();
        if (ntHash is null)
        {
            // 用户不存在 / 已禁用 / 未设置 NT 哈希——统一返回登录失败，不区分原因（避免账号枚举）
            logger.LogWarning("SMB 登录失败：user={User}（不存在/禁用/未设 NT 哈希）", user);
            return NTStatus.STATUS_LOGON_FAILURE;
        }

        var ntResponse = auth.NtChallengeResponse ?? [];
        // NTLMv2：前 16 字节为 NTProofStr，其后为 blob（含 client challenge 等）
        if (ntResponse.Length <= 16)
        {
            logger.LogWarning("SMB 登录失败：user={User}（非 NTLMv2 响应或长度不足）", user);
            return NTStatus.STATUS_LOGON_FAILURE;
        }

        var clientProof = ntResponse[..16];
        var blob = ntResponse[16..];

        var ntOwfV2 = NtHash.NtOwfV2(ntHash, user, domain);
        var expectedProof = NtHash.NtProofStr(ntOwfV2, session.ServerChallenge, blob);

        if (!NtHash.FixedTimeEquals(clientProof, expectedProof))
        {
            logger.LogWarning("SMB 登录失败：user={User}（NTLM 响应校验不通过）", user);
            return NTStatus.STATUS_LOGON_FAILURE;
        }

        session.UserName = user;
        session.DomainName = domain;

        // 密钥交换：客户端若协商了 KeyExchange，会送 EncryptedRandomSessionKey
        // = RC4(SessionBaseKey, ClientRandomSessionKey)，导出密钥即解密后的客户端随机密钥。
        // 必须返回导出密钥给上层做 SMB 签名；错用 BaseKey 会导致签名对不上。
        var sessionBaseKey = NtHash.SessionBaseKey(ntOwfV2, clientProof);
        var wantsKeyExchange = (auth.NegotiateFlags & NegotiateFlags.KeyExchange) != 0;
        var encryptedKey = auth.EncryptedRandomSessionKey ?? [];
        byte[] sessionKey;
        if (wantsKeyExchange)
        {
            if (encryptedKey.Length == 0)
            {
                logger.LogWarning("SMB 登录失败：user={User}（协商了密钥交换但未送加密会话密钥）", user);
                return NTStatus.STATUS_LOGON_FAILURE;
            }
            try
            {
                sessionKey = System.Security.Cryptography.RC4.Decrypt(sessionBaseKey, encryptedKey);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SMB 登录失败：user={User}（会话密钥解密失败）", user);
                return NTStatus.STATUS_LOGON_FAILURE;
            }
            if (sessionKey.Length != 16)
            {
                logger.LogWarning("SMB 登录失败：user={User}（导出会话密钥长度异常）", user);
                return NTStatus.STATUS_LOGON_FAILURE;
            }
        }
        else
        {
            sessionKey = sessionBaseKey;
        }

        session.SessionKey = sessionKey;
        logger.LogInformation("SMB 登录成功：user={User} domain={Domain}", user, domain);
        return NTStatus.STATUS_SUCCESS;
    }

    public override bool DeleteSecurityContext(ref object context)
    {
        context = null!;
        return true;
    }

    public override object GetContextAttribute(object context, GSSAttributeName attributeName)
    {
        if (context is not Session session)
            return null!;

        return attributeName switch
        {
            GSSAttributeName.UserName => session.UserName,
            GSSAttributeName.DomainName => session.DomainName,
            GSSAttributeName.SessionKey => session.SessionKey!,
            GSSAttributeName.IsGuest => false,
            GSSAttributeName.IsAnonymous => false,
            GSSAttributeName.MachineName => session.UserName,
            GSSAttributeName.OSVersion => "10.0",
            _ => null!,
        };
    }
}
