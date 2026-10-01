using WebdavSharp.Core.Entities;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>审计日志实体单测（落库/过滤/清理谓词）。</summary>
public class AuditLogTests
{
    private static IFreeSql CreateDb()
    {
        var fsql = new FreeSql.FreeSqlBuilder()
            .UseConnectionString(FreeSql.DataType.Sqlite, "Data Source=:memory:")
            .Build();
        fsql.CodeFirst.SyncStructure<DavAuditLog>();
        return fsql;
    }

    [Fact]
    public void Insert_And_Filter()
    {
        var db = CreateDb();
        db.Insert(new DavAuditLog { Username = "alice", Method = "PUT", VirtualPath = "/a.txt", StatusCode = 201 }).ExecuteAffrows();
        db.Insert(new DavAuditLog { Username = "alice", Method = "GET", VirtualPath = "/b.txt", StatusCode = 403 }).ExecuteAffrows();
        db.Insert(new DavAuditLog { Username = "bob", Method = "GET", VirtualPath = "/c", StatusCode = 200, Time = DateTime.UtcNow.AddDays(-100) }).ExecuteAffrows();

        Assert.Equal(2, db.Select<DavAuditLog>().Where(l => l.Username == "alice").Count());
        // 中间件审计规则：变更动词全记 + 失败；成功的 GET 不在其中
        Assert.Equal(2, db.Select<DavAuditLog>().Where(l => l.StatusCode >= 400 || (l.Method != "GET" && l.Method != "HEAD" && l.Method != "OPTIONS" && l.Method != "PROPFIND")).Count());

        var cutoff = DateTime.UtcNow.AddDays(-90);
        var purged = db.Delete<DavAuditLog>().Where(l => l.Time < cutoff).ExecuteAffrows();
        Assert.Equal(1, purged);
        Assert.Equal(2, db.Select<DavAuditLog>().Count());
    }

    [Fact]
    public void CleanupService_Purge_DeletesOnlyExpired()
    {
        var db = CreateDb();
        db.Insert(new DavAuditLog { Username = "old", Method = "PUT", VirtualPath = "/o", StatusCode = 201, Time = DateTime.UtcNow.AddDays(-100) }).ExecuteAffrows();
        db.Insert(new DavAuditLog { Username = "new", Method = "PUT", VirtualPath = "/n", StatusCode = 201 }).ExecuteAffrows();

        var n = WebdavSharp.Server.Services.DavAuditCleanupService.Purge(db, 90);
        Assert.Equal(1, n);
        Assert.Equal("new", db.Select<DavAuditLog>().First(l => l.Username));
    }
}
