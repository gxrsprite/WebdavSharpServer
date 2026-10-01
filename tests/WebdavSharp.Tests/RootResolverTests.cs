using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>用户主目录解析单测（对齐 hacdias per-user directory）。</summary>
public class RootResolverTests
{
    private static readonly List<DavMount> Mounts =
    [
        new() { Name = "media", PhysicalPath = "/data/media" },
    ];

    [Fact]
    public void RootPathSet_SkipsMountSplit()
    {
        var r = DavRootResolver.Resolve("/home/alice", "media/a.txt", Mounts, "/data");
        Assert.Equal("/home/alice", r.PhysicalRoot);
        Assert.Equal("media/a.txt", r.MountRel);
        Assert.Null(r.Mount);
    }

    [Fact]
    public void NoRootPath_MountHit()
    {
        var r = DavRootResolver.Resolve("", "media/a.txt", Mounts, "/data");
        Assert.Equal("/data/media", r.PhysicalRoot);
        Assert.Equal("a.txt", r.MountRel);
        Assert.NotNull(r.Mount);
    }

    [Fact]
    public void NoRootPath_Fallback()
    {
        var r = DavRootResolver.Resolve("  ", "other/a.txt", Mounts, "/data");
        Assert.Equal("/data", r.PhysicalRoot);
        Assert.Equal("other/a.txt", r.MountRel);
        Assert.Null(r.Mount);
    }
}
