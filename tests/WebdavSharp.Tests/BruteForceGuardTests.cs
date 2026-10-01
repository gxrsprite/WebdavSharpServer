using WebdavSharp.Server.WebDav;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>暴力破解防护单测。</summary>
public class BruteForceGuardTests
{
    [Fact]
    public void Banned_AfterThreshold_ThenExpires()
    {
        var g = new DavBruteForceGuard();
        var key = DavBruteForceGuard.KeyFor("1.2.3.4", "Admin");
        var now = DateTime.UtcNow;
        for (var i = 0; i < 4; i++)
            Assert.False(g.RecordFailure(key, now, maxFailures: 5, windowMinutes: 5, banMinutes: 15));
        Assert.True(g.RecordFailure(key, now, maxFailures: 5, windowMinutes: 5, banMinutes: 15));
        Assert.True(g.IsBanned(key, now, 15));
        Assert.False(g.IsBanned(key, now.AddMinutes(16), 15));
    }

    [Fact]
    public void Success_Resets()
    {
        var g = new DavBruteForceGuard();
        var key = DavBruteForceGuard.KeyFor("1.2.3.4", "u");
        var now = DateTime.UtcNow;
        g.RecordFailure(key, now, 5, 5, 15);
        g.RecordSuccess(key);
        Assert.False(g.IsBanned(key, now, 15));
        Assert.Equal(0, g.Count);
    }

    [Fact]
    public void Window_Slides()
    {
        var g = new DavBruteForceGuard();
        var key = DavBruteForceGuard.KeyFor("9.9.9.9", "u");
        var now = DateTime.UtcNow;
        g.RecordFailure(key, now, 5, 5, 15);
        // 窗口外重新计数，不会因旧失败被封
        Assert.False(g.RecordFailure(key, now.AddMinutes(6), 5, 5, 15));
    }

    [Fact]
    public void Keys_Isolated_ByIpAndUser()
    {
        var g = new DavBruteForceGuard();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            g.RecordFailure(DavBruteForceGuard.KeyFor("1.1.1.1", "victim"), now, 5, 5, 15);
        Assert.False(g.IsBanned(DavBruteForceGuard.KeyFor("2.2.2.2", "victim"), now, 15));
        Assert.False(g.IsBanned(DavBruteForceGuard.KeyFor("1.1.1.1", "other"), now, 15));
        // 用户名大小写归一
        Assert.True(g.IsBanned(DavBruteForceGuard.KeyFor("1.1.1.1", "VICTIM"), now, 15));
    }
}
