namespace WebdavSharp.Core.Services;

/// <summary>
/// <c>{env}</c> 占位解析。对齐 hacdias user.go：值以 <c>{env}</c> 开头时取其余部分
/// 为环境变量名；变量未设置或为空 → 启动期直接报错（绝不静默放行空口令）。
/// ASP.NET Core 自带的 <c>Dav__Port</c> 式环境覆盖已等价于参考的 <c>WD_</c> 前缀，
/// 此处只处理值内的 <c>{env}</c> 占位（主要用于种子口令）。
/// </summary>
public static class EnvPlaceholder
{
    public const string Prefix = "{env}";

    public static bool IsPlaceholder(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Resolve(string? value, string fieldName)
    {
        if (!IsPlaceholder(value))
            return value ?? string.Empty;
        var name = value[Prefix.Length..];
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException($"配置 {fieldName}：{{env}} 后缺少环境变量名。");
        var resolved = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(resolved))
            throw new InvalidOperationException($"配置 {fieldName}：环境变量 {name} 未设置或为空。");
        return resolved;
    }
}
