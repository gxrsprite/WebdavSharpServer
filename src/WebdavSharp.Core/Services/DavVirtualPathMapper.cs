using WebdavSharp.Core.Entities;

namespace WebdavSharp.Core.Services;

/// <summary>
/// 物理路径 → 虚拟路径（规则 pattern）映射。
/// 「浏览」控件给出的是服务端真实路径，而规则/权限按虚拟路径匹配，
/// 因此需要用挂载表反查：物理路径落在某挂载根下 → <c>/挂载名/相对路径</c>。
/// 用户主目录（RootPath）命中时 → <c>/相对路径</c>。
/// </summary>
public static class DavVirtualPathMapper
{
    public sealed record MapResult(bool Ok, string VirtualPath, string? Reason);

    /// <summary>目录映射为集合路径（补尾斜杠，正好命中「集合自身也可匹配」语义）。</summary>
    public static MapResult MapDirectory(string physicalPath, IEnumerable<DavMount> mounts, string? userRootPath = null)
        => Map(physicalPath, mounts, isDirectory: true, userRootPath);

    public static MapResult MapFile(string physicalPath, IEnumerable<DavMount> mounts, string? userRootPath = null)
        => Map(physicalPath, mounts, isDirectory: false, userRootPath);

    private static MapResult Map(string physicalPath, IEnumerable<DavMount> mounts, bool isDirectory, string? userRootPath)
    {
        if (string.IsNullOrWhiteSpace(physicalPath))
            return new MapResult(false, string.Empty, "路径为空。");

        string full;
        try
        {
            full = Path.GetFullPath(physicalPath);
        }
        catch (Exception ex)
        {
            return new MapResult(false, string.Empty, $"路径非法：{ex.Message}");
        }

        // 1) 用户主目录优先（与 DavRootResolver 的优先序一致）
        if (!string.IsNullOrWhiteSpace(userRootPath))
        {
            var root = SafeFullPath(userRootPath);
            if (root is not null && IsUnder(full, root, out var relFromRoot, PlatformPath.Comparison))
                return Build(string.Empty, relFromRoot, isDirectory);
        }

        // 2) 挂载：取最长匹配的挂载根（嵌套挂载时更具体者胜）
        DavMount? best = null;
        var bestRel = string.Empty;
        var bestLen = -1;
        foreach (var mount in mounts.Where(m => m.IsEnabled))
        {
            var root = SafeFullPath(mount.PhysicalPath);
            if (root is null)
                continue;
            if (!IsUnder(full, root, out var rel, PlatformPath.Comparison))
                continue;
            if (root.Length > bestLen)
            {
                best = mount;
                bestRel = rel;
                bestLen = root.Length;
            }
        }

        if (best is not null)
            return Build(best.Name, bestRel, isDirectory);

        return new MapResult(false, string.Empty,
            "该路径不属于任何已启用的挂载（也不是用户主目录），无法转为虚拟路径。请先添加对应的虚拟挂载。");
    }

    private static MapResult Build(string mountPrefix, string relative, bool isDirectory)
    {
        var rel = (relative ?? string.Empty).Replace('\\', '/').Trim('/');
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(mountPrefix))
            parts.Add(mountPrefix);
        if (!string.IsNullOrEmpty(rel))
            parts.Add(rel);

        var path = "/" + string.Join('/', parts);
        // 集合：尾斜杠（path 规则的“集合自身可匹配”语义靠它生效）
        if (isDirectory && path.Length > 1 && !path.EndsWith('/'))
            path += "/";
        return new MapResult(true, path, null);
    }

    private static string? SafeFullPath(string p)
    {
        try
        {
            return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>target 是否在 root 之内（含 root 自身），是则给出相对路径。按平台大小写语义比较。</summary>
    private static bool IsUnder(string target, string root, out string relative, StringComparison compare)
    {
        var sep = Path.DirectorySeparatorChar;
        // 保留根形态：Unix 根 "/" 去尾会变空串，故只去“多余”的尾分隔符
        var t = TrimTrailingSeparators(target);
        var r = TrimTrailingSeparators(root);

        if (string.Equals(t, r, compare))
        {
            relative = string.Empty;
            return true;
        }

        // 前缀补成单分隔符结尾（r 可能是 "" —— 对应 Unix 根 "/"）
        var prefix = r.Length == 0 || r.EndsWith(sep) ? r : r + sep;
        if (prefix.Length == 0)
            prefix = sep.ToString(); // Unix 根

        if (!t.StartsWith(prefix, compare))
        {
            relative = string.Empty;
            return false;
        }
        relative = t[prefix.Length..];
        return true;
    }

    /// <summary>去掉多余的尾分隔符；Unix 根 "/" 归一化为空串（便于前缀拼接）。</summary>
    private static string TrimTrailingSeparators(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        var trimmed = path.TrimEnd(sep);
        // Windows 盘符根 "C:\" 去尾会变 "C:"（=驱动器相对路径，语义不同）→ 保留分隔符
        if (trimmed.Length == 2 && trimmed[1] == ':')
            return trimmed + sep;
        return trimmed;
    }
}
