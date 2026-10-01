using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>
/// 可见性与权限求值单测：可见与权限相互独立，均取“最后一条命中规则”。
/// 默认可见（便于“全勾选=全放开”）。
/// </summary>
public class VisibilityRuleTests
{
    private static DavRule Rule(string pattern, string perms, bool visible = true, int sort = 0) =>
        new()
        {
            Kind = DavRuleKind.Path,
            Pattern = pattern,
            Permissions = perms,
            IsVisible = visible,
            SortOrder = sort,
        };

    [Fact]
    public void Invisible_IndependentOfPermission()
    {
        // 可读但不可见：能访问，但不出现在列表
        var rules = new[] { Rule("/secret/", "R", visible: false) };
        var access = DavPermissionEvaluator.EvaluateEffective("/secret/a.txt", DavPermission.None, rules);
        Assert.Equal(DavPermission.Read, access.Permissions);
        Assert.False(access.IsVisible);
    }

    [Fact]
    public void DefaultIsVisible()
    {
        // 新规则默认可见；无命中时也可见
        Assert.True(new DavRule().IsVisible);
        Assert.True(new DavMount().IsVisible);
        Assert.True(DavPermissionEvaluator.EvaluateEffective("/x", DavPermission.Read, []).IsVisible);
        Assert.True(DavPermissionEvaluator.IsVisible("/x", []));
    }

    [Fact]
    public void LastMatchWins_ForVisibility()
    {
        var rules = new[]
        {
            Rule("/s/", "R", visible: false, sort: 1),
            Rule("/s/open/", "R", visible: true, sort: 2),
        };
        Assert.True(DavPermissionEvaluator.EvaluateEffective("/s/open/a.txt", DavPermission.None, rules).IsVisible);
        Assert.False(DavPermissionEvaluator.EvaluateEffective("/s/other/a.txt", DavPermission.None, rules).IsVisible);
    }

    [Fact]
    public void DisabledRule_DoesNotAffectVisibility()
    {
        var rules = new[]
        {
            new DavRule { Kind = DavRuleKind.Path, Pattern = "/h/", Permissions = "R", IsVisible = false, IsEnabled = false },
        };
        Assert.True(DavPermissionEvaluator.EvaluateEffective("/h/a", DavPermission.Read, rules).IsVisible);
    }

    [Fact]
    public void Evaluate_StillReturnsPermissionOnly()
    {
        // 旧 API 行为不变（多处调用点依赖）
        var rules = new[] { Rule("/r/", "RU", visible: false) };
        Assert.Equal(DavPermission.Read | DavPermission.Update,
            DavPermissionEvaluator.Evaluate("/r/a", DavPermission.None, rules));
    }
}

/// <summary>规则协议作用域单测：空 = 适配所有协议。</summary>
public class RuleScopeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyProtocols_AppliesToEverything(string? protocols)
    {
        foreach (var p in DavProtocols.All)
            Assert.True(DavRuleScope.AppliesTo(protocols, p));
    }

    [Theory]
    [InlineData("webdav", DavProtocols.WebDav, true)]
    [InlineData("webdav", DavProtocols.Smb, false)]
    [InlineData("webdav", DavProtocols.Ftp, false)]
    [InlineData("smb,ftp", DavProtocols.Smb, true)]
    [InlineData("smb,ftp", DavProtocols.Ftp, true)]
    [InlineData("smb,ftp", DavProtocols.WebDav, false)]
    [InlineData("smb, ftp", DavProtocols.Ftp, true)]   // 容忍空格
    [InlineData("SMB", DavProtocols.Smb, true)]        // 大小写不敏感
    public void ScopedProtocols(string protocols, string protocol, bool expected)
    {
        Assert.Equal(expected, DavRuleScope.AppliesTo(protocols, protocol));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("ftp,smb", "smb,ftp")]              // 规范化后固定顺序
    [InlineData("smb,smb,ftp", "smb,ftp")]          // 去重
    [InlineData("webdav,unknown", "webdav")]        // 丢弃未知协议名
    [InlineData("WEBDAV", "webdav")]                // 小写化
    public void Normalize(string? input, string expected)
    {
        Assert.Equal(expected, DavRuleScope.Normalize(input));
    }

    [Fact]
    public void Describe_ForUi()
    {
        Assert.Equal("全部协议", DavRuleScope.Describe(""));
        Assert.Equal("全部协议", DavRuleScope.Describe(null));
        Assert.Equal("smb,ftp", DavRuleScope.Describe("SMB,FTP"));
    }
}
