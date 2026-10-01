using WebdavSharp.Core.Entities;

namespace WebdavSharp.Core.Services;

/// <summary>
/// 用户文件根解析。对齐 hacdias per-user directory 语义：
/// 用户设置主目录（<see cref="DavUser.RootPath"/>）时，整个虚拟路径视为其相对路径
/// （不做挂载名拆分）；否则首段命中启用的挂载，否则单根回退。
/// 纯函数，可单测；调用方仍需经 <see cref="DavPathSecurity"/> 做物理穿越拦截。
/// </summary>
public static class DavRootResolver
{
    public sealed record ResolvedRoot(string PhysicalRoot, string MountRel, DavMount? Mount);

    public static ResolvedRoot Resolve(
        string userRootPath,
        string canonicalVirtualPath,
        IEnumerable<DavMount> enabledMounts,
        string fallbackDirectory)
    {
        if (!string.IsNullOrWhiteSpace(userRootPath))
        {
            return new ResolvedRoot(userRootPath, canonicalVirtualPath, null);
        }

        var (mountName, rel) = DavPathSecurity.SplitMount(canonicalVirtualPath);
        var mount = enabledMounts.FirstOrDefault(m =>
            m.Name.Equals(mountName, StringComparison.OrdinalIgnoreCase));
        if (mount is not null)
            return new ResolvedRoot(mount.PhysicalPath, rel, mount);
        return new ResolvedRoot(fallbackDirectory, canonicalVirtualPath, null);
    }
}
