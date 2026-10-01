namespace WebdavSharp.Core.Services;

/// <summary>
/// 服务端选项，绑定 <c>appsettings.json → Dav</c>。
/// 对齐 hacdias/webdav 顶层字段：address/port/prefix/directory|directories/
/// permissions/defaultRules/behindProxy/cors（子集，v1 按需扩展）。
/// </summary>
public sealed class DavServerOptions
{
    public const string SectionName = "Dav";

    public string Address { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 5210;

    /// <summary>WebDAV 路径前缀，默认 /dav/。</summary>
    public string Prefix { get; set; } = "/dav/";

    /// <summary>单根回退目录（未命中任何虚拟挂载时使用）。</summary>
    public string Directory { get; set; } = "LocalPaths/dav-root";

    /// <summary>用户默认权限字母，如 R。大小写不敏感。</summary>
    public string DefaultPermissions { get; set; } = "R";

    /// <summary>是否在可信代理后（取 X-Forwarded-For 记日志）。</summary>
    public bool BehindProxy { get; set; }

    public DavCorsOptions Cors { get; set; } = new();

    /// <summary>FreeSql DataType 名：Sqlite / PostgreSQL / MySql / SqlServer。</summary>
    public string Database { get; set; } = "Sqlite";

    public string ConnectionString { get; set; } = "Data Source=LocalPaths/webdavsharp.db";

    /// <summary>开发环境自动按实体建表/更新表结构（生产关闭）。</summary>
    public bool AutoSyncStructure { get; set; }

    /// <summary>
    /// 委托认证模式（对齐 hacdias noPassword）：DAV Basic 只认用户名、不验口令，
    /// 口令校验交给上游可信代理。开启后必须确保服务只监听可信来源！
    /// 管理后台 Cookie 登录不受影响（始终验口令）。
    /// </summary>
    public bool NoPassword { get; set; }

    public DavSeedOptions Seed { get; set; } = new();

    /// <summary>审计保留天数（0 = 不自动清理，靠管理页手动）。</summary>
    public int AuditRetainDays { get; set; } = 90;

    /// <summary>审计清理间隔（小时，至少 1 分钟；首次启动后 1 分钟即跑一次）。</summary>
    public double AuditCleanupIntervalHours { get; set; } = 24;

    /// <summary>暴力破解防护：窗口内允许的最大失败次数。</summary>
    public int MaxFailedLogins { get; set; } = 5;

    /// <summary>失败计数窗口（分钟）。</summary>
    public int FailedLoginWindowMinutes { get; set; } = 5;

    /// <summary>触发阈值后的封禁时长（分钟）。</summary>
    public int LoginBanMinutes { get; set; } = 15;

    // ---------- FTP ----------

    /// <summary>是否启用 FTP 服务。</summary>
    public bool FtpEnabled { get; set; }

    /// <summary>FTP 监听地址（空则用 <see cref="Address"/>）。</summary>
    public string FtpAddress { get; set; } = string.Empty;

    /// <summary>FTP 端口。默认 2121（非特权端口，避免 Linux 上需要 root）。</summary>
    public int FtpPort { get; set; } = 2121;

    /// <summary>FTP 被动模式数据端口范围起点（0 = 交给系统分配）。</summary>
    public int FtpPassivePortStart { get; set; }

    /// <summary>FTP 被动模式数据端口范围终点。</summary>
    public int FtpPassivePortEnd { get; set; }

    // ---------- SMB ----------

    /// <summary>是否启用 SMB 服务。默认关闭。</summary>
    public bool SmbEnabled { get; set; }

    /// <summary>SMB 监听地址（空则用 <see cref="Address"/>）。</summary>
    public string SmbAddress { get; set; } = string.Empty;

    /// <summary>
    /// SMB 监听端口，默认 445。
    /// 注意：Windows 资源管理器 / net use、macOS Finder、手机客户端只连 445，
    /// 非 445 端口只对支持指定端口的客户端有效（如 smbclient -p、pysmb）。
    /// 适用场景：445 被占用、多实例共存、联调。
    /// </summary>
    public int SmbPort { get; set; } = 445;

    /// <summary>服务器名（SMB 会话中显示）。</summary>
    public string SmbServerName { get; set; } = "WebdavSharp";

    /// <summary>是否启用 SMB1（默认关闭：老旧且安全性差）。</summary>
    public bool SmbEnableSmb1 { get; set; }

    /// <summary>是否启用 SMB2（默认开启）。</summary>
    public bool SmbEnableSmb2 { get; set; } = true;

    /// <summary>是否启用 SMB3（默认开启）。</summary>
    public bool SmbEnableSmb3 { get; set; } = true;
}

public sealed class DavCorsOptions
{
    public bool Enabled { get; set; } = true;
    public bool Credentials { get; set; } = true;
    public string[] AllowedHosts { get; set; } = ["*"];
}

public sealed class DavSeedOptions
{
    public string AdminUsername { get; set; } = "admin";
    public string AdminPassword { get; set; } = "admin";
}
