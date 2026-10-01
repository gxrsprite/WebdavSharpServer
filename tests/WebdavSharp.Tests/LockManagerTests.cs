using WebdavSharp.Server.WebDav;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>DavLockManager 单测：创建/冲突/共享/刷新/解锁/过期/写检查/头解析。</summary>
public class LockManagerTests
{
    private static string PathFor(string name) =>
        Path.Combine(Path.GetTempPath(), "davlock-" + name);

    [Fact]
    public void Create_ThenWriteBlocked_ThenUnlockAllows()
    {
        var mgr = new DavLockManager();
        var entry = mgr.Create(PathFor("a"), exclusive: true, depthInfinity: true, owner: "u", timeout: TimeSpan.FromMinutes(5));
        Assert.NotNull(entry);

        Assert.False(mgr.CheckWrite(PathFor("a"), [], deep: false));
        Assert.False(mgr.CheckWrite(PathFor("a") + Path.DirectorySeparatorChar + "child.txt", [], deep: false));
        Assert.True(mgr.CheckWrite(PathFor("a"), [entry.Token], deep: false));

        Assert.True(mgr.Unlock(PathFor("a"), entry.Token));
        Assert.True(mgr.CheckWrite(PathFor("a"), [], deep: false));
    }

    [Fact]
    public void SecondExclusive_Conflicts()
    {
        var mgr = new DavLockManager();
        Assert.NotNull(mgr.Create(PathFor("b"), true, true, "", null));
        Assert.Null(mgr.Create(PathFor("b"), true, true, "", null));
    }

    [Fact]
    public void SharedShared_Coexists_ExclusiveConflicts()
    {
        var mgr = new DavLockManager();
        Assert.NotNull(mgr.Create(PathFor("c"), false, true, "", null));
        Assert.NotNull(mgr.Create(PathFor("c"), false, true, "", null));
        Assert.Null(mgr.Create(PathFor("c"), true, true, "", null));
    }

    [Fact]
    public void DepthZero_DoesNotCoverChild_ButChildBlocksParentDelete()
    {
        var mgr = new DavLockManager();
        var child = PathFor("d") + Path.DirectorySeparatorChar + "f.txt";
        var entry = mgr.Create(child, true, false, "", null);
        Assert.NotNull(entry);

        // depth 0 锁不覆盖兄弟路径
        Assert.True(mgr.CheckWrite(PathFor("d") + Path.DirectorySeparatorChar + "other.txt", [], deep: false));
        // 但删父集合时，后代锁阻止（deep 整树语义）
        Assert.False(mgr.CheckWrite(PathFor("d"), [], deep: true));
        // 凭令牌放行
        Assert.True(mgr.CheckWrite(PathFor("d"), [entry.Token], deep: true));
    }

    [Fact]
    public void Expired_LockNoLongerBlocks()
    {
        var mgr = new DavLockManager();
        var entry = mgr.Create(PathFor("e"), true, true, "", TimeSpan.FromMilliseconds(1));
        Assert.NotNull(entry);
        Thread.Sleep(50);
        Assert.True(mgr.CheckWrite(PathFor("e"), [], deep: false));
    }

    [Fact]
    public void Refresh_Extends_And_WrongTokenFails()
    {
        var mgr = new DavLockManager();
        var entry = mgr.Create(PathFor("f"), true, true, "", TimeSpan.FromMinutes(1));
        Assert.NotNull(entry);
        Assert.Null(mgr.Refresh(PathFor("f"), "opaquelocktoken:nope", null));
        var refreshed = mgr.Refresh(PathFor("f"), entry.Token, TimeSpan.FromHours(2));
        Assert.NotNull(refreshed);
        Assert.False(mgr.CheckWrite(PathFor("f"), [], deep: false)); // 续期后仍有效
    }

    [Fact]
    public void Refresh_OnlyCoversLockedPath()
    {
        var mgr = new DavLockManager();
        var entry = mgr.Create(PathFor("g"), true, false, "", null);
        Assert.NotNull(entry);
        Assert.Null(mgr.Refresh(PathFor("g") + "-elsewhere", entry.Token, null));
    }

    [Theory]
    [InlineData("Second-3600", 3600)]
    [InlineData("Infinite", null)]
    [InlineData(null, 3600)]
    public void ParseTimeout_Maps(string? header, int? expectedSeconds)
    {
        var ts = DavLockManager.ParseTimeout(header);
        if (expectedSeconds is null)
            Assert.Null(ts);
        else
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds.Value), ts);
    }

    [Theory]
    [InlineData("<D:lockscope><D:exclusive/></D:lockscope>", true)]
    [InlineData("<D:lockscope><D:shared/></D:lockscope>", false)]
    [InlineData("", true)]
    public void ParseLockBody_Scope(string body, bool exclusive)
    {
        Assert.Equal(exclusive, DavLockManager.ParseLockBody(body).Exclusive);
    }
}
