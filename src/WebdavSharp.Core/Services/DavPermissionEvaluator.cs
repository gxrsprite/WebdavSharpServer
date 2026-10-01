using System.Text.RegularExpressions;
using WebdavSharp.Core.Entities;

namespace WebdavSharp.Core.Services;

/// <summary>
/// 权限求值：对齐 hacdias/webdav —— 适用规则（全局 + 用户所属角色 + 用户）
/// 按 <c>SortOrder</c> 从小到大扁平应用，“最后一条命中的规则生效”；
/// 无命中则回退默认权限。SortOrder 跨目标可比，见 docs/permission-model.md。
/// 大小写策略由调用方按 backing 目录探测传入（见 <see cref="DavCaseProbe"/>），
/// 默认忽略大小写（Windows/macOS 行为；Linux ext4 上调用方应传 Ordinal）。
/// </summary>
public static class DavPermissionEvaluator
{
    public static DavPermission Evaluate(
        string virtualPath,
        DavPermission defaultPermissions,
        IEnumerable<DavRule> orderedRules,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        => EvaluateEffective(virtualPath, defaultPermissions, orderedRules, comparison).Permissions;

    /// <summary>求值结果：权限位 + 是否可见。</summary>
    public sealed record EffectiveAccess(DavPermission Permissions, bool IsVisible);

    /// <summary>
    /// 一次遍历同时得出权限与可见性：两者都取“最后一条命中规则”。
    /// 无命中时权限回退默认、可见（IsVisible=true）。
    /// </summary>
    public static EffectiveAccess EvaluateEffective(
        string virtualPath,
        DavPermission defaultPermissions,
        IEnumerable<DavRule> orderedRules,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var path = Normalize(virtualPath);
        var result = defaultPermissions;
        var visible = true;

        foreach (var rule in orderedRules)
        {
            if (!rule.IsEnabled)
                continue;
            if (!Matches(path, rule, comparison))
                continue;
            result = DavPermissionParser.Parse(rule.Permissions);
            visible = rule.IsVisible;
        }

        return new EffectiveAccess(result, visible);
    }

    /// <summary>路径在父目录列表中是否可见（取最后命中规则）。</summary>
    public static bool IsVisible(
        string virtualPath,
        IEnumerable<DavRule> orderedRules,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var path = Normalize(virtualPath);
        var visible = true;
        foreach (var rule in orderedRules)
        {
            if (!rule.IsEnabled || !Matches(path, rule, comparison))
                continue;
            visible = rule.IsVisible;
        }
        return visible;
    }

    public static bool Matches(string virtualPath, DavRule rule,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var path = Normalize(virtualPath);

        if (rule.Kind == DavRuleKind.Regex)
        {
            // hacdias：regex 对路径字面匹配，无前缀/集合特殊处理；
            // 不敏感时同时按折叠后路径匹配（此处等价为 IgnoreCase）。
            var options = RegexOptions.CultureInvariant |
                (comparison == StringComparison.Ordinal ? RegexOptions.None : RegexOptions.IgnoreCase);
            return Regex.IsMatch(path, rule.Pattern, options);
        }

        // path：前缀匹配；尾斜杠同时覆盖集合自身（/secret/ 覆盖 /secret）。
        var pattern = Normalize(rule.Pattern);
        if (pattern.EndsWith('/'))
        {
            var trimmed = pattern.TrimEnd('/');
            if (trimmed.Length == 0)
                return true; // "/" 覆盖一切
            if (path.Equals(trimmed, comparison))
                return true;
            return path.StartsWith(pattern, comparison);
        }

        return path.StartsWith(pattern, comparison);
    }

    /// <summary>
    /// 大小写策略：由调用方按 backing 目录探测传入 comparison
    /// （见 <see cref="DavCaseProbe"/>；NTFS/APFS 不敏感，ext4 等敏感）。
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "/";
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/'))
            path = "/" + path;
        // 解析 "." 段（".." 由 DavPathSecurity 拦截，此处只做规范化）
        var segments = new List<string>();
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
                continue;
            segments.Add(seg);
        }
        return "/" + string.Join('/', segments);
    }
}
