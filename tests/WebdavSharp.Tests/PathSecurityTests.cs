using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>路径安全单测：穿越拦截 / 挂载拆分。</summary>
public class PathSecurityTests
{
    [Fact]
    public void ResolveSafePath_AllowsInside()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var full = DavPathSecurity.ResolveSafePath(root, "a/b.txt");
            Assert.StartsWith(Path.GetFullPath(root), full);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/absolute/escape.txt")]
    public void ResolveSafePath_BlocksTraversal(string evil)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // "/absolute/..." 会被 TrimStart 后视为相对路径（仍在根内，不抛）——
            // 真正危险的是 .. 逃逸；此处断言 .. 必抛。
            if (evil.Contains(".."))
                Assert.Throws<DavPathTraversalException>(() => DavPathSecurity.ResolveSafePath(root, evil));
            else
                Assert.StartsWith(Path.GetFullPath(root), DavPathSecurity.ResolveSafePath(root, evil));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("media/a/b", "media", "a/b")]
    [InlineData("media", "media", "")]
    [InlineData("/media/a", "media", "a")]
    public void SplitMount_SplitsFirstSegment(string virtualPath, string mount, string rel)
    {
        Assert.Equal((mount, rel), DavPathSecurity.SplitMount(virtualPath));
    }

    /// <summary>
    /// 回归：挂载到盘符根（J:\）时，根路径尾分隔符曾导致前缀比对失败，
    /// 表现为读正常但所有写操作 403 "path escapes mount root"。
    /// </summary>
    [Fact]
    public void ResolveSafePath_Root_AllowsChildren()
    {
        // 跨平台：Windows 盘符根 "C:\" / Unix 根 "/" 都必须能正常拼子路径
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), root);

        var full = DavPathSecurity.ResolveSafePath(root, "some/sub/file.txt");
        var expected = Path.GetFullPath(Path.Combine(root, "some", "sub", "file.txt"));
        Assert.Equal(expected, full);
    }

    [Fact]
    public void ResolveSafePath_Root_MountRootItself()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(Path.GetFullPath(root), DavPathSecurity.ResolveSafePath(root, ""));
    }

    [Fact]
    public void ResolveSafePath_Root_DotDotStaysInside()
    {
        // 根没有上级，"..\x" 会被钳制回根内；关键是没指到根外
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var full = DavPathSecurity.ResolveSafePath(root, ".." + Path.DirectorySeparatorChar + "escape.txt");
        Assert.StartsWith(Path.GetFullPath(root), full, PlatformPath.Comparison);
    }

    [Fact]
    public void ResolveSafePath_Subdir_StillBlocksTraversal()
    {
        // 真正能逃逸的场景（子目录挂载点）必须抛。
        var sub = Path.Combine(Path.GetTempPath(), "davsec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sub);
        try
        {
            Assert.Throws<DavPathTraversalException>(
                () => DavPathSecurity.ResolveSafePath(sub, ".." + Path.DirectorySeparatorChar + "escape.txt"));
        }
        finally
        {
            Directory.Delete(sub, true);
        }
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\data\")]
    [InlineData(@"C:\data")]
    public void NormalizeRoot_KeepsDriveRootButTrimsOthers(string input)
    {
        // 这一组专门测 Windows 盘符根形态的归一化（纯字符串逻辑），任何平台都可断言
        var normalized = DavPathSecurity.NormalizeRoot(input);
        if (input == @"C:\")
            Assert.Equal(@"C:\", normalized);          // 盘符根保持
        else
            Assert.Equal(@"C:\data", normalized);      // 其余去尾分隔符
    }

    [Fact]
    public void NormalizeRoot_UnixRoot_KeptAsIs()
    {
        // Unix 根 "/" 去尾会变空串（等于相对当前盘），必须原样保留
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal(sep.ToString(), DavPathSecurity.NormalizeRoot(sep.ToString()));
    }

    [Fact]
    public void NormalizeRoot_UnixSubdir_TrimsTrailingSeparator()
    {
        var sep = Path.DirectorySeparatorChar;
        var input = $"{sep}data{sep}nas{sep}";
        Assert.Equal($"{sep}data{sep}nas", DavPathSecurity.NormalizeRoot(input));
    }

    /// <summary>物理路径比较必须跟随平台（Linux 敏感，Win/macOS 不敏感）。</summary>
    [Fact]
    public void PlatformPath_ComparisonMatchesOsSemantics()
    {
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        Assert.Equal(expected, PlatformPath.Comparison);
        Assert.Equal(expected == StringComparison.OrdinalIgnoreCase,
            PlatformPath.Comparer.Equals("a", "A"));
    }

    [Fact]
    public void PlatformPath_SystemRoot_IsAbsolute()
    {
        Assert.True(Path.IsPathRooted(PlatformPath.SystemRoot));
        Assert.False(string.IsNullOrWhiteSpace(PlatformPath.SystemRoot));
    }
}
