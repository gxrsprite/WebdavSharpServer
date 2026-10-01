namespace WebdavSharp.Core.Services;

/// <summary>
/// 物理路径的平台语义。跨平台要点：
/// Windows / macOS 默认大小写不敏感，Linux（及多数 Unix）默认敏感。
/// 物理路径的**比较与哈希**必须跟随平台，否则在大小写敏感卷上
/// <c>/data/nas</c> 与 <c>/data/NAS</c> 会被当成同一目录——
/// 既可能误判“路径在挂载根之内”（逃逸检查被绕过），也会让锁/遍历去重串味。
/// 虚拟路径（URL、规则 pattern）另当别论：那里用 <see cref="DavCaseProbe"/> 的探测结果。
/// </summary>
public static class PlatformPath
{
    /// <summary>平台默认路径比较方式（Win/macOS → OrdinalIgnoreCase，其余 → Ordinal）。</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>与 <see cref="Comparison"/> 对应的比较器（用于 HashSet/Dictionary 键）。</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>平台目录分隔符。</summary>
    public static char Separator => Path.DirectorySeparatorChar;

    /// <summary>系统根路径：Windows 为当前盘根（<c>C:\</c>），Unix 为 <c>/</c>。跨平台安全。</summary>
    public static string SystemRoot
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                // 不用 Environment.SystemDirectory（仅 Windows 有意义），
                // 这里取当前工作目录所在盘的根，Unix 上落到 "/"。
                return Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\";
            }
            return "/";
        }
    }
}
