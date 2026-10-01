using Microsoft.Extensions.Options;
using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>
/// 协议无关访问脊柱（DavAccessService / DavSession）单测。
/// 这是 SMB/FTP 与 WebDAV 行为一致的保证点，因此重点覆盖：
/// 协议过滤、规则优先级、可见性、路径逃逸、列表过滤。
/// </summary>
public class DavSessionTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "davsess-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public DavSessionTests()
    {
        _root = Path.Combine(_base, "davroot");
        Directory.CreateDirectory(Path.Combine(_root, "public", "open"));
        Directory.CreateDirectory(Path.Combine(_root, "public", "private"));
        File.WriteAllText(Path.Combine(_root, "public", "open", "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "public", "private", "secret.txt"), "s");
    }

    public void Dispose()
    {
        DavCaseProbe.ClearCache();
        if (Directory.Exists(_base))
            Directory.Delete(_base, true);
    }

    private static DavSession NewSession(
        string protocol,
        string fallback,
        IEnumerable<DavRule>? rules = null,
        string defaults = "R",
        string? userRoot = null,
        IEnumerable<DavMount>? mounts = null)
    {
        var user = new DavUser { Username = "u", IsEnabled = true, RootPath = userRoot ?? string.Empty };
        return new DavSession(
            user, protocol,
            mounts?.ToList() ?? [],
            rules?.ToList() ?? [],
            DavPermissionParser.Parse(defaults),
            fallback);
    }

    private static DavRule Rule(string pattern, string perms, bool visible = true, string protocols = "", int sort = 0) =>
        new()
        {
            Kind = DavRuleKind.Path,
            Pattern = pattern,
            Permissions = perms,
            IsVisible = visible,
            Protocols = protocols,
            SortOrder = sort,
            IsEnabled = true,
        };

    [Fact]
    public void Resolve_MapsVirtualToPhysical()
    {
        var s = NewSession(DavProtocols.Ftp, _root);
        var loc = s.Resolve("public/open/a.txt");
        Assert.NotNull(loc);
        Assert.Equal(Path.Combine(_root, "public", "open", "a.txt"), loc!.PhysicalPath);
        Assert.Equal("/public/open/a.txt", loc.RulePath);
    }

    [Fact]
    public void Resolve_Escape_ReturnsNull()
    {
        var s = NewSession(DavProtocols.Ftp, _root);
        Assert.Null(s.Resolve("../outside.txt"));
        Assert.Null(s.Resolve("public/../../outside.txt"));
    }

    [Fact]
    public void Allowed_UsesDefaultsWhenNoRuleMatches()
    {
        var s = NewSession(DavProtocols.Ftp, _root, defaults: "R");
        Assert.True(s.Allowed("public/open/a.txt", DavPermission.Read));
        Assert.False(s.Allowed("public/open/a.txt", DavPermission.Delete));
    }

    [Fact]
    public void Rules_RespectLastMatchWins()
    {
        var rules = new[]
        {
            Rule("/", "CRUD", sort: 100),
            Rule("/public/private/", "none", sort: 900),
        };
        var s = NewSession(DavProtocols.Ftp, _root, rules);
        Assert.True(s.Allowed("public/open/a.txt", DavPermission.Read));
        Assert.False(s.Allowed("public/private/secret.txt", DavPermission.Read));
    }

    [Fact]
    public void ProtocolScoped_Rules_OnlyApplyToMatchingProtocol()
    {
        // 该规则只作用于 SMB：FTP 会话不应看到它
        var rules = new[] { Rule("/", "CRUD", protocols: DavProtocols.Smb, sort: 100) };

        var ftp = NewSession(DavProtocols.Ftp, _root, rules, defaults: "R");
        var smb = NewSession(DavProtocols.Smb, _root, rules, defaults: "R");

        Assert.False(ftp.Allowed("public/open/a.txt", DavPermission.Delete));
        Assert.True(smb.Allowed("public/open/a.txt", DavPermission.Delete));
    }

    [Fact]
    public void EmptyProtocols_AppliesToAllProtocols()
    {
        var rules = new[] { Rule("/", "CRUD", protocols: "", sort: 100) };
        foreach (var proto in DavProtocols.All)
        {
            var s = NewSession(proto, _root, rules, defaults: "R");
            Assert.True(s.Allowed("public/open/a.txt", DavPermission.Delete));
        }
    }

    [Fact]
    public void List_FiltersByReadPermission()
    {
        var rules = new[] { Rule("/public/private/", "none", sort: 900) };
        var s = NewSession(DavProtocols.Ftp, _root, rules, defaults: "R");

        var names = s.List("public").Select(e => e.Name).ToList();
        Assert.Contains("open", names);
        Assert.DoesNotContain("private", names);
    }

    [Fact]
    public void List_FiltersInvisible()
    {
        var rules = new[] { Rule("/public/private/", "R", visible: false, sort: 900) };
        var s = NewSession(DavProtocols.Ftp, _root, rules, defaults: "R");

        var names = s.List("public").Select(e => e.Name).ToList();
        Assert.DoesNotContain("private", names);          // 不可见 → 不列出
        Assert.True(s.Allowed("public/private/secret.txt", DavPermission.Read)); // 但仍可直达
    }

    [Fact]
    public void List_ReportsMetadata()
    {
        var s = NewSession(DavProtocols.Ftp, _root, defaults: "R");
        var entries = s.List("public/open");
        var file = Assert.Single(entries);
        Assert.Equal("a.txt", file.Name);
        Assert.False(file.IsDirectory);
        Assert.Equal(1, file.Length);
        Assert.True(file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1));
    }

    [Fact]
    public void List_Nonexistent_ReturnsEmpty()
    {
        var s = NewSession(DavProtocols.Ftp, _root, defaults: "R");
        Assert.Empty(s.List("no/such/dir"));
    }

    [Fact]
    public void ListRootEntries_ListsMountsInsteadOfFallbackDir()
    {
        // 回退目录物理上只有 public；虚拟挂载 NAS/D 必须出现在根列表里
        // （FTP 根之前只列 public 的 bug）。
        var nas = Path.Combine(_base, "nas");
        var d = Path.Combine(_base, "droot");
        Directory.CreateDirectory(nas);
        Directory.CreateDirectory(d);
        var mounts = new[]
        {
            new DavMount { Name = "public", PhysicalPath = Path.Combine(_root, "public"), IsEnabled = true },
            new DavMount { Name = "NAS", PhysicalPath = nas, IsEnabled = true },
            new DavMount { Name = "D", PhysicalPath = d, IsEnabled = true },
        };
        var s = NewSession(DavProtocols.Ftp, _root, mounts: mounts, defaults: "R");

        var names = s.ListRootEntries().Select(e => e.Name).ToList();
        Assert.Equal(["D", "NAS", "public"], names);
        Assert.All(s.ListRootEntries(), e => Assert.True(e.IsDirectory));
    }

    [Fact]
    public void ListRootEntries_RespectsVisibilityAndRead()
    {
        var nas = Path.Combine(_base, "nas2");
        var hid = Path.Combine(_base, "hid");
        var deny = Path.Combine(_base, "deny");
        Directory.CreateDirectory(nas);
        Directory.CreateDirectory(hid);
        Directory.CreateDirectory(deny);
        var mounts = new[]
        {
            new DavMount { Name = "NAS", PhysicalPath = nas, IsEnabled = true },
            // 挂载级不可见：不列出，但可直达
            new DavMount { Name = "hid", PhysicalPath = hid, IsEnabled = true, IsVisible = false },
            // 无 Read：不列出，直达也被拒
            new DavMount { Name = "deny", PhysicalPath = deny, IsEnabled = true },
        };
        var rules = new[] { Rule("/deny/", "none", sort: 900) };
        var s = NewSession(DavProtocols.Ftp, _root, rules, defaults: "R", mounts: mounts);

        var names = s.ListRootEntries().Select(e => e.Name).ToList();
        Assert.Equal(["NAS"], names);
        Assert.True(s.Allowed("hid", DavPermission.Read)); // 不可见但仍可直达
        Assert.False(s.Allowed("deny", DavPermission.Read)); // 无 Read，直达也被拒
    }

    [Fact]
    public void ListRootEntries_UserRootOrNoMounts_FallsBackToPhysical()
    {
        // 有用户主目录时虚拟根即主目录，走普通物理列表
        var home = Path.Combine(_base, "homes", "bob");
        Directory.CreateDirectory(Path.Combine(home, "docs"));
        var s = NewSession(DavProtocols.Ftp, _root, userRoot: home);
        Assert.Equal(["docs"], s.ListRootEntries().Select(e => e.Name).ToList());

        // 无挂载时同样回退
        var s2 = NewSession(DavProtocols.Ftp, _root);
        Assert.Equal(["public"], s2.ListRootEntries().Select(e => e.Name).ToList());
    }

    [Fact]
    public void Mount_MapsToShareRoot()
    {
        var mountDir = Path.Combine(_base, "nas");
        Directory.CreateDirectory(Path.Combine(mountDir, "剧集"));
        var mounts = new[] { new DavMount { Name = "NAS", PhysicalPath = mountDir, IsEnabled = true } };

        var s = NewSession(DavProtocols.Smb, _root, mounts: mounts);
        var loc = s.Resolve("NAS/剧集");
        Assert.NotNull(loc);
        Assert.Equal(Path.Combine(mountDir, "剧集"), loc!.PhysicalPath);
        Assert.Equal("NAS", loc.Mount?.Name);
    }

    [Fact]
    public void UserRootPath_TakesPrecedenceOverMounts()
    {
        var home = Path.Combine(_base, "homes", "alice");
        Directory.CreateDirectory(Path.Combine(home, "docs"));
        var mounts = new[] { new DavMount { Name = "NAS", PhysicalPath = Path.Combine(_base, "nas"), IsEnabled = true } };

        var s = NewSession(DavProtocols.Ftp, _root, mounts: mounts, userRoot: home);
        var loc = s.Resolve("docs");
        Assert.NotNull(loc);
        Assert.Equal(Path.Combine(home, "docs"), loc!.PhysicalPath);
    }

    // ---------- DavAccessService（认证/装载） ----------

    private static (DavAccessService Service, IFreeSql Db) NewService(string fallback, string adminPw = "pw")
    {
        var fsql = new FreeSql.FreeSqlBuilder()
            .UseConnectionString(FreeSql.DataType.Sqlite, "Data Source=:memory:")
            .Build();
        fsql.CodeFirst.SyncStructure<DavUser>();
        fsql.CodeFirst.SyncStructure<DavRole>();
        fsql.CodeFirst.SyncStructure<DavUserRole>();
        fsql.CodeFirst.SyncStructure<DavMount>();
        fsql.CodeFirst.SyncStructure<DavRule>();

        var hasher = new PasswordHasher();
        var role = new DavRole { Name = "admin" };
        fsql.Insert(role).ExecuteAffrows();
        var u = new DavUser { Username = "alice", PasswordHash = hasher.Hash(adminPw), IsEnabled = true };
        fsql.Insert(u).ExecuteAffrows();
        fsql.Insert(new DavUserRole { UserId = u.Id, RoleId = role.Id }).ExecuteAffrows();
        fsql.Insert(new DavMount { Name = "NAS", PhysicalPath = fallback, SortOrder = 1 }).ExecuteAffrows();
        fsql.Insert(new DavRule { RoleId = role.Id, Kind = DavRuleKind.Path, Pattern = "/", Permissions = "CRUD", SortOrder = 100 }).ExecuteAffrows();
        fsql.Insert(new DavRule { RoleId = role.Id, Kind = DavRuleKind.Path, Pattern = "/ftp-only/", Permissions = "none", Protocols = DavProtocols.Ftp, SortOrder = 900 }).ExecuteAffrows();

        var opts = Options.Create(new DavServerOptions { Directory = fallback, DefaultPermissions = "R" });
        return (new DavAccessService(fsql, opts, hasher), fsql);
    }

    [Fact]
    public async Task Authenticate_AcceptsCorrectPassword_RejectsWrong()
    {
        var (svc, _) = NewService(_root);
        Assert.NotNull(await svc.AuthenticateAsync("alice", "pw"));
        Assert.Null(await svc.AuthenticateAsync("alice", "bad"));
        Assert.Null(await svc.AuthenticateAsync("ghost", "pw"));
    }

    [Fact]
    public async Task OpenSession_LoadsProtocolScopedRules()
    {
        var (svc, _) = NewService(_root);

        var ftp = await svc.OpenSessionAsync("alice", "pw", DavProtocols.Ftp);
        Assert.NotNull(ftp);
        // ftp-only 规则对 FTP 生效
        Assert.False(ftp!.Allowed("ftp-only/x", DavPermission.Read));

        var smb = await svc.OpenSessionAsync("alice", "pw", DavProtocols.Smb);
        Assert.NotNull(smb);
        // 同一规则对 SMB 不生效
        Assert.True(smb!.Allowed("ftp-only/x", DavPermission.Read));
    }

    [Fact]
    public async Task OpenSession_RejectsDisabledUser()
    {
        var (svc, db) = NewService(_root);
        db.Update<DavUser>().Set(u => u.IsEnabled, false).Where(u => u.Username == "alice").ExecuteAffrows();
        Assert.Null(await svc.OpenSessionAsync("alice", "pw", DavProtocols.Ftp));
    }

    [Fact]
    public async Task Session_SeesMounts()
    {
        var (svc, _) = NewService(_root);
        var s = await svc.OpenSessionAsync("alice", "pw", DavProtocols.Smb);
        Assert.NotNull(s);
        Assert.Single(s!.Mounts);
        Assert.Equal("NAS", s.Mounts[0].Name);
    }
}
