using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>
/// 协议服务配置（DavServiceSettings）单测：播种一次语义、校验、缓存失效。
/// 这是“配置文件只管初始值、DB 是运行真相”的契约保证。
/// </summary>
public class ServiceSettingsTests
{
    private static IFreeSql NewDb()
    {
        var fsql = new FreeSql.FreeSqlBuilder()
            .UseConnectionString(FreeSql.DataType.Sqlite, "Data Source=:memory:")
            .Build();
        fsql.CodeFirst.SyncStructure<DavServiceSetting>();
        return fsql;
    }

    [Fact]
    public async Task EnsureSeeded_WritesOnce_NeverOverwrites()
    {
        var svc = new DavServiceSettings(NewDb());
        await svc.EnsureSeededAsync([
            (DavProtocols.WebDav, true, "127.0.0.1", 5210),
            (DavProtocols.Ftp, false, "", 2121),
            (DavProtocols.Smb, false, "", 445),
        ]);

        // 管理页改掉 FTP，再跑一次播种：必须保留管理页的值
        await svc.SetAsync(DavProtocols.Ftp, true, "0.0.0.0", 2122);
        await svc.EnsureSeededAsync([
            (DavProtocols.WebDav, true, "127.0.0.1", 5210),
            (DavProtocols.Ftp, false, "", 2121),
            (DavProtocols.Smb, false, "", 445),
        ]);

        var ftp = await svc.GetAsync(DavProtocols.Ftp);
        Assert.True(ftp.IsEnabled);
        Assert.Equal("0.0.0.0", ftp.Address);
        Assert.Equal(2122, ftp.Port);
        Assert.Equal(3, (await svc.GetAllAsync()).Count);
    }

    [Fact]
    public async Task Set_RejectsBadInput()
    {
        var svc = new DavServiceSettings(NewDb());
        await svc.EnsureSeededAsync([(DavProtocols.Ftp, false, "", 2121)]);

        var (ok1, err1) = await svc.SetAsync("gopher", true, "", 70);
        Assert.False(ok1);
        Assert.Contains("未知协议", err1);

        var (ok2, err2) = await svc.SetAsync(DavProtocols.Ftp, true, "", 0);
        Assert.False(ok2);
        Assert.Contains("1~65535", err2);

        var (ok3, err3) = await svc.SetAsync(DavProtocols.Ftp, true, "", 70000);
        Assert.False(ok3);

        var (ok4, err4) = await svc.SetAsync(DavProtocols.Ftp, true, "not-an-ip", 2121);
        Assert.False(ok4);
        Assert.Contains("IP", err4);

        // 坏输入不能污染已存值
        var ftp = await svc.GetAsync(DavProtocols.Ftp);
        Assert.False(ftp.IsEnabled);
        Assert.Equal(2121, ftp.Port);
    }

    [Fact]
    public async Task Set_AcceptsEmptyAddress_MeansFollowMain()
    {
        var svc = new DavServiceSettings(NewDb());
        await svc.EnsureSeededAsync([(DavProtocols.Smb, false, "1.2.3.4", 445)]);
        var (ok, err) = await svc.SetAsync(DavProtocols.Smb, true, "   ", 4445);
        Assert.True(ok, err);
        Assert.Equal(string.Empty, (await svc.GetAsync(DavProtocols.Smb)).Address);
    }

    [Fact]
    public async Task Get_UnknownProtocol_Throws()
    {
        var svc = new DavServiceSettings(NewDb());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetAsync("gopher"));
    }

    [Fact]
    public async Task WebDav_SeededEnabled()
    {
        var svc = new DavServiceSettings(NewDb());
        await svc.EnsureSeededAsync([
            (DavProtocols.WebDav, true, "127.0.0.1", 5210),
            (DavProtocols.Ftp, false, "", 2121),
            (DavProtocols.Smb, false, "", 445),
        ]);
        var webdav = await svc.GetAsync(DavProtocols.WebDav);
        Assert.True(webdav.IsEnabled);
        Assert.Equal(5210, webdav.Port);
    }
}
