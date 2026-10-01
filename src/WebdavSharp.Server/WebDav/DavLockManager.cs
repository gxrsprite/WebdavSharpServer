using System.Text.RegularExpressions;
using WebdavSharp.Core.Services;

namespace WebdavSharp.Server.WebDav;

/// <summary>
/// 内存锁管理器。对齐参考（x/net memLS 经 lockSystem 委托）：排他/共享锁、超时过期、
/// 后代覆盖、凭令牌（Lock-Token / If 头）放行。命名空间取物理全路径，
/// 因此锁跨用户生效（同参考 resolve 到 backing path 的做法）。
/// 单例、线程安全；过期条目在每次访问时惰性清理。
/// </summary>
public sealed class DavLockManager
{
    private readonly object _gate = new();
    // 锁命名空间是物理路径 → 必须跟平台大小写语义（Linux 上 /d 与 /D 是不同目录）
    private readonly Dictionary<string, List<LockEntry>> _locks = new(PlatformPath.Comparer);

    public sealed record LockEntry(
        string Token,
        string Path,
        bool Exclusive,
        bool DepthInfinity,
        string Owner,
        DateTime ExpiresAtUtc);

    /// <summary>创建锁。冲突（不兼容的重叠锁）时返回 null。</summary>
    public LockEntry? Create(string physicalPath, bool exclusive, bool depthInfinity, string owner, TimeSpan? timeout)
    {
        var path = Normalize(physicalPath);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (FindConflictLocked(path, excludeToken: null, sharedOk: !exclusive) is not null)
                return null;
            var entry = new LockEntry(
                $"opaquelocktoken:{Guid.CreateVersion7()}",
                path, exclusive, depthInfinity, owner,
                timeout is null ? DateTime.MaxValue : DateTime.UtcNow.Add(timeout.Value));
            if (!_locks.TryGetValue(path, out var list))
                _locks[path] = list = [];
            list.Add(entry);
            return entry;
        }
    }

    /// <summary>刷新：凭令牌延长覆盖该路径的锁。成功返回更新后条目。</summary>
    public LockEntry? Refresh(string physicalPath, string token, TimeSpan? timeout)
    {
        var path = Normalize(physicalPath);
        lock (_gate)
        {
            PurgeExpiredLocked();
            var entry = FindByTokenLocked(token);
            if (entry is null || !Covers(entry, path))
                return null;
            var updated = entry with
            {
                ExpiresAtUtc = timeout is null ? DateTime.MaxValue : DateTime.UtcNow.Add(timeout.Value)
            };
            ReplaceLocked(entry, updated);
            return updated;
        }
    }

    /// <summary>解锁：令牌须匹配覆盖该路径的锁。</summary>
    public bool Unlock(string physicalPath, string token)
    {
        var path = Normalize(physicalPath);
        lock (_gate)
        {
            PurgeExpiredLocked();
            var entry = FindByTokenLocked(token);
            if (entry is null || !Covers(entry, path))
                return false;
            _locks[entry.Path].Remove(entry);
            if (_locks[entry.Path].Count == 0)
                _locks.Remove(entry.Path);
            return true;
        }
    }

    /// <summary>
    /// 写检查：无重叠活动锁 → true；有 → 出示的令牌须命中覆盖目标的锁。
    /// 集合操作（整树生效）由调用方传 <paramref name="deep"/> = true，
    /// 此时路径下任何后代锁都会纳入判定（即使后代是 depth 0 锁）。
    /// </summary>
    public bool CheckWrite(string physicalPath, IEnumerable<string> presentedTokens, bool deep = false)
    {
        var path = Normalize(physicalPath);
        var presented = new HashSet<string>(presentedTokens, StringComparer.Ordinal);
        lock (_gate)
        {
            PurgeExpiredLocked();
            foreach (var (lockPath, list) in _locks)
            {
                foreach (var entry in list)
                {
                    if (!Overlaps(entry, path, deep))
                        continue;
                    if (presented.Contains(entry.Token))
                        continue;
                    return false;
                }
            }
            return true;
        }
    }

    // ---------- 内部 ----------

    private static string Normalize(string physicalPath)
    {
        try
        {
            return Path.GetFullPath(physicalPath).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return physicalPath;
        }
    }

    /// <summary>锁 entry 是否覆盖目标路径（depth 0 仅自身；infinity 含后代）。</summary>
    private static bool Covers(LockEntry entry, string targetPath) =>
        targetPath.Equals(entry.Path, PlatformPath.Comparison) ||
        (entry.DepthInfinity && IsDescendant(targetPath, entry.Path));

    /// <summary>
    /// 写操作是否受该锁影响：锁覆盖目标，或（deep 集合操作时）目标覆盖锁
    /// （锁住的子孙会阻止删/移父集合——同 x/net Confirm 整树语义）。
    /// </summary>
    private static bool Overlaps(LockEntry entry, string targetPath, bool deep) =>
        Covers(entry, targetPath) ||
        (deep && IsDescendant(entry.Path, targetPath));

    private static bool IsDescendant(string path, string ancestor) =>
        path.StartsWith(ancestor + Path.DirectorySeparatorChar, PlatformPath.Comparison);

    /// <summary>查找与新锁不兼容的重叠锁。共享+共享（且新锁 depth 0 或双方共享）可共存。</summary>
    private LockEntry? FindConflictLocked(string path, string? excludeToken, bool sharedOk)
    {
        foreach (var (lockPath, list) in _locks)
        {
            foreach (var entry in list)
            {
                if (excludeToken is not null && entry.Token == excludeToken)
                    continue;
                // 新锁覆盖已锁后代 / 已有锁覆盖新锁路径 / 同路径：都算重叠
                var overlap = Covers(entry, path) ||
                    IsDescendant(entry.Path, path) ||
                    entry.Path.Equals(path, PlatformPath.Comparison);
                if (!overlap)
                    continue;
                if (sharedOk && !entry.Exclusive)
                    continue; // 共享+共享共存
                return entry;
            }
        }
        return null;
    }

    private LockEntry? FindByTokenLocked(string token)
    {
        foreach (var list in _locks.Values)
        {
            var hit = list.FirstOrDefault(e => e.Token == token);
            if (hit is not null)
                return hit;
        }
        return null;
    }

    private void ReplaceLocked(LockEntry oldEntry, LockEntry newEntry)
    {
        var list = _locks[oldEntry.Path];
        list[list.IndexOf(oldEntry)] = newEntry;
    }

    private void PurgeExpiredLocked()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _locks.Keys.ToList())
        {
            _locks[key].RemoveAll(e => e.ExpiresAtUtc <= now);
            if (_locks[key].Count == 0)
                _locks.Remove(key);
        }
    }

    // ---------- 头解析 ----------

    private static readonly Regex TokenRegex = new(@"<([^<>\s]+)>", RegexOptions.Compiled);

    /// <summary>解析 Timeout 头：Second-N / Infinite（逗号分隔取第一个 Second）。缺省 1 小时。</summary>
    public static TimeSpan? ParseTimeout(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return TimeSpan.FromHours(1);
        foreach (var part in header.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("Second-", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(p["Second-".Length..], out var seconds))
                return TimeSpan.FromSeconds(Math.Max(1, seconds));
            if (p.Equals("Infinite", StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return TimeSpan.FromHours(1);
    }

    /// <summary>从 Lock-Token / If 头提取出示的令牌集合。</summary>
    public static HashSet<string> PresentedTokens(HttpRequest request)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var lockToken = request.Headers["Lock-Token"].ToString();
        foreach (Match m in TokenRegex.Matches(lockToken))
            set.Add(m.Groups[1].Value);
        var @if = request.Headers["If"].ToString();
        foreach (Match m in TokenRegex.Matches(@if))
            set.Add(m.Groups[1].Value);
        return set;
    }

    /// <summary>从 LOCK 请求体解析 scope（默认 exclusive）与 owner（尽力）。</summary>
    public static (bool Exclusive, string Owner) ParseLockBody(string body)
    {
        var exclusive = true;
        if (body.Contains("<D:shared", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("<shared", StringComparison.OrdinalIgnoreCase))
            exclusive = false;
        var owner = "";
        var om = Regex.Match(body, @"<D:owner[^>]*>(.*?)</D:owner[^>]*>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!om.Success)
            om = Regex.Match(body, @"<owner[^>]*>(.*?)</owner[^>]*>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (om.Success)
            owner = Regex.Replace(om.Groups[1].Value, @"<[^>]+>", "").Trim();
        return (exclusive, owner);
    }
}
