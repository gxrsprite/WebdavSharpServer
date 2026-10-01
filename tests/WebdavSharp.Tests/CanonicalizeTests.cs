using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>虚拟路径规范化单测（对齐 hacdias cleanPath）。</summary>
public class CanonicalizeTests
{
    [Theory]
    [InlineData("media/a/b", "media/a/b")]
    [InlineData("/media/a/b", "media/a/b")]
    [InlineData("media/./a", "media/a")]
    [InlineData("media/x/../a", "media/a")]
    [InlineData("media/a/", "media/a/")]
    [InlineData("media/a/.", "media/a/")]
    [InlineData("media/a/..", "media/")]
    [InlineData("", "")]
    [InlineData("/", "")]
    public void Canonicalize_ResolvesDots(string input, string expected)
    {
        Assert.Equal(expected, DavPathSecurity.CanonicalizeVirtualPath(input));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("media/../../x")]
    [InlineData("/..")]
    public void Canonicalize_Escape_ReturnsNull(string input)
    {
        Assert.Null(DavPathSecurity.CanonicalizeVirtualPath(input));
    }

    [Fact]
    public void Canonicalize_BypassAttempt_NormalizesIntoDeniedPrefix()
    {
        // "/allowed/../denied/x" 规范化后落到 denied 前缀下，规则可正常命中
        Assert.Equal("denied/x", DavPathSecurity.CanonicalizeVirtualPath("allowed/../denied/x"));
    }
}
