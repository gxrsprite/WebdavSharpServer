using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>大小写探测与比较模式单测（对齐 hacdias casefold.go）。</summary>
public class CaseProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "davcase-" + Guid.NewGuid().ToString("N"));

    public CaseProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        DavCaseProbe.ClearCache();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void FlipFirstCased_FlipsOnlyFirst()
    {
        Assert.Equal("Abc", DavCaseProbe.FlipFirstCased("abc"));
        Assert.Equal("aBC", DavCaseProbe.FlipFirstCased("ABC"));
        Assert.Null(DavCaseProbe.FlipFirstCased("123-_"));
    }

    [Fact]
    public void Probe_ExistingDir_ReturnsOsBehavior()
    {
        // 当前卷上翻转后必然存在（同一目录）→ 不敏感；与平台默认一致断言太强，
        // 只断言探测有确定结果且与 ComparisonFor 一致。
        var insensitive = DavCaseProbe.Probe(_root);
        var comparison = DavCaseProbe.ComparisonFor(_root);
        Assert.Equal(insensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal, comparison);
    }

    [Fact]
    public void Probe_MissingDir_FallsBackToPlatformDefault()
    {
        var missing = Path.Combine(_root, "no-such-dir");
        Assert.Equal(DavCaseProbe.PlatformDefault, DavCaseProbe.Probe(missing));
    }

    [Fact]
    public void Fold_Lowercases_And_Normalizes()
    {
        Assert.Equal("abc", DavCaseProbe.Fold("ABC"));
        Assert.Equal("é".Normalize(System.Text.NormalizationForm.FormC),
            DavCaseProbe.Fold("É".Normalize(System.Text.NormalizationForm.FormD)));
    }

    private static DavRule PathRule(string pattern, string perms) =>
        new() { Kind = DavRuleKind.Path, Pattern = pattern, Permissions = perms };

    [Fact]
    public void Evaluator_SensitiveMode_DistinguishesCase()
    {
        var rules = new[] { PathRule("/secret/", "none") };
        // 敏感模式：大小写不同即不命中 → 回退默认
        Assert.Equal(DavPermission.All,
            DavPermissionEvaluator.Evaluate("/SECRET/f.txt", DavPermission.All, rules, StringComparison.Ordinal));
        // 完全一致才命中
        Assert.Equal(DavPermission.None,
            DavPermissionEvaluator.Evaluate("/secret/f.txt", DavPermission.All, rules, StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluator_InsensitiveRegex_MatchesFolded()
    {
        var rules = new[] { new DavRule { Kind = DavRuleKind.Regex, Pattern = @"^/pub/.*\.js$", Permissions = "R" } };
        Assert.Equal(DavPermission.Read,
            DavPermissionEvaluator.Evaluate("/PUB/app.JS", DavPermission.None, rules, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DavPermission.None,
            DavPermissionEvaluator.Evaluate("/PUB/app.JS", DavPermission.None, rules, StringComparison.Ordinal));
    }
}
