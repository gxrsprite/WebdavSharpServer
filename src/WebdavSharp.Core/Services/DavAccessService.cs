using Microsoft.Extensions.Options;
using WebdavSharp.Core.Entities;

namespace WebdavSharp.Core.Services;

/// <summary>
/// 协议无关的访问服务：认证、装载挂载与规则、创建会话。
/// <para>
/// WebDAV / SMB / FTP 都通过它取得 <see cref="DavSession"/>，
/// 从而共用同一套用户表、挂载表与规则表（含协议作用域）。
/// </para>
/// </summary>
public sealed class DavAccessService(
    IFreeSql db,
    IOptions<DavServerOptions> options,
    PasswordHasher hasher)
{
    private readonly DavServerOptions _dav = options.Value;

    /// <summary>
    /// 校验用户名/口令。口令走 PBKDF2 哈希；<c>NoPassword</c>（委托认证）时只认用户名。
    /// 禁用账号一律拒绝。
    /// </summary>
    public async Task<DavUser?> AuthenticateAsync(
        string? username, string? password, bool skipPasswordCheck = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var user = await db.Select<DavUser>()
            .Where(u => u.Username == username && u.IsEnabled)
            .FirstAsync(ct);
        if (user is null)
            return null;

        if (!skipPasswordCheck && !hasher.Verify(password ?? string.Empty, user.PasswordHash))
            return null;
        return user;
    }

    /// <summary>认证并建立会话；失败返回 null。</summary>
    public async Task<DavSession?> OpenSessionAsync(
        string? username, string? password, string protocol, CancellationToken ct = default)
    {
        var user = await AuthenticateAsync(username, password, _dav.NoPassword, ct);
        if (user is null)
            return null;
        return await CreateSessionAsync(user, protocol, ct);
    }

    /// <summary>用已认证用户建立会话（规则按协议过滤）。</summary>
    public async Task<DavSession> CreateSessionAsync(
        DavUser user, string protocol, CancellationToken ct = default)
    {
        var mounts = await db.Select<DavMount>()
            .Where(m => m.IsEnabled)
            .OrderBy(m => m.SortOrder)
            .ToListAsync(ct);

        var rules = await LoadRulesAsync(user, protocol, ct);
        var defaults = DavPermissionParser.Parse(_dav.DefaultPermissions);

        return new DavSession(user, protocol, mounts, rules, defaults, _dav.Directory);
    }

    /// <summary>
    /// 按用户名建立会话（用于**已认证连接**的后续操作）。
    /// <para>
    /// FTP 的 <c>IFileSystemProvider</c> 每次只拿到 <c>Username</c>（不含口令），
    /// SMB 的共享回调同理，故需要这条“已认证后重建会话”的通路。
    /// 每次都会重新查库确认账号**仍存在且启用**——账号被禁用后旧连接立即失效。
    /// </para>
    /// <para>注意：不做缓存，保证规则变更即时生效（代价是每次操作数次小查询）。</para>
    /// </summary>
    public async Task<DavSession?> BuildSessionForUsernameAsync(
        string? username, string protocol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var user = await db.Select<DavUser>()
            .Where(u => u.Username == username && u.IsEnabled)
            .FirstAsync(ct);
        if (user is null)
            return null;

        return await CreateSessionAsync(user, protocol, ct);
    }

    /// <summary>
    /// 装载适用规则：全局 + 本用户角色 + 本用户，按协议过滤，按 SortOrder 升序
    /// （最后命中者胜，与 WebDAV 完全一致）。
    /// </summary>
    public async Task<List<DavRule>> LoadRulesAsync(DavUser user, string protocol, CancellationToken ct = default)
    {
        var roleIds = await db.Select<DavUserRole>()
            .Where(r => r.UserId == user.Id)
            .ToListAsync(r => r.RoleId, ct);

        var rules = await db.Select<DavRule>()
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(ct);

        return rules
            .Where(r => (r.UserId == null && r.RoleId == null)          // 全局
                || (r.RoleId != null && roleIds.Contains(r.RoleId.Value)) // 角色
                || (r.UserId == user.Id))                                // 用户
            .Where(r => DavRuleScope.AppliesTo(r.Protocols, protocol))   // 协议作用域
            .OrderBy(r => r.SortOrder)
            .ToList();
    }

    /// <summary>列出已启用的挂载（SMB 用于注册共享）。</summary>
    public Task<List<DavMount>> LoadMountsAsync(CancellationToken ct = default) =>
        db.Select<DavMount>().Where(m => m.IsEnabled).OrderBy(m => m.SortOrder).ToListAsync(ct);

    /// <summary>
    /// 取用户的 NT 哈希（供 SMB/NTLM 验证）。禁用账号或未设置时返回 null。
    /// 仅返回哈希，不泄露口令。
    /// </summary>
    public async Task<byte[]?> GetNtHashAsync(string? username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var user = await db.Select<DavUser>()
            .Where(u => u.Username == username && u.IsEnabled)
            .FirstAsync(ct);
        return user is null ? null : NtHash.ParseHex(user.NtHash);
    }

    /// <summary>
    /// 设置口令：同时写入 PBKDF2（WebDAV/FTP 用）与 NT 哈希（SMB 用），并递增 AuthVersion
    /// 使旧会话失效。集中在此处，避免各处漏算 NT 哈希导致 SMB 登录不了。
    /// </summary>
    public async Task SetPasswordAsync(Guid userId, string password, CancellationToken ct = default)
    {
        var user = await db.Select<DavUser>().Where(u => u.Id == userId).FirstAsync(ct);
        if (user is null)
            return;

        await db.Update<DavUser>()
            .Set(u => u.PasswordHash, hasher.Hash(password))
            .Set(u => u.NtHash, NtHash.ComputeHex(password))
            .Set(u => u.AuthVersion, user.AuthVersion + 1)
            .Where(u => u.Id == userId)
            .ExecuteAffrowsAsync(ct);
    }
}
