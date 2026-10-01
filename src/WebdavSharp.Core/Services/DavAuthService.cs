using System.Security.Cryptography;
using System.Text;
using WebdavSharp.Core.Entities;

namespace WebdavSharp.Core.Services;

/// <summary>密码哈希（PBKDF2-SHA256，自包含、无第三方依赖）。格式：PBKDF2$迭代数$盐$哈希。</summary>
public sealed class PasswordHasher
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"PBKDF2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "PBKDF2")
            return false; // 非本格式（含 WebBlazor 遗留明文列）一律不通过，由迁移流程处理
        if (!int.TryParse(parts[1], out var iterations))
            return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>
/// 认证服务：WebDAV Basic 用户名/口令校验 + 管理后台 Cookie 会话校验
/// （会话绑定 <see cref="DavUser.AuthVersion"/>，改密/禁用后旧会话失效——借鉴 WebBlazor）。
/// </summary>
public sealed class DavAuthService(IFreeSql fsql, PasswordHasher hasher)
{
    public async Task<DavUser?> AuthenticateBasicAsync(
        string username, string password, bool skipPasswordCheck = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;
        var user = await fsql.Select<DavUser>()
            .Where(u => u.Username == username && u.IsEnabled)
            .FirstAsync(ct);
        if (user is null)
            return null;
        // NoPassword 委托模式：只认用户名（上游代理已验）。管理后台登录永不走此分支。
        if (!skipPasswordCheck && !hasher.Verify(password, user.PasswordHash))
            return null;
        return user;
    }

    public async Task<DavUser?> ValidateSessionAsync(Guid userId, int authVersion, CancellationToken ct = default)
    {
        var user = await fsql.Select<DavUser>()
            .Where(u => u.Id == userId && u.IsEnabled)
            .FirstAsync(ct);
        if (user is null || user.AuthVersion != authVersion)
            return null;
        return user;
    }

    public static (string Username, string Password)? ParseBasicHeader(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..].Trim()));
            var colon = decoded.IndexOf(':');
            if (colon < 0)
                return null;
            return (decoded[..colon], decoded[(colon + 1)..]);
        }
        catch
        {
            return null;
        }
    }
}
