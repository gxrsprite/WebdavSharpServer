namespace WebdavSharp.Server.WebDav;

/// <summary>
/// 暴力破解防护：同一（客户端 IP + 用户名）在窗口内失败满阈值即临时封禁。
/// 对齐参考 Fail2Ban 章节的做法，但内置于服务（无外部依赖）。
/// 单例、线程安全；过期条目惰性清理。
/// </summary>
public sealed class DavBruteForceGuard
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Attempt(int Failures, DateTime WindowStartUtc, DateTime? BannedUntilUtc);

    public static string KeyFor(string clientIp, string username) =>
        $"{clientIp}|{username.Trim().ToLowerInvariant()}";

    /// <summary>是否正被封禁（含过期清理）。</summary>
    public bool IsBanned(string key, DateTime now, int banMinutes)
    {
        lock (_gate)
        {
            if (!_attempts.TryGetValue(key, out var a) || a.BannedUntilUtc is null)
                return false;
            if (a.BannedUntilUtc <= now)
            {
                _attempts.Remove(key);
                return false;
            }
            return true;
        }
    }

    public bool IsBanned(string key, int banMinutes) => IsBanned(key, DateTime.UtcNow, banMinutes);

    /// <summary>记录一次失败；达到阈值返回 true（本次即封禁）。</summary>
    public bool RecordFailure(string key, int maxFailures, int windowMinutes, int banMinutes)
        => RecordFailure(key, DateTime.UtcNow, maxFailures, windowMinutes, banMinutes);

    public bool RecordFailure(string key, DateTime now, int maxFailures, int windowMinutes, int banMinutes)
    {
        lock (_gate)
        {
            if (!_attempts.TryGetValue(key, out var a) || (now - a.WindowStartUtc).TotalMinutes >= windowMinutes)
                a = new Attempt(0, now, null);
            a = a with { Failures = a.Failures + 1 };
            if (a.Failures >= maxFailures)
                a = a with { BannedUntilUtc = now.AddMinutes(banMinutes) };
            _attempts[key] = a;
            return a.BannedUntilUtc is not null && a.BannedUntilUtc > now;
        }
    }

    /// <summary>成功登录清零（用户名被合法主人用对即解套）。</summary>
    public void RecordSuccess(string key)
    {
        lock (_gate)
        {
            _attempts.Remove(key);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _attempts.Count;
            }
        }
    }
}
