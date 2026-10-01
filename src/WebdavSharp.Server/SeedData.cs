using Microsoft.Extensions.Options;
using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;

namespace WebdavSharp.Server;

/// <summary>
/// 首次运行种子：admin 角色/用户 + 示例挂载 public + 两条示例规则。
/// 密码来自 <c>Dav:Seed</c>（默认 admin/admin，生产务必覆盖并在首次登录后修改）。
/// </summary>
public static class SeedData
{
    /// <summary>
    /// 一次性迁移：把旧的 <c>IsHidden</c> 语义反转成 <c>IsVisible</c>。
    /// <para>
    /// 背景：字段从“隐藏（默认 false）”改为“可见（默认 true）”，便于“全勾选=全放开”。
    /// 若不搬运，升级后所有规则会变成不可见（列表突然空掉）。
    /// </para>
    /// <para>
    /// <b>必须在 SyncStructure 之前调用</b>：SQLite 下 FreeSql 同步结构会重建表，
    /// 重建后旧列数据丢失（变 NULL），届时 <c>CASE WHEN IsHidden = 1</c> 恒不成立，
    /// 会把所有行都写成可见——静默写坏数据。
    /// 做法：先手工补 IsVisible 列并把 <c>IsVisible = NOT IsHidden</c> 抄过去，
    /// 再交给 SyncStructure（它看到列已存在，不会再动数据）。
    /// </para>
    /// <para>幂等：无旧列时直接跳过；已有 IsVisible 时不再重复抄。</para>
    /// </summary>
    internal static void MigrateVisibilityFlag(IFreeSql fsql)
    {
        foreach (var table in new[] { "dav_rule", "dav_mount" })
        {
            if (!ColumnExists(fsql, table, "IsHidden"))
                continue;

            try
            {
                if (!ColumnExists(fsql, table, "IsVisible"))
                {
                    fsql.Ado.ExecuteNonQuery(
                        $"ALTER TABLE {table} ADD COLUMN IsVisible INTEGER NOT NULL DEFAULT 1");
                }
                fsql.Ado.ExecuteNonQuery(
                    $"UPDATE {table} SET IsVisible = CASE WHEN IsHidden = 1 THEN 0 ELSE 1 END");
            }
            catch
            {
                // 迁移失败不阻断启动（下次启动旧列仍在会重试）
            }
        }
    }

    private static bool ColumnExists(IFreeSql fsql, string table, string column)
    {
        try
        {
            var dt = fsql.Ado.ExecuteDataTable($"SELECT * FROM {table} WHERE 1 = 0");
            foreach (System.Data.DataColumn c in dt.Columns)
            {
                if (string.Equals(c.ColumnName, column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false; // 表不存在等：无需迁移
        }
    }

    public static void Initialize(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var fsql = sp.GetRequiredService<IFreeSql>();
        var hasher = sp.GetRequiredService<PasswordHasher>();
        var dav = sp.GetRequiredService<IOptions<DavServerOptions>>().Value;

        // 必须在 SyncStructure 之前跑：SQLite 上 FreeSql 同步结构会重建表，
        // 旧列数据届时已丢失，迁移就再也读不到原值了（见方法注释）。
        MigrateVisibilityFlag(fsql);

        fsql.CodeFirst.SyncStructure<DavUser>();
        fsql.CodeFirst.SyncStructure<DavRole>();
        fsql.CodeFirst.SyncStructure<DavUserRole>();
        fsql.CodeFirst.SyncStructure<DavMount>();
        fsql.CodeFirst.SyncStructure<DavRule>();
        fsql.CodeFirst.SyncStructure<DavAuditLog>();
        fsql.CodeFirst.SyncStructure<DavServiceSetting>();

        if (!fsql.Select<DavRole>().Any())
        {
            var adminRole = new DavRole { Name = "admin", Description = "系统管理员（全部权限）", IsSystem = true };
            var userRole = new DavRole { Name = "user", Description = "普通用户（默认只读，靠规则放行）" };
            fsql.Insert(new[] { adminRole, userRole }).ExecuteAffrows();
        }

        // 种子口令支持 {env}VAR 占位（变量缺失/为空则启动失败，见 EnvPlaceholder）。
        // 先解析再判重：用户名本身也可能是占位。
        var seedUsername = EnvPlaceholder.Resolve(dav.Seed.AdminUsername, "Dav:Seed:AdminUsername");
        var seedPassword = EnvPlaceholder.Resolve(dav.Seed.AdminPassword, "Dav:Seed:AdminPassword");
        if (!fsql.Select<DavUser>().Any(u => u.Username == seedUsername))
        {
            var adminRoleId = fsql.Select<DavRole>().Where(r => r.Name == "admin").First(r => r.Id);
            var admin = new DavUser
            {
                Username = seedUsername,
                Nickname = "管理员",
                PasswordHash = hasher.Hash(seedPassword),
                // SMB/NTLM 登录需要 NT 哈希（协议要求，无法用 PBKDF2 完成）
                NtHash = NtHash.ComputeHex(seedPassword),
                IsEnabled = true,
                IsSystem = true,
            };
            fsql.Insert(admin).ExecuteAffrows();
            fsql.Insert(new DavUserRole { UserId = admin.Id, RoleId = adminRoleId }).ExecuteAffrows();
        }

        if (!fsql.Select<DavMount>().Any())
        {
            var root = Path.GetFullPath(dav.Directory);
            Directory.CreateDirectory(Path.Combine(root, "public"));
            fsql.Insert(new DavMount
            {
                Name = "public",
                PhysicalPath = Path.Combine(root, "public"),
                SortOrder = 0,
                Description = "示例挂载（可删除或改名）",
            }).ExecuteAffrows();
        }

        if (!fsql.Select<DavRule>().Any())
        {
            var adminRoleId = fsql.Select<DavRole>().Where(r => r.Name == "admin").First(r => r.Id);
            fsql.Insert(new[]
            {
                // admin 角色：全部权限（SortOrder 小；序号更大的拒绝规则可覆盖它）
                new DavRule { RoleId = adminRoleId, Kind = DavRuleKind.Path, Pattern = "/", Permissions = "CRUD", SortOrder = 100, Note = "管理员全部权限" },
                // 示例：任何人（含匿名默认 R 之外）都不可见的黑洞目录
                new DavRule { Kind = DavRuleKind.Path, Pattern = "/public/private/", Permissions = "none", SortOrder = 900, Note = "示例拒绝规则（最后命中生效）" },
            }).ExecuteAffrows();
        }

        // 服务启停/监听配置：表空时播种一次（配置文件只管初始值），之后以 DB 为准。
        // 注意 SeedData 跑在 Program.Build 之后、托管服务启动之前，正好给 FTP/SMB 托管启动用。
        var settings = sp.GetRequiredService<DavServiceSettings>();
        settings.EnsureSeededAsync([
            (DavProtocols.WebDav, true, dav.Address, dav.Port),
            (DavProtocols.Ftp, dav.FtpEnabled, dav.FtpAddress, dav.FtpPort),
            (DavProtocols.Smb, dav.SmbEnabled, dav.SmbAddress, dav.SmbPort),
        ]).GetAwaiter().GetResult();
    }
}
