using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>
/// 物理路径 → 虚拟路径映射单测（浏览控件选完路径后转规则 pattern 用）。
/// 跨平台：测试夹具用临时目录构造，不写死 Windows 盘符；
/// 盘符根特异行为另有 <c>VirtualPathMapperWindowsTests</c> 分组保护。
/// </summary>
public class VirtualPathMapperTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "davmap-" + Guid.NewGuid().ToString("N"));

    public VirtualPathMapperTests()
    {
        Directory.CreateDirectory(Path.Combine(_base, "nas", "剧集"));
        Directory.CreateDirectory(Path.Combine(_base, "data", "public"));
        Directory.CreateDirectory(Path.Combine(_base, "homes", "alice", "docs"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
            Directory.Delete(_base, true);
    }

    private string P(params string[] parts) => Path.Combine([_base, .. parts]);

    private static List<DavMount> Mounts(params (string Name, string Path)[] items) =>
        items.Select(i => new DavMount { Name = i.Name, PhysicalPath = i.Path }).ToList();

    [Fact]
    public void DirectoryUnderMount_MapsToCollectionPath_WithTrailingSlash()
    {
        var mounts = Mounts(("NAS", P("nas")), ("public", P("data", "public")));
        var r = DavVirtualPathMapper.MapDirectory(P("nas", "剧集"), mounts);
        Assert.True(r.Ok);
        Assert.Equal("/NAS/剧集/", r.VirtualPath);
    }

    [Fact]
    public void FileUnderMount_MapsWithoutTrailingSlash()
    {
        var file = P("data", "public", "a.txt");
        File.WriteAllText(file, "x");
        var mounts = Mounts(("public", P("data", "public")));
        var r = DavVirtualPathMapper.MapFile(file, mounts);
        Assert.True(r.Ok);
        Assert.Equal("/public/a.txt", r.VirtualPath);
    }

    [Fact]
    public void MountRootItself_MapsToMountName()
    {
        var mounts = Mounts(("NAS", P("nas")));
        var r = DavVirtualPathMapper.MapDirectory(P("nas"), mounts);
        Assert.True(r.Ok);
        Assert.Equal("/NAS/", r.VirtualPath);
    }

    [Fact]
    public void OutsideAnyMount_Fails_WithActionableReason()
    {
        var mounts = Mounts(("public", P("data", "public")));
        var r = DavVirtualPathMapper.MapDirectory(Path.GetTempPath(), mounts);
        Assert.False(r.Ok);
        Assert.Contains("挂载", r.Reason);
    }

    [Fact]
    public void NestedMounts_LongestRootWins()
    {
        var mounts = Mounts(("outer", P("data")), ("inner", P("data", "public")));
        var r = DavVirtualPathMapper.MapDirectory(P("data", "public", "sub"), mounts);
        Assert.True(r.Ok);
        Assert.Equal("/inner/sub/", r.VirtualPath);
    }

    [Fact]
    public void DisabledMount_NotUsed()
    {
        var mounts = new List<DavMount>
        {
            new() { Name = "off", PhysicalPath = P("data"), IsEnabled = false },
        };
        Assert.False(DavVirtualPathMapper.MapDirectory(P("data", "public"), mounts).Ok);
    }

    [Fact]
    public void UserRootPath_TakesPrecedence()
    {
        var mounts = Mounts(("NAS", P("homes")));
        var r = DavVirtualPathMapper.MapDirectory(P("homes", "alice", "docs"), mounts, P("homes", "alice"));
        Assert.True(r.Ok);
        Assert.Equal("/docs/", r.VirtualPath);
    }

    [Fact]
    public void WindowsStyleVirtualNames_StillUseForwardSlashes()
    {
        // 虚拟路径一律 '/'；物理分隔符不得泄漏到 pattern 里
        var mounts = Mounts(("NAS", P("nas")));
        var r = DavVirtualPathMapper.MapDirectory(P("nas", "剧集"), mounts);
        Assert.True(r.Ok);
        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), r.VirtualPath.TrimStart('/').TrimEnd('/'));
    }
}

/// <summary>Windows 盘符根特异行为（其它平台自动跳过）。</summary>
public class VirtualPathMapperWindowsTests
{
    [Fact]
    public void DriveRoot_MapsToMountName()
    {
        if (!OperatingSystem.IsWindows())
            return; // 仅 Windows 有盘符根

        var drive = Path.GetPathRoot(Path.GetTempPath())!;
        var mounts = new List<DavMount> { new() { Name = "NAS", PhysicalPath = drive } };
        var r = DavVirtualPathMapper.MapDirectory(drive, mounts);
        Assert.True(r.Ok);
        Assert.Equal("/NAS/", r.VirtualPath);
    }
}
