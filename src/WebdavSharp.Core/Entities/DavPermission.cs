namespace WebdavSharp.Core.Entities;

/// <summary>
/// WebDAV 权限位。对齐 hacdias/webdav 的 CRUD 字母：
/// C=Create / R=Read / U=Update / D=Delete。
/// </summary>
[Flags]
public enum DavPermission
{
    None = 0,
    Read = 1,
    Create = 2,
    Update = 4,
    Delete = 8,
    All = Read | Create | Update | Delete,
}

/// <summary>
/// 权限字母与 <see cref="DavPermission"/> 互转（大小写不敏感，"none" 显式拒绝）。
/// </summary>
public static class DavPermissionParser
{
    public static DavPermission Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return DavPermission.None;
        text = text.Trim();
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
            return DavPermission.None;

        var result = DavPermission.None;
        foreach (var ch in text.ToUpperInvariant())
        {
            result |= ch switch
            {
                'R' => DavPermission.Read,
                'C' => DavPermission.Create,
                'U' => DavPermission.Update,
                'D' => DavPermission.Delete,
                _ => throw new FormatException($"未知权限字母 '{ch}'（仅允许 C/R/U/D 或 none）。"),
            };
        }
        return result;
    }

    public static string Format(DavPermission permissions)
    {
        if (permissions == DavPermission.None)
            return "none";
        var sb = new System.Text.StringBuilder();
        if (permissions.HasFlag(DavPermission.Create)) sb.Append('C');
        if (permissions.HasFlag(DavPermission.Read)) sb.Append('R');
        if (permissions.HasFlag(DavPermission.Update)) sb.Append('U');
        if (permissions.HasFlag(DavPermission.Delete)) sb.Append('D');
        return sb.ToString();
    }
}
