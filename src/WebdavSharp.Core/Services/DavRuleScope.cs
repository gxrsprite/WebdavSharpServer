namespace WebdavSharp.Core.Services;

/// <summary>
/// 协议名常量。规则可通过 <c>DavRule.Protocols</c> 限定只对某些协议生效，
/// 留空表示适配所有协议。
/// </summary>
public static class DavProtocols
{
    public const string WebDav = "webdav";
    public const string Smb = "smb";
    public const string Ftp = "ftp";

    /// <summary>UI/校验用：全部支持的协议名。</summary>
    public static readonly string[] All = [WebDav, Smb, Ftp];

    public static bool IsKnown(string name) =>
        All.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 规则协议作用域判定。
/// <para>语义：<c>Protocols</c> 留空/空白 = 适配所有协议；
/// 否则按逗号分隔的协议名列表精确匹配（大小写不敏感）。</para>
/// </summary>
public static class DavRuleScope
{
    /// <summary>该规则的协议列表是否适用于 <paramref name="protocol"/>。</summary>
    public static bool AppliesTo(string? protocols, string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocols))
            return true; // 空 = 适配所有

        foreach (var p in protocols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (p.Equals(protocol, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>规范化协议串（去重、小写、固定顺序），供入库前统一格式。</summary>
    public static string Normalize(string? protocols)
    {
        if (string.IsNullOrWhiteSpace(protocols))
            return string.Empty;

        var set = protocols
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .Where(DavProtocols.IsKnown)
            .Distinct()
            .ToHashSet(StringComparer.Ordinal);

        // 固定顺序输出，便于比较与显示
        return string.Join(',', DavProtocols.All.Where(set.Contains));
    }

    /// <summary>用于界面显示的友好文本。</summary>
    public static string Describe(string? protocols) =>
        string.IsNullOrWhiteSpace(protocols) ? "全部协议" : protocols.ToLowerInvariant();
}
