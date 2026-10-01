using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>挂载名校验单测：对齐 hacdias validDirectoryMountName。</summary>
public class MountValidatorTests
{
    [Theory]
    [InlineData("media")]
    [InlineData("backups-2026")]
    [InlineData("a")]
    public void ValidNames_Pass(string name)
    {
        Assert.True(DavMountValidator.IsValidName(name));
        Assert.Null(DavMountValidator.GetError(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    public void InvalidNames_Fail(string? name)
    {
        Assert.False(DavMountValidator.IsValidName(name));
        Assert.NotNull(DavMountValidator.GetError(name));
    }
}
