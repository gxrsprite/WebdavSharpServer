using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>{env} 占位与 NoPassword 委托认证单测（对齐 hacdias user.go / handler）。</summary>
public class EnvAndNoPasswordTests
{
    [Fact]
    public void Resolve_PlainValue_Passthrough()
    {
        Assert.Equal("admin", EnvPlaceholder.Resolve("admin", "f"));
        Assert.Equal("", EnvPlaceholder.Resolve(null, "f"));
        Assert.False(EnvPlaceholder.IsPlaceholder("admin"));
        Assert.True(EnvPlaceholder.IsPlaceholder("{env}X"));
    }

    [Fact]
    public void Resolve_FromEnvironment()
    {
        Environment.SetEnvironmentVariable("WEBDAVSHARP_TEST_PW", "s3cret");
        try
        {
            Assert.Equal("s3cret", EnvPlaceholder.Resolve("{env}WEBDAVSHARP_TEST_PW", "f"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBDAVSHARP_TEST_PW", null);
        }
    }

    [Fact]
    public void Resolve_MissingName_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => EnvPlaceholder.Resolve("{env}", "f"));
    }

    [Fact]
    public void Resolve_MissingVariable_Throws()
    {
        Environment.SetEnvironmentVariable("WEBDAVSHARP_TEST_ABSENT", null);
        Assert.Throws<InvalidOperationException>(
            () => EnvPlaceholder.Resolve("{env}WEBDAVSHARP_TEST_ABSENT", "f"));
    }

    private static (IFreeSql Fsql, DavAuthService Auth) CreateAuth()
    {
        var fsql = new FreeSql.FreeSqlBuilder()
            .UseConnectionString(FreeSql.DataType.Sqlite, "Data Source=:memory:")
            .Build();
        fsql.CodeFirst.SyncStructure<DavUser>();
        var hasher = new PasswordHasher();
        fsql.Insert(new DavUser { Username = "u1", PasswordHash = hasher.Hash("right") }).ExecuteAffrows();
        fsql.Insert(new DavUser { Username = "u2", IsEnabled = false, PasswordHash = hasher.Hash("right") }).ExecuteAffrows();
        return (fsql, new DavAuthService(fsql, hasher));
    }

    [Fact]
    public async Task NoPassword_AcceptsAnyPassword_ForEnabledUser()
    {
        var (_, auth) = CreateAuth();
        Assert.NotNull(await auth.AuthenticateBasicAsync("u1", "wrong", skipPasswordCheck: true));
        Assert.NotNull(await auth.AuthenticateBasicAsync("u1", "", skipPasswordCheck: true));
    }

    [Fact]
    public async Task NoPassword_StillRejects_UnknownOrDisabled()
    {
        var (_, auth) = CreateAuth();
        Assert.Null(await auth.AuthenticateBasicAsync("ghost", "x", skipPasswordCheck: true));
        Assert.Null(await auth.AuthenticateBasicAsync("u2", "right", skipPasswordCheck: true));
    }

    [Fact]
    public async Task NormalMode_RequiresPassword()
    {
        var (_, auth) = CreateAuth();
        Assert.NotNull(await auth.AuthenticateBasicAsync("u1", "right"));
        Assert.Null(await auth.AuthenticateBasicAsync("u1", "wrong"));
    }
}
