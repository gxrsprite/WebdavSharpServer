using FreeSql.DataAnnotations;

namespace WebdavSharp.Core.Entities;

/// <summary>
/// 虚拟挂载：把一个物理目录挂为虚拟根下的一个条目。
/// 对齐 hacdias/webdav 的 <c>directories:</c>（<c>- media: /data/media</c>）。
/// 未命中任何挂载名的虚拟路径走 <see cref="Services.DavServerOptions.Directory"/> 单根回退。
/// </summary>
[Table(Name = "dav_mount")]
public class DavMount
{
    [Column(IsPrimary = true)]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>虚拟根下的条目名，如 media。URL 形如 /dav/media/...。</summary>
    [Column(StringLength = 100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>物理目录（绝对路径或相对程序目录）。</summary>
    [Column(StringLength = 1000)]
    public string PhysicalPath { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 是否可见：true（默认）= 出现在虚拟根列表；false = 不列出，但知道 URL 仍可直接访问
    /// （权限照常校验）。对齐常见 NAS 的“不列出但可直达”。
    /// 默认可见，便于“全勾选=全部权限/全部放开”的直觉操作。
    /// </summary>
    public bool IsVisible { get; set; } = true;

    [Column(StringLength = 500)]
    public string Description { get; set; } = string.Empty;
}

/// <summary>规则类型：path 前缀匹配 / regex 全路径字面匹配。</summary>
public enum DavRuleKind
{
    Path = 0,
    Regex = 1,
}

/// <summary>
/// 访问规则。对齐 hacdias <c>rules:</c> ——“最后一条命中的规则生效”。
/// 规则可挂在用户或角色上（都为空 = 全局默认，优先级最低）；
/// <see cref="SortOrder"/> 越大越靠后生效。权限字母见 <see cref="DavPermissionParser"/>。
/// </summary>
[Table(Name = "dav_rule")]
public class DavRule
{
    [Column(IsPrimary = true)]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid? UserId { get; set; }

    public Guid? RoleId { get; set; }

    public DavRuleKind Kind { get; set; } = DavRuleKind.Path;

    /// <summary>
    /// 虚拟路径（含挂载名），如 <c>/media/public/access/</c>。
    /// 尾斜杠同时覆盖集合自身（/secret/ 覆盖对 /secret 的请求）。
    /// Regex 类型时为正则，如 <c>^.+\.js$</c>。
    /// </summary>
    [Column(StringLength = 1000)]
    public string Pattern { get; set; } = string.Empty;

    /// <summary>权限字母，如 CRUD / RU / R / none。</summary>
    [Column(StringLength = 10)]
    public string Permissions { get; set; } = "R";

    public int SortOrder { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 是否可见：true（默认）= 出现在父目录列表；false = 不列出该条目，
    /// 但知道 URL 仍可访问——与权限独立判定。取“最后一条命中规则”的值
    /// （同权限的 last-match-wins 语义）。默认可见，便于“全勾选即全放开”。
    /// </summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// 适配协议：逗号分隔，可选 webdav / smb / ftp；**留空 = 适配所有协议**。
    /// 例：空 → 三协议通用；"webdav" → 只作用于 WebDAV；"smb,ftp" → 只作用于 SMB/FTP。
    /// </summary>
    [Column(StringLength = 100)]
    public string Protocols { get; set; } = string.Empty;

    [Column(StringLength = 500)]
    public string Note { get; set; } = string.Empty;
}
