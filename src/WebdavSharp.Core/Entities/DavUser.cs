using FreeSql.DataAnnotations;

namespace WebdavSharp.Core.Entities;

/// <summary>
/// 用户。借鉴 WebBlazor <c>SysUser</c>：PHC hash 存 <see cref="PasswordHash"/>、
/// 凭据变更时递增 <see cref="AuthVersion"/> 使已颁发会话失效。
/// </summary>
[Table(Name = "dav_user")]
public class DavUser
{
    [Column(IsPrimary = true)]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [Column(StringLength = 50)]
    public string Username { get; set; } = string.Empty;

    [Column(StringLength = 50)]
    public string Nickname { get; set; } = string.Empty;

    /// <summary>PHC password hash（PBKDF2-SHA256，见 PasswordHasher）。</summary>
    [Column(StringLength = 512)]
    public string? PasswordHash { get; set; }

    /// <summary>
    /// NT 哈希（MD4(UTF-16LE 口令)，大写十六进制）。**仅供 SMB/NTLM 登录验证**。
    /// <para>
    /// NTLM 协议要求服务端持有 NT 哈希或明文才能验证客户端响应，
    /// 因此启用 SMB 登录的用户需要此列（Samba/AD 同样做法）。
    /// 该哈希为无盐 MD4，安全性弱于 <see cref="PasswordHash"/>；
    /// 不需要 SMB 登录的用户可将其留空以降低暴露面。
    /// </para>
    /// </summary>
    [Column(StringLength = 64)]
    public string? NtHash { get; set; }

    /// <summary>凭据变更时递增，Cookie 会话绑定此版本号。</summary>
    public int AuthVersion { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool IsSystem { get; set; }

    /// <summary>
    /// 用户主目录（对齐 hacdias per-user directory）：设置后该用户的整个虚拟命名空间
    /// 直接映射到此物理目录，不再做挂载名拆分（与全局 directories 互斥、优先）。
    /// 为空则走全局挂载/单根回退。规则仍按虚拟路径匹配。
    /// </summary>
    [Column(StringLength = 1000)]
    public string RootPath { get; set; } = string.Empty;

    [Column(StringLength = 500)]
    public string Description { get; set; } = string.Empty;

    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

    [Navigate(ManyToMany = typeof(DavUserRole))]
    public List<DavRole>? Roles { get; set; }
}

/// <summary>角色：规则可挂在角色上，用户通过角色继承目录权限。</summary>
[Table(Name = "dav_role")]
public class DavRole
{
    [Column(IsPrimary = true)]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [Column(StringLength = 50)]
    public string Name { get; set; } = string.Empty;

    [Column(StringLength = 500)]
    public string Description { get; set; } = string.Empty;

    public bool IsSystem { get; set; }
}

/// <summary>用户—角色多对多关联。</summary>
[Table(Name = "dav_user_role")]
public class DavUserRole
{
    [Column(IsPrimary = true)]
    public Guid UserId { get; set; }

    [Column(IsPrimary = true)]
    public Guid RoleId { get; set; }
}
