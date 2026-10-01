using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>
/// 权限求值单测：锁定 hacdias 核心语义（最后一条命中生效 / 尾斜杠覆盖集合自身 /
/// regex 字面匹配 / 大小写不敏感 / none 显式拒绝）。
/// </summary>
public class PermissionEvaluatorTests
{
    private static DavRule PathRule(string pattern, string perms, int sort = 0) =>
        new() { Kind = DavRuleKind.Path, Pattern = pattern, Permissions = perms, SortOrder = sort };

    private static DavRule RegexRule(string pattern, string perms, int sort = 0) =>
        new() { Kind = DavRuleKind.Regex, Pattern = pattern, Permissions = perms, SortOrder = sort };

    [Fact]
    public void NoMatch_FallsBackToDefault()
    {
        var result = DavPermissionEvaluator.Evaluate("/media/a.txt", DavPermission.Read, []);
        Assert.Equal(DavPermission.Read, result);
    }

    [Fact]
    public void LastMatch_Wins()
    {
        var rules = new[]
        {
            PathRule("/public/", "CRUD", sort: 1),
            PathRule("/public/private/", "none", sort: 2),
        };
        var result = DavPermissionEvaluator.Evaluate("/public/private/a.txt", DavPermission.Read, rules);
        Assert.Equal(DavPermission.None, result);
    }

    [Fact]
    public void TrailingSlash_CoversCollectionItself()
    {
        var rules = new[] { PathRule("/secret/", "none") };
        Assert.Equal(DavPermission.None,
            DavPermissionEvaluator.Evaluate("/secret", DavPermission.All, rules));
        Assert.Equal(DavPermission.None,
            DavPermissionEvaluator.Evaluate("/secret/file.txt", DavPermission.All, rules));
    }

    [Fact]
    public void Regex_MatchesLiterally_NoPrefixHandling()
    {
        var rules = new[] { RegexRule("^.+.js$", "RU") };
        Assert.Equal(DavPermission.Read | DavPermission.Update,
            DavPermissionEvaluator.Evaluate("/public/app.js", DavPermission.None, rules));
        Assert.Equal(DavPermission.None,
            DavPermissionEvaluator.Evaluate("/public/app.ts", DavPermission.None, rules));
    }

    [Fact]
    public void PathMatch_IsCaseInsensitive_OnWindowsRoots()
    {
        var rules = new[] { PathRule("/Secret/", "none") };
        Assert.Equal(DavPermission.None,
            DavPermissionEvaluator.Evaluate("/SECRET/file.txt", DavPermission.All, rules));
    }

    [Theory]
    [InlineData("CRUD", DavPermission.All)]
    [InlineData("ru", DavPermission.Read | DavPermission.Update)]
    [InlineData("none", DavPermission.None)]
    public void Parser_RoundTrips(string text, DavPermission expected)
    {
        Assert.Equal(expected, DavPermissionParser.Parse(text));
    }
}
