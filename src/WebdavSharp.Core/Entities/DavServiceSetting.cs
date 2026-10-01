using FreeSql.DataAnnotations;

namespace WebdavSharp.Core.Entities;

/// <summary>
/// 协议服务启停与监听配置（WebDAV / FTP / SMB 各一行）。
/// <para>
/// <b>DB 是运行真相，配置文件只是初始值</b>：首次启动时按 <c>Dav:*</c> 种子写入
/// （见 <c>SeedData</c>），之后以 DB 为准——管理页改完即时生效（WebDAV 的 IP/端口
/// 除外：那是 Kestrel 宿主绑定，改完需重启应用）。
/// </para>
/// </summary>
[Table(Name = "dav_service")]
public class DavServiceSetting
{
    /// <summary>协议名：webdav / ftp / smb（见 <see cref="Services.DavProtocols"/>）。</summary>
    [Column(IsPrimary = true, StringLength = 20)]
    public string Protocol { get; set; } = string.Empty;

    /// <summary>是否启用（运行时开关，不用改配置文件重启）。</summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 监听地址。空 = 跟随主地址（<c>Dav:Address</c>）。
    /// WebDAV 行存的是展示用值（实际绑定看启动参数，改完需重启）。
    /// </summary>
    [Column(StringLength = 100)]
    public string Address { get; set; } = string.Empty;

    /// <summary>监听端口（WebDAV 行同上：展示用，改完需重启）。</summary>
    public int Port { get; set; }
}
