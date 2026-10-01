namespace WebdavSharp.Core.Services;

/// <summary>虚拟路径触及挂载根之外时抛出（如 /../ 逃逸、未知挂载）。</summary>
public sealed class DavPathTraversalException(string message) : Exception(message);

/// <summary>
/// 路径安全：虚拟路径 → 物理路径映射 + 穿越拦截。
/// 虚拟路径首段为挂载名（/media/a/b → 挂载 media + 相对路径 a/b）；
/// 未命中挂载时走单根回退（整段视为相对路径）。
/// </summary>
public static class DavPathSecurity
{
    /// <summary>
    /// 规范化虚拟路径（对齐参考 cleanPath）：消解 "." / ".." 段，保留集合尾斜杠
    /// （"/c/"、"/c/."、"/c/.." 都指向集合自身）；".." 试图越过虚拟根时返回 null，
    /// 调用方应直接 403——必须在权限求值之前调用，否则 "/allowed/../denied/x"
    /// 会按字面绕过规则匹配（物理层在单根回退时不一定抛）。
    /// 输入为相对虚拟路径（如 "media/a"）或绝对式（"/media/a"），输出不带前导斜杠。
    /// </summary>
    public static string? CanonicalizeVirtualPath(string? virtualPath)
    {
        var raw = (virtualPath ?? string.Empty).Trim();
        var isCollection = raw.EndsWith('/') || raw.EndsWith("/.") || raw.EndsWith("/..");
        var stack = new List<string>();
        foreach (var seg in raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
                continue;
            if (seg == "..")
            {
                if (stack.Count == 0)
                    return null; // 越过虚拟根
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(seg);
        }
        var result = string.Join('/', stack);
        if (isCollection && result.Length > 0)
            result += "/";
        return result;
    }
    /// <summary>
    /// 把请求相对路径（相对挂载物理根）解析为物理全路径，逃逸则抛异常。
    /// 拼接规则借鉴 <c>StringUtilities.Combine</c>（Demo/Library）：
    /// 根去尾 + 单分隔符拼接，保证盘符根 <c>J:\</c> 的子路径前缀是 <c>J:\</c>
    /// 而不是 <c>J:\\</c>（双分隔符曾导致挂载到盘根时读正常、写全 403）。
    /// </summary>
    public static string ResolveSafePath(
        string mountPhysicalRoot, string relativePath, StringComparison? pathComparison = null)
    {
        var compare = pathComparison ?? PlatformPath.Comparison;
        var root = NormalizeRoot(Path.GetFullPath(mountPhysicalRoot));
        var rel = (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, rel));

        // 子路径前缀：根已归一化（盘符根保留尾分隔符、其余去尾），
        // 结尾有分隔符直接用、没有则补一个——永远单分隔符。
        var childPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!full.Equals(root, compare) && !full.StartsWith(childPrefix, compare))
        {
            throw new DavPathTraversalException($"路径逃逸挂载根：{relativePath}");
        }
        return full;
    }

    /// <summary>
    /// 归一化物理根：去掉多余的尾分隔符，但保留盘符根的形态（<c>J:\</c> → <c>J:\</c>）。
    /// </summary>
    internal static string NormalizeRoot(string root)
    {
        if (string.IsNullOrEmpty(root))
            return root;
        // 盘符根 "X:\" 或 UNC "\\server\share\" 保持不动（去尾会变成 "X:"，语义变相对路径）
        if (root.Length >= 3 && root[1] == ':' && root[2] == Path.DirectorySeparatorChar && root.Length == 3)
            return root;
        if (root.EndsWith(Path.DirectorySeparatorChar) &&
            root.TrimEnd(Path.DirectorySeparatorChar) is { Length: > 0 } trimmed)
        {
            // "X:" 形式（无分隔符）补回一个分隔符，避免被当成驱动器相对路径
            if (trimmed.Length == 2 && trimmed[1] == ':')
                return trimmed + Path.DirectorySeparatorChar;
            return trimmed;
        }
        return root;
    }

    /// <summary>拆虚拟路径为 (挂载名, 挂载内相对路径)。"media/a/b" → ("media", "a/b")。</summary>
    public static (string MountName, string RelativePath) SplitMount(string virtualPath)
    {
        var trimmed = (virtualPath ?? string.Empty).Trim().TrimStart('/');
        var slash = trimmed.IndexOf('/');
        if (slash < 0)
            return (trimmed, string.Empty);
        return (trimmed[..slash], trimmed[(slash + 1)..]);
    }
}
