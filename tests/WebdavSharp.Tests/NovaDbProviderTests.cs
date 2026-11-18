using FreeSql.Custom;
using NewLife.NovaDb.Client;
using WebdavSharp.Core.Entities;
using Xunit;

namespace WebdavSharp.Tests;

// NovaDb 串行：嵌入引擎按目录常驻进程，删除测试目录前需释放句柄，并行跑会互相踩。
[CollectionDefinition("NovaDb", DisableParallelization = true)]
public class NovaDbCollection { }

/// <summary>
/// NovaDb 方言分支（third_party/FreeSql.Provider.Custom）集成测试：
/// 建表幂等 + 全字段形态 CRUD。要求：本仓默认库即 NovaDb，见 docs/novadb.md。
/// </summary>
[Collection("NovaDb")]
public class NovaDbProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly IFreeSql _fsql;

    public NovaDbProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "webdavsharp-novadb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var cs = $"Data Source={_dir}";
        _fsql = new FreeSql.FreeSqlBuilder()
            .UseConnectionFactory(FreeSql.DataType.CustomMySql, () => new NovaConnection(cs))
            .Build();
        _fsql.SetDbProviderFactory(NovaClientFactory.Instance);
    }

    public void Dispose()
    {
        try { (_fsql as IDisposable)?.Dispose(); } catch { }
        try { NovaConnection.DisposeSharedEngines(); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void SyncStructure_AllEntities_IsIdempotent()
    {
        _fsql.CodeFirst.SyncStructure<DavUser>();
        _fsql.CodeFirst.SyncStructure<DavRole>();
        _fsql.CodeFirst.SyncStructure<DavUserRole>();
        _fsql.CodeFirst.SyncStructure<DavMount>();
        _fsql.CodeFirst.SyncStructure<DavRule>();
        _fsql.CodeFirst.SyncStructure<DavAuditLog>();
        _fsql.CodeFirst.SyncStructure<DavServiceSetting>();

        // 第二轮必须零 DDL（否则每次启动都空跑迁移，见 NovaPrimaryDbType/NovaDbTypeEquals 注释）
        Assert.Null(_fsql.CodeFirst.GetComparisonDDLStatements<DavUser>());
        Assert.Null(_fsql.CodeFirst.GetComparisonDDLStatements<DavRole>());
        Assert.Null(_fsql.CodeFirst.GetComparisonDDLStatements<DavUserRole>());
        Assert.Null(_fsql.CodeFirst.GetComparisonDDLStatements<DavMount>());
        Assert.Null(_fsql.CodeFirst.GetComparisonDDLStatements<DavRule>());
        Assert.Null(_fsql.CodeFirst.GetComparisonDDLStatements<DavAuditLog>());
        Assert.Null(_fsql.CodeFirst.GetComparisonDDLStatements<DavServiceSetting>());

        _fsql.CodeFirst.SyncStructure<DavUserRole>(); // 唯一索引表二次同步也不得抛“已存在”
    }

    [Fact]
    public void Crud_CoversFieldShapes()
    {
        _fsql.CodeFirst.SyncStructure<DavUser>();
        _fsql.CodeFirst.SyncStructure<DavRole>();
        _fsql.CodeFirst.SyncStructure<DavUserRole>();
        _fsql.CodeFirst.SyncStructure<DavMount>();
        _fsql.CodeFirst.SyncStructure<DavRule>();

        var role = new DavRole { Name = "admin", IsSystem = true };
        Assert.Equal(1, _fsql.Insert(role).ExecuteAffrows());

        var user = new DavUser { Username = "alice", Nickname = "Alice", IsEnabled = true };
        Assert.Equal(1, _fsql.Insert(user).ExecuteAffrows());
        _fsql.Insert(new DavUserRole { UserId = user.Id, RoleId = role.Id }).ExecuteAffrows();

        // bool + Guid 主键的等值查询（Nova 不做 bool↔数字隐转，见 Ado 补丁注释）
        var found = _fsql.Select<DavUser>().Where(u => u.Username == "alice" && u.IsEnabled).First();
        Assert.NotNull(found);
        Assert.Equal(user.Id, found!.Id);

        // Windows 反斜杠路径必须原样往返（FreeSql MySQL 式 \\ 加倍已在分支中去掉）
        var winPath = @"C:\data\webdav\media";
        _fsql.Insert(new DavMount { Name = "media", PhysicalPath = winPath, IsVisible = true }).ExecuteAffrows();
        var mount = _fsql.Select<DavMount>().Where(m => m.Name == "media").First();
        Assert.NotNull(mount);
        Assert.Equal(winPath, mount!.PhysicalPath);

        // 枚举存取 + Contains/分页/计数
        _fsql.Insert(new DavRule { Kind = DavRuleKind.Regex, Pattern = @"^.+\.js$", Permissions = "none", SortOrder = 900 }).ExecuteAffrows();
        Assert.Equal(1, _fsql.Select<DavRule>().Where(r => r.Kind == DavRuleKind.Regex).Count());
        Assert.Equal(1, _fsql.Select<DavRule>().Where(r => r.Pattern.Contains(".js")).Count());
        var page = _fsql.Select<DavRule>().OrderBy(r => r.SortOrder).Page(1, 10).ToList();
        Assert.Single(page);

        // 更新 + 事务提交
        Assert.Equal(1, _fsql.Update<DavUser>().Set(u => u.Nickname, "Alice2").Where(u => u.Id == user.Id).ExecuteAffrows());
        _fsql.Transaction(() =>
        {
            _fsql.Insert(new DavRole { Name = "reader" }).ExecuteAffrows();
        });
        Assert.Equal(2, _fsql.Select<DavRole>().Count());

        // 删除
        Assert.Equal(1, _fsql.Delete<DavRule>().Where(r => r.Kind == DavRuleKind.Regex).ExecuteAffrows());
        Assert.Equal(0, _fsql.Select<DavRule>().Count());
    }
}
