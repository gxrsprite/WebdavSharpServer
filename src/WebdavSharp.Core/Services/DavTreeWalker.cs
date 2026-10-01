using WebdavSharp.Core.Entities;

namespace WebdavSharp.Core.Services;

/// <summary>
/// 物理树遍历与集合操作辅助。对齐参考 <c>permissionsFS.allowedThroughout</c>：
/// 整树判定走**未过滤**的物理枚举（不能复用 PROPFIND 的过滤视图，否则“需要拒绝的条目”
/// 恰好被隐藏而误放行）；COPY 则是过滤式部分拷贝（拒绝条目被留下）。
/// 规则路径以 '/' 分隔，形如 <c>/media/dir/sub/file.txt</c>。
/// </summary>
public static class DavTreeWalker
{
    private const int MaxDepth = 256;

    /// <summary>深度优先枚举根下所有后代（物理全路径，规则相对路径）。防 symlink 环，有深度上限。</summary>
    public static IEnumerable<(string PhysicalPath, string RelativePath)> Walk(string rootPhysical)
    {
        var visited = new HashSet<string>(PlatformPath.Comparer);
        var stack = new Stack<(string Physical, string Relative, int Depth)>();
        stack.Push((rootPhysical, string.Empty, 0));

        while (stack.Count > 0)
        {
            var (physical, relative, depth) = stack.Pop();
            if (depth > MaxDepth)
                continue;
            string full;
            try
            {
                full = Path.GetFullPath(physical);
            }
            catch
            {
                continue;
            }
            if (!visited.Add(full))
                continue; // symlink 环

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(physical);
            }
            catch
            {
                continue; // 无权或竞态：剪枝该分支
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                var rel = string.IsNullOrEmpty(relative) ? name : relative + "/" + name;
                yield return (entry, rel);
                if (Directory.Exists(entry))
                    stack.Push((entry, rel, depth + 1));
            }
        }
    }

    /// <summary>
    /// 整树判定：根自身 + 每个后代的规则路径求值后都必须含 <paramref name="required"/>
    /// （组合位如 R|D 要求两者皆有，语义同 <c>Enum.HasFlag</c>）。
    /// </summary>
    public static bool AllSatisfy(
        string rootPhysical, string rootRulePath,
        DavPermission defaults, IEnumerable<DavRule> rules,
        DavPermission required,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        if (!DavPermissionEvaluator.Evaluate(rootRulePath, defaults, rules, comparison).HasFlag(required))
            return false;
        var basePath = rootRulePath.TrimEnd('/');
        foreach (var (_, relative) in Walk(rootPhysical))
        {
            var rulePath = basePath + "/" + relative;
            if (!DavPermissionEvaluator.Evaluate(rulePath, defaults, rules, comparison).HasFlag(required))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 过滤式集合拷贝：创建目标根；无 Read 的目录整棵剪掉（留下），无 Read 的文件跳过；
    /// 其余目录建空壳、文件复制。调用方已对目标根做 Create 判定。
    /// </summary>
    public static void CopyWhereAllowed(
        string srcRootPhysical, string destRootPhysical, string srcRuleRoot,
        DavPermission defaults, IEnumerable<DavRule> rules,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        Directory.CreateDirectory(destRootPhysical);
        var basePath = srcRuleRoot.TrimEnd('/');

        bool Readable(string rulePath) =>
            DavPermissionEvaluator.Evaluate(rulePath, defaults, rules, comparison).HasFlag(DavPermission.Read);

        foreach (var (physical, relative) in Walk(srcRootPhysical))
        {
            var rulePath = basePath + "/" + relative;
            var dest = Path.Combine(destRootPhysical, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(physical))
            {
                if (Readable(rulePath))
                    Directory.CreateDirectory(dest);
                // 无 Read 的目录：整棵留下（Walk 仍会产出其后代，但后代规则路径同样命中
                // 该目录前缀的拒绝规则——只要拒绝规则是 path 前缀即然；为保险起见此处显式剪枝）
            }
            else if (File.Exists(physical))
            {
                // 文件：自身 + 所有祖先目录都必须可读（祖先不可读时整棵已剪，这里只查自身；
                // 祖先剪枝靠：祖先目录无 Read 时跳过建目录，但文件仍会走到这里——必须显式查祖先链）
                if (IsChainReadable(basePath, relative, defaults, rules, comparison))
                    File.Copy(physical, dest, overwrite: true);
            }
        }
    }

    private static bool IsChainReadable(
        string basePath, string relative,
        DavPermission defaults, IEnumerable<DavRule> rules,
        StringComparison comparison)
    {
        // relative 如 a/b/c.txt：检查 /base/a、/base/a/b、/base/a/b/c.txt 全部可读
        var segments = relative.Split('/');
        var path = basePath;
        foreach (var seg in segments)
        {
            path += "/" + seg;
            if (!DavPermissionEvaluator.Evaluate(path, defaults, rules, comparison).HasFlag(DavPermission.Read))
                return false;
        }
        return true;
    }
}
