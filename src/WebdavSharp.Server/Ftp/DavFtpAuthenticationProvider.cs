using FmiSrl.FtpServer.Server.Abstractions;
using Microsoft.Extensions.Logging;
using WebdavSharp.Core.Services;

namespace WebdavSharp.Server.Ftp;

/// <summary>
/// FTP 认证：直接复用 <see cref="DavAccessService"/>，与 WebDAV 同一张用户表、
/// 同一套 PBKDF2 口令校验与 <c>NoPassword</c> 委托模式。
/// </summary>
public sealed class DavFtpAuthenticationProvider(
    DavAccessService access,
    Microsoft.Extensions.Options.IOptions<DavServerOptions> options,
    ILogger<DavFtpAuthenticationProvider> logger) : IAuthenticationProvider
{
    private readonly DavServerOptions _dav = options.Value;

    public async Task<bool> AuthenticateAsync(string username, string password)
    {
        var user = await access.AuthenticateAsync(username, password, _dav.NoPassword);
        if (user is null)
        {
            logger.LogWarning("FTP 认证失败：{User}", username);
            return false;
        }
        logger.LogInformation("FTP 认证成功：{User}", user.Username);
        return true;
    }
}

/// <summary>FTP 操作越权/非法路径时抛出，由服务器转成 FTP 错误码（550/530）。</summary>
public sealed class FtpAccessDeniedException(string message) : Exception(message);
