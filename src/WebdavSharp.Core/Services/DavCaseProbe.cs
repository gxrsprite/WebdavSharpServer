using System.Collections.Concurrent;

namespace WebdavSharp.Core.Services;

/// <summary>
/// 大小写探测与折叠。对齐 hacdias <c>casefold.go</c>：
/// 探测 backing 目录是否忽略大小写（翻转末级名称首个可翻转字母的大小写，
/// 翻转后依然存在即判不敏感；探测失败回退平台默认 Windows/macOS 不敏感）。
/// Fold = 小写化（Invariant）+ Unicode NFC 规范化。
/// 与参考的差异见方法注释（SameFile 极端碰撞场景）。
/// </summary>
public static class DavCaseProbe
{
    // 键是物理路径 → 用平台比较器（Linux 上大小写敏感，避免 /data/nas 与 /data/NAS 串味）
    private static readonly ConcurrentDictionary<string, bool> Cache = new(PlatformPath.Comparer);

    /// <summary>取物理根的大小写策略（带缓存）。</summary>
    public static StringComparison ComparisonFor(string physicalRoot) =>
        GetOrProbe(physicalRoot) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static void Invalidate(string physicalRoot)
    {
        try
        {
            Cache.TryRemove(Path.GetFullPath(physicalRoot), out _);
        }
        catch
        {
            // 路径非法：忽略
        }
    }

    public static void ClearCache() => Cache.Clear();

    private static bool GetOrProbe(string physicalRoot)
    {
        string full;
        try
        {
            full = Path.GetFullPath(physicalRoot);
        }
        catch
        {
            return PlatformDefault;
        }
        return Cache.GetOrAdd(full, Probe);
    }

    /// <summary>
    /// 探测：翻转末级名称首个大小写可往返字母后 stat，两次都存在即不敏感。
    /// 参考用 os.SameFile 排除“大小写敏感卷上恰好同时存在两种拼写”的极端情况；
    /// 此处用存在性判定（该场景会误判为不敏感——偏向拒绝侧，见 docs 说明）。
    /// </summary>
    public static bool Probe(string fullPath)
    {
        try
        {
            var dir = fullPath.TrimEnd(Path.DirectorySeparatorChar);
            // Unix 根 "/" 去尾变空串 → 回落到根自身再探测
            if (dir.Length == 0)
                dir = Path.DirectorySeparatorChar.ToString();
            var leaf = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(leaf) || !Exists(dir))
                return PlatformDefault;

            var flippedLeaf = FlipFirstCased(leaf);
            if (flippedLeaf is null)
                return PlatformDefault;

            var parent = Path.GetDirectoryName(dir) ?? dir;
            return Exists(Path.Combine(parent, flippedLeaf));
        }
        catch
        {
            return PlatformDefault;
        }
    }

    private static bool Exists(string path) =>
        Directory.Exists(path) || File.Exists(path);

    /// <summary>翻转首个大小写可往返字母；无此类字母返回 null（对齐 flipCase）。</summary>
    public static string? FlipFirstCased(string leaf)
    {
        var chars = leaf.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsLower(c))
            {
                var u = char.ToUpperInvariant(c);
                if (char.ToLowerInvariant(u) == c)
                {
                    chars[i] = u;
                    return new string(chars);
                }
            }
            else if (char.IsUpper(c))
            {
                var l = char.ToLowerInvariant(c);
                if (char.ToUpperInvariant(l) == c)
                {
                    chars[i] = l;
                    return new string(chars);
                }
            }
        }
        return null;
    }

    /// <summary>Fold：小写化 + NFC（对齐 foldPath；比较时对两侧同时应用）。</summary>
    public static string Fold(string path) =>
        path.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormC);

    public static bool PlatformDefault =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
}
