using WebdavSharp.Core.Entities;

namespace WebdavSharp.Core.Services;

/// <summary>目录项（协议无关），供 WebDAV / SMB / FTP 列表使用。</summary>
public sealed record DavEntry(
    string Name,
    string VirtualPath,
    string PhysicalPath,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc);

/// <summary>
/// 一次连接/会话的访问上下文：绑定「用户 + 协议 + 适用规则」，
/// 提供虚拟路径 → 物理路径解析、权限判定与列表过滤。
/// <para>
/// WebDAV / SMB / FTP 三个协议共用本类型，确保“同一套规则”真正落在同一段代码上
/// （而不是三处各自实现、行为漂移）。协议差异只体现在 <see cref="Protocol"/> 上，
/// 规则通过 <see cref="DavRuleScope"/> 按协议过滤。
/// </para>
/// </summary>
public sealed class DavSession(
    DavUser user,
    string protocol,
    IReadOnlyList<DavMount> mounts,
    IReadOnlyList<DavRule> rules,
    DavPermission defaults,
    string fallbackDirectory)
{
    public DavUser User { get; } = user;

    /// <summary>当前协议（<see cref="DavProtocols"/> 之一）。</summary>
    public string Protocol { get; } = protocol;

    public IReadOnlyList<DavMount> Mounts { get; } = mounts;

    /// <summary>
    /// 已按协议过滤并排序的适用规则（最后命中者胜）。
    /// <para>
    /// 这里**再做一次协议过滤**（即使 <see cref="DavAccessService.LoadRulesAsync"/> 已过滤过）：
    /// 防止调用方误传未过滤的规则集，导致「SMB 会话套用了 WebDAV 专属规则」这类静默越权。
    /// 防御性冗余，代价可忽略。
    /// </para>
    /// </summary>
    public IReadOnlyList<DavRule> Rules { get; } = rules
        .Where(r => DavRuleScope.AppliesTo(r.Protocols, protocol))
        .OrderBy(r => r.SortOrder)
        .ToList();

    public DavPermission Defaults { get; } = defaults;

    /// <summary>按本次命中的 backing 物理根探测大小写策略。</summary>
    public StringComparison ComparisonFor(string physicalRoot) => DavCaseProbe.ComparisonFor(physicalRoot);

    /// <summary>虚拟根（该用户命名空间的根）。listSiblingMounts=true 时表示要列出挂载入口。</summary>
    public bool IsVirtualRoot(string canonicalVirtualPath) => string.IsNullOrEmpty(canonicalVirtualPath);

    /// <summary>解析结果：物理路径 + 该路径的规则路径。</summary>
    public sealed record Location(
        string CanonicalVirtualPath,
        string RulePath,
        string PhysicalPath,
        DavMount? Mount,
        bool IsRoot);

    /// <summary>
    /// 把（可能未规范化的）虚拟路径解析为物理路径。
    /// 越界（<c>..</c> 逃逸、或逃出挂载根）返回 null，调用方应拒绝访问。
    /// </summary>
    public Location? Resolve(string? virtualPath)
    {
        var canonical = DavPathSecurity.CanonicalizeVirtualPath(virtualPath);
        if (canonical is null)
            return null; // 越过虚拟根

        var resolved = DavRootResolver.Resolve(User.RootPath, canonical, Mounts, fallbackDirectory);
        var physicalRoot = resolved.PhysicalRoot;

        // 用户主目录允许相对路径：锚到全局根
        if (!Path.IsPathRooted(physicalRoot))
            physicalRoot = Path.GetFullPath(Path.Combine(fallbackDirectory, physicalRoot));

        // 根判定：虚拟根 = 无挂载且相对路径为空；挂载根同理由 resolved 给出
        var isRoot = string.IsNullOrEmpty(resolved.MountRel);

        try
        {
            var physical = DavPathSecurity.ResolveSafePath(physicalRoot, resolved.MountRel, PlatformPath.Comparison);
            return new Location(canonical, "/" + canonical.TrimStart('/'), physical, resolved.Mount, isRoot);
        }
        catch (DavPathTraversalException)
        {
            return null;
        }
    }

    /// <summary>该路径的权限（默认 + 规则求值）。</summary>
    public DavPermission PermissionsAt(Location location) =>
        DavPermissionEvaluator.Evaluate(location.RulePath, Defaults, Rules, ComparisonFor(RuleComparisonRoot(location)));

    /// <summary>是否可执行某类操作。</summary>
    public bool Allowed(Location location, DavPermission need) =>
        PermissionsAt(location).HasFlag(need);

    /// <summary>该条目是否应在列表中可见（权限 + 可见性双重判定）。</summary>
    public bool CanSee(Location location)
    {
        var access = DavPermissionEvaluator.EvaluateEffective(
            location.RulePath, Defaults, Rules, ComparisonFor(RuleComparisonRoot(location)));
        return access.IsVisible;
    }

    public bool Allowed(string virtualPath, DavPermission need)
    {
        var loc = Resolve(virtualPath);
        return loc is not null && Allowed(loc, need);
    }

    /// <summary>
    /// 列出目录内容：隐藏项与无 Read 权限的项都被过滤（与 WebDAV 语义一致）。
    /// 物理目录不存在或不可读时返回空列表。
    /// </summary>
    public IReadOnlyList<DavEntry> List(string? virtualPath, bool includeInvisible = false)
    {
        var loc = Resolve(virtualPath);
        if (loc is null || !Directory.Exists(loc.PhysicalPath))
            return [];

        var comparison = ComparisonFor(RuleComparisonRoot(loc));
        var baseRule = loc.RulePath.TrimEnd('/');
        var result = new List<DavEntry>();

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(loc.PhysicalPath);
        }
        catch
        {
            return [];
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name))
                continue;

            var childRule = baseRule + "/" + name;
            var access = DavPermissionEvaluator.EvaluateEffective(childRule, Defaults, Rules, comparison);
            if (!includeInvisible && !access.IsVisible)
                continue;
            if (!access.Permissions.HasFlag(DavPermission.Read))
                continue;

            var isDir = Directory.Exists(entry);
            long length = 0;
            DateTime lastWrite;
            try
            {
                if (isDir)
                {
                    lastWrite = Directory.GetLastWriteTimeUtc(entry);
                }
                else
                {
                    var fi = new FileInfo(entry);
                    length = fi.Length;
                    lastWrite = fi.LastWriteTimeUtc;
                }
            }
            catch
            {
                continue; // 竞态/无权限：跳过该条
            }

            result.Add(new DavEntry(name, childRule, entry, isDir, length, lastWrite));
        }

        return result
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 虚拟根挂载入口列表（与 WebDAV PROPFIND 虚拟根 / SMB 共享枚举一致）。
    /// 无用户主目录且有多挂载时，根列出挂载入口而非回退目录的物理子项；
    /// 否则（有主目录 / 无挂载）回退到普通物理列表。
    /// 过滤语义与 WebDAV AppendMounts 一致：不可见、无 Read 权限、
    /// 物理路径不存在的挂载都不列出（但知道 URL 仍可直达，权限照常校验）。
    /// </summary>
    public IReadOnlyList<DavEntry> ListRootEntries()
    {
        if (!string.IsNullOrWhiteSpace(User.RootPath) || Mounts.Count == 0)
            return List(string.Empty);

        var comparison = ComparisonFor(fallbackDirectory);
        var result = new List<DavEntry>();
        foreach (var mount in Mounts)
        {
            if (!mount.IsVisible)
                continue;
            var childRule = "/" + mount.Name;
            var access = DavPermissionEvaluator.EvaluateEffective(childRule, Defaults, Rules, comparison);
            if (!access.Permissions.HasFlag(DavPermission.Read) || !access.IsVisible)
                continue;
            DateTime lastWrite;
            try
            {
                if (!Directory.Exists(mount.PhysicalPath))
                    continue;
                lastWrite = Directory.GetLastWriteTimeUtc(mount.PhysicalPath);
            }
            catch
            {
                continue;
            }
            result.Add(new DavEntry(mount.Name, childRule, mount.PhysicalPath, true, 0, lastWrite));
        }
        return result
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>某路径的父目录（虚拟路径），已在根时返回自身。</summary>
    public static string ParentOf(string canonicalVirtualPath)
    {
        var trimmed = canonicalVirtualPath.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx <= 0 ? string.Empty : trimmed[..idx];
    }

    /// <summary>
    /// 大小写探测用的物理根：取本次解析的挂载根 / 用户主目录 / 全局根，
    /// 与 WebDAV 一致（按 backing 目录探测，而非按具体文件）。
    /// </summary>
    private string RuleComparisonRoot(Location location)
    {
        if (location.Mount is not null)
            return location.Mount.PhysicalPath;
        if (!string.IsNullOrWhiteSpace(User.RootPath))
            return Path.IsPathRooted(User.RootPath)
                ? User.RootPath
                : Path.GetFullPath(Path.Combine(fallbackDirectory, User.RootPath));
        return fallbackDirectory;
    }
}
