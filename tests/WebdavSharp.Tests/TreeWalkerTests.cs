using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>DavTreeWalker 单测：遍历 / 整树判定 / 过滤式集合拷贝。</summary>
public class TreeWalkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "davwalker-" + Guid.NewGuid().ToString("N"));

    public TreeWalkerTests()
    {
        // tree/
        //   a.txt
        //   sub/b.txt
        //   denied/c.txt
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(Path.Combine(_root, "denied"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), "b");
        File.WriteAllText(Path.Combine(_root, "denied", "c.txt"), "c");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
        var dest = _root + "-dest";
        if (Directory.Exists(dest))
            Directory.Delete(dest, true);
    }

    private static DavRule PathRule(string pattern, string perms) =>
        new() { Kind = DavRuleKind.Path, Pattern = pattern, Permissions = perms };

    [Fact]
    public void Walk_EnumeratesAllDescendants()
    {
        var rels = DavTreeWalker.Walk(_root).Select(x => x.RelativePath).OrderBy(x => x).ToList();
        Assert.Equal(["a.txt", "denied", "denied/c.txt", "sub", "sub/b.txt"], rels);
    }

    [Fact]
    public void AllSatisfy_FalseWhenDescendantDenied()
    {
        var rules = new[] { PathRule("/t/denied/", "none") };
        Assert.False(DavTreeWalker.AllSatisfy(_root, "/t", DavPermission.All, rules, DavPermission.Delete));
        Assert.False(DavTreeWalker.AllSatisfy(_root, "/t", DavPermission.All, rules, DavPermission.Read));
    }

    [Fact]
    public void AllSatisfy_TrueWhenAllAllowed()
    {
        Assert.True(DavTreeWalker.AllSatisfy(_root, "/t", DavPermission.All, [], DavPermission.Read | DavPermission.Delete));
    }

    [Fact]
    public void CopyWhereAllowed_LeavesDeniedEntriesBehind()
    {
        var dest = _root + "-dest";
        var rules = new[] { PathRule("/t/denied/", "none") };
        DavTreeWalker.CopyWhereAllowed(_root, dest, "/t", DavPermission.All, rules);

        Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "sub", "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(dest, "denied")));
        Assert.False(File.Exists(Path.Combine(dest, "denied", "c.txt")));
    }

    [Fact]
    public void CopyWhereAllowed_FullCopyWhenNoDenies()
    {
        var dest = _root + "-dest";
        DavTreeWalker.CopyWhereAllowed(_root, dest, "/t", DavPermission.Read, []);
        Assert.True(File.Exists(Path.Combine(dest, "denied", "c.txt")));
    }
}
