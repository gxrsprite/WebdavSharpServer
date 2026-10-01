using FreeSql.DataAnnotations;

namespace WebdavSharp.Core.Entities;

/// <summary>
/// WebDAV 审计日志：记录变更操作（PUT/MKCOL/DELETE/COPY/MOVE/LOCK/UNLOCK/PROPPATCH）
/// 全量，以及 401/403/409/412/423/5xx 等失败请求；成功的读操作（GET/HEAD/OPTIONS/
/// PROPFIND）不记 resident 以防轮询打爆表。保留清理在管理页手动/按天执行。
/// </summary>
[Table(Name = "dav_audit_log")]
public class DavAuditLog
{
    [Column(IsPrimary = true)]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTime Time { get; set; } = DateTime.UtcNow;

    [Column(StringLength = 50)]
    public string Username { get; set; } = string.Empty;

    [Column(StringLength = 10)]
    public string Method { get; set; } = string.Empty;

    [Column(StringLength = 1000)]
    public string VirtualPath { get; set; } = string.Empty;

    [Column(StringLength = 1000)]
    public string Destination { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    [Column(StringLength = 500)]
    public string Detail { get; set; } = string.Empty;
}
