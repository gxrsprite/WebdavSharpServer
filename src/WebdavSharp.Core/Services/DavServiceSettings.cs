using System.Collections.Concurrent;
using System.Net;
using WebdavSharp.Core.Entities;

namespace WebdavSharp.Core.Services;

/// <summary>
/// 协议服务配置：DB 驱动 + 内存缓存。
/// <para>
/// 读路径全部走内存（中间件每次请求都会查 WebDAV 开关，不能每请求打 DB）；
/// 写路径落库并清缓存。SeedData 只在表空时播种——配置文件是初始值，不是每次覆盖。
/// </para>
/// </summary>
public sealed class DavServiceSettings(IFreeSql db)
{
    private readonly ConcurrentDictionary<string, DavServiceSetting> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] Protocols = [DavProtocols.WebDav, DavProtocols.Smb, DavProtocols.Ftp];

    public static bool IsKnownProtocol(string? protocol) =>
        Protocols.Contains(protocol ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    /// <summary>校验 IP 字面量（空 = 跟随主地址，合法）。</summary>
    public static string? ValidateAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;
        return IPAddress.TryParse(address.Trim(), out _) ? null : "监听地址必须是 IP 字面量（如 0.0.0.0 / 127.0.0.1），留空=跟随主地址";
    }

    /// <summary>校验端口范围。</summary>
    public static string? ValidatePort(int port) =>
        port is >= 1 and <= 65535 ? null : "端口必须在 1~65535 之间";

    public async Task<DavServiceSetting> GetAsync(string protocol, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(protocol, out var cached))
            return cached;
        var row = await db.Select<DavServiceSetting>()
            .Where(s => s.Protocol == protocol)
            .FirstAsync(ct);
        if (row is null)
            throw new InvalidOperationException($"dav_service 缺少 {protocol} 行（SeedData 未播种？）");
        _cache[protocol] = row;
        return row;
    }

    public async Task<List<DavServiceSetting>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await db.Select<DavServiceSetting>().ToListAsync(ct);
        foreach (var r in rows)
            _cache[r.Protocol] = r;
        return rows;
    }

    /// <summary>保存一行并清缓存。返回 (成功, 错误信息)。</summary>
    public async Task<(bool Ok, string? Error)> SetAsync(
        string protocol, bool enabled, string? address, int port, CancellationToken ct = default)
    {
        if (!IsKnownProtocol(protocol))
            return (false, $"未知协议：{protocol}");
        var addrErr = ValidateAddress(address);
        if (addrErr is not null)
            return (false, addrErr);
        var portErr = ValidatePort(port);
        if (portErr is not null)
            return (false, portErr);

        var addr = address?.Trim() ?? string.Empty;
        var affected = await db.Update<DavServiceSetting>()
            .Set(s => s.IsEnabled, enabled)
            .Set(s => s.Address, addr)
            .Set(s => s.Port, port)
            .Where(s => s.Protocol == protocol)
            .ExecuteAffrowsAsync(ct);
        if (affected == 0)
        {
            await db.Insert(new DavServiceSetting
            {
                Protocol = protocol.ToLowerInvariant(),
                IsEnabled = enabled,
                Address = addr,
                Port = port,
            }).ExecuteAffrowsAsync(ct);
        }
        _cache.TryRemove(protocol, out _);
        return (true, null);
    }

    /// <summary>
    /// 表空时按给定初始值播种（配置文件只管这一次）。非空则什么都不做——
    /// 管理页改过的值不会被重启覆盖。
    /// </summary>
    public async Task EnsureSeededAsync(
        IEnumerable<(string Protocol, bool Enabled, string Address, int Port)> seeds,
        CancellationToken ct = default)
    {
        if (await db.Select<DavServiceSetting>().AnyAsync(ct))
            return;
        foreach (var (protocol, enabled, address, port) in seeds)
        {
            await db.Insert(new DavServiceSetting
            {
                Protocol = protocol,
                IsEnabled = enabled,
                Address = address,
                Port = port,
            }).ExecuteAffrowsAsync(ct);
        }
    }
}
