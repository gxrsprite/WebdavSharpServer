namespace WebdavSharp.Core.Services;

/// <summary>
/// 挂载名校验。对齐 hacdias <c>validDirectoryMountName</c>：
/// 非空、不为 "." / ".."、不含 "/" 与 "\"（另加：首尾空白与重复由调用方检查）。
/// </summary>
public static class DavMountValidator
{
    public static bool IsValidName(string? name) =>
        GetError(name) is null;

    public static string? GetError(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "挂载名不能为空。";
        name = name.Trim();
        if (name is "." or "..")
            return "挂载名不能为 . 或 ..。";
        if (name.Contains('/') || name.Contains('\\'))
            return "挂载名不能包含 / 或 \\。";
        return null;
    }
}
