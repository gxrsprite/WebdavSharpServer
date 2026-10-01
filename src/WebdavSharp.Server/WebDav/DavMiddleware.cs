using System.Net;
using System.Security;
using System.Text;
using Microsoft.Extensions.Options;
using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;

namespace WebdavSharp.Server.WebDav;

/// <summary>
/// WebDAV 中间件（v1 骨架 + 真实权限卡点）。
/// 动词分发：OPTIONS/PROPFIND（含 Depth: infinity 递归）/GET/HEAD/PUT/MKCOL/DELETE
/// （整树判定）/COPY/MOVE（含集合：COPY 过滤式部分拷贝，MOVE 整树鉴权）真实现；
/// LOCK（fake token）/UNLOCK/PROPPATCH 最小兼容；其余 501。
/// 方法→权限映射对齐 hacdias Allowed()/AllowedDestination()：MOVE 源要求 R+D，
/// COPY/MOVE 目标（已存在→U，不存在→C），UNLOCK 要求 C 或 U。
/// 虚拟根不可写、挂载点不可删（对齐参考 multidir）。
/// 认证：HTTP Basic（无用户表记录时 401 + WWW-Authenticate）。
/// 授权：每条触及路径都过 <see cref="DavPermissionEvaluator"/>（用户默认→角色规则→用户规则，
/// 最后命中生效；集合操作要求后代全部允许）。
/// </summary>
public sealed class DavMiddleware(
    RequestDelegate next,
    IOptions<DavServerOptions> options,
    DavServiceSettings services)
{
    private readonly DavServerOptions _dav = options.Value;

    private static readonly string[] AllowVerbs =
        ["OPTIONS", "PROPFIND", "PROPPATCH", "GET", "HEAD", "PUT", "DELETE",
         "MKCOL", "COPY", "MOVE", "LOCK", "UNLOCK"];

    public async Task InvokeAsync(
        HttpContext http, IFreeSql fsql, DavAuthService auth,
        DavLockManager locks, DavBruteForceGuard guard, ILogger<DavMiddleware> logger)
    {
        var prefix = NormalizePrefix(_dav.Prefix);
        if (!http.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(http);
            return;
        }

        // WebDAV 总开关（管理页即时生效；走内存缓存，不额外打 DB）。
        // 注意：只管 /dav/ 前缀，不管后台管理页与健康探针——否则关掉 WebDAV 会把自己锁在外面。
        var svc = await services.GetAsync(DavProtocols.WebDav);
        if (!svc.IsEnabled)
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await http.Response.WriteAsync("WebDAV 已在管理页禁用（/admin/services 可重新开启）");
            return;
        }

        // 审计上下文：try 包住 401/规范化失败等早期返回，finally 统一落库 + 访问日志。
        var auditMethod = http.Request.Method.ToUpperInvariant();
        var auditUser = "?";
        var auditPath = "/";
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        // 状态码用 OnStarting 捕获，避免在 finally 里读 Response.StatusCode 造成的
        // 响应锁定/提前提交（会让 SendFileAsync 的 Range 处理失效）。
        var finalStatus = 0;
        http.Response.OnStarting(() =>
        {
            finalStatus = http.Response.StatusCode;
            return Task.CompletedTask;
        });
        // 真实客户端 IP：可信代理后取 X-Forwarded-For 首个（对齐 BehindProxy 语义）。
        var clientIp = _dav.BehindProxy
            ? http.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim()
            : http.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (string.IsNullOrEmpty(clientIp))
            clientIp = "?";
        try
        {

        // 虚拟路径 = 去掉前缀部分（保留挂载名），如 /dav/media/a → media/a。
        // Request.Path 已由服务器解码一次，不再二次解码（防 %252e 双重解码绕过）。
        // 鉴权前先规范化（消解 ./..，保留集合尾斜杠），越过虚拟根直接 403。
        var virtualPath = http.Request.Path.Value![prefix.Length..].TrimStart('/');
        var canonical = DavPathSecurity.CanonicalizeVirtualPath(virtualPath);
        if (canonical is null)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsync("Forbidden: path escapes virtual root");
            return;
        }
        var decodedVirtual = canonical;
        auditPath = "/" + canonical.TrimStart('/');

        var cred = DavAuthService.ParseBasicHeader(http.Request.Headers["Authorization"].ToString());
        DavUser? user = null;
        var banKey = DavBruteForceGuard.KeyFor(clientIp, cred?.Username ?? "?");
        if (guard.IsBanned(banKey, _dav.LoginBanMinutes))
        {
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            http.Response.Headers["Retry-After"] = (_dav.LoginBanMinutes * 60).ToString();
            auditUser = cred?.Username ?? "?";
            await http.Response.WriteAsync("Too Many Requests: login temporarily banned");
            return;
        }
        if (cred is not null)
        {
            auditUser = cred.Value.Username;
            user = await auth.AuthenticateBasicAsync(cred.Value.Username, cred.Value.Password, _dav.NoPassword);
        }
        if (user is null)
        {
            guard.RecordFailure(banKey, _dav.MaxFailedLogins, _dav.FailedLoginWindowMinutes, _dav.LoginBanMinutes);
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            http.Response.Headers["WWW-Authenticate"] = "Basic realm=\"WebdavSharp\"";
            await http.Response.WriteAsync("Unauthorized");
            return;
        }
        auditUser = user.Username;
        guard.RecordSuccess(banKey);

        // 文件根解析：用户主目录（RootPath）优先，否则挂载名拆分，否则单根回退。
        // 用于权限求值的虚拟路径恒为 "/" + 规范虚拟路径（含挂载名或主目录相对路径）。
        var mounts = await fsql.Select<DavMount>().Where(m => m.IsEnabled).OrderBy(m => m.SortOrder).ToListAsync();
        var resolved = DavRootResolver.Resolve(user.RootPath, decodedVirtual, mounts, _dav.Directory);
        var mount = resolved.Mount;
        var physicalRoot = resolved.PhysicalRoot;
        var mountRel = resolved.MountRel;
        var rulePath = "/" + decodedVirtual.TrimStart('/');
        // 用户主目录允许写相对路径：锚到全局根（_dav.Directory 经 PostConfigure 已锚为绝对）。
        if (!Path.IsPathRooted(physicalRoot))
            physicalRoot = Path.GetFullPath(Path.Combine(_dav.Directory, physicalRoot));
        var isVirtualRoot = mount is null && string.IsNullOrEmpty(mountRel);
        var isMountRoot = mount is not null && string.IsNullOrEmpty(mountRel);
        Directory.CreateDirectory(physicalRoot);

        string physical;
        try
        {
            // 物理路径归属判定用平台大小写语义（Linux 敏感），与规则匹配的探测语义分开
            physical = DavPathSecurity.ResolveSafePath(physicalRoot, mountRel, PlatformPath.Comparison);
        }
        catch (DavPathTraversalException)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsync("Forbidden: path escapes mount root");
            return;
        }

        var rules = await LoadRulesAsync(fsql, user, DavProtocols.WebDav);
        var defaults = DavPermissionParser.Parse(_dav.DefaultPermissions);
        // 大小写策略按本次命中的 backing 物理根探测（对齐 hacdias caseInsensitiveFS）。
        var comparison = DavCaseProbe.ComparisonFor(physicalRoot);
        bool Allowed(string vp, DavPermission need) =>
            DavPermissionEvaluator.Evaluate(vp, defaults, rules, comparison).HasFlag(need);
        var presentedTokens = DavLockManager.PresentedTokens(http.Request);
        bool WriteUnlocked(string targetPhysical, bool deep) =>
            locks.CheckWrite(targetPhysical, presentedTokens, deep);

        var method = http.Request.Method.ToUpperInvariant();
        http.Response.Headers["Dav"] = "1, 2";
        http.Response.Headers["Allow"] = string.Join(", ", AllowVerbs);

        switch (method)
        {
            case "OPTIONS":
                http.Response.StatusCode = StatusCodes.Status200OK;
                return;

            case "PROPFIND":
                if (!Allowed(rulePath, DavPermission.Read))
                {
                    await Forbidden(http);
                    return;
                }
                // 有挂载且无用户主目录时，虚拟根列出挂载入口（对齐参考 rootEntries）；
                // 无挂载时虚拟根即单根目录本身（直挂模式），列物理子项。
                var listMountsAtRoot = isVirtualRoot && mounts.Count > 0 &&
                    string.IsNullOrWhiteSpace(user.RootPath);
                await PropfindAsync(http, physical, rulePath, defaults, rules, comparison, mounts, listMountsAtRoot);
                return;

            case "PROPPATCH":
                if (!Allowed(rulePath, DavPermission.Update))
                {
                    await Forbidden(http);
                    return;
                }
                if (!WriteUnlocked(physical, deep: false))
                {
                    await Locked(http);
                    return;
                }
                http.Response.StatusCode = 207;
                http.Response.ContentType = "application/xml";
                await http.Response.WriteAsync(Multistatus($"<D:response><D:href>{Href(rulePath)}</D:href></D:response>"));
                return;

            case "GET":
            case "HEAD":
                if (!Allowed(rulePath, DavPermission.Read))
                {
                    await Forbidden(http);
                    return;
                }
                if (Directory.Exists(physical))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (!File.Exists(physical))
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                var fileInfo = new FileInfo(physical);
                http.Response.ContentType = ContentType(physical);
                // 显式实现 Range（不依赖 SendFileAsync）：本中间件运行在路由之前，
                // 该位置没有启用 sendfile 的响应特性，SendFileAsync 会退化为
                // chunked 流拷贝并忽略 Range（实测 206/Content-Range 全丢）。
                await ServeFileAsync(http, physical, fileInfo, method == "HEAD");
                return;

            case "PUT":
                if (isVirtualRoot || isMountRoot)
                {
                    await Forbidden(http, "Forbidden: cannot PUT a collection root");
                    return;
                }
                var need = File.Exists(physical) ? DavPermission.Update : DavPermission.Create;
                if (!Allowed(rulePath, need))
                {
                    await Forbidden(http);
                    return;
                }
                if (!WriteUnlocked(physical, deep: false))
                {
                    await Locked(http);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
                var existed = File.Exists(physical);
                await using (var fs = File.Create(physical))
                    await http.Request.Body.CopyToAsync(fs);
                http.Response.StatusCode = existed ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
                return;

            case "MKCOL":
                if (!Allowed(rulePath, DavPermission.Create))
                {
                    await Forbidden(http);
                    return;
                }
                if (!WriteUnlocked(physical, deep: false))
                {
                    await Locked(http);
                    return;
                }
                if (isVirtualRoot || isMountRoot || Directory.Exists(physical) || File.Exists(physical))
                {
                    http.Response.StatusCode = 405; // Method Not Allowed（已存在；虚拟根/挂载根视为已存在）
                    return;
                }
                Directory.CreateDirectory(physical);
                http.Response.StatusCode = StatusCodes.Status201Created;
                return;

            case "DELETE":
                if (isVirtualRoot || isMountRoot)
                {
                    await Forbidden(http, "Forbidden: cannot DELETE a mount point");
                    return;
                }
                if (!Allowed(rulePath, DavPermission.Delete))
                {
                    await Forbidden(http);
                    return;
                }
                if (!WriteUnlocked(physical, deep: true))
                {
                    await Locked(http);
                    return;
                }
                // 整树判定走未过滤的物理枚举（对齐参考 allowedThroughout）。
                if (Directory.Exists(physical) &&
                    !DavTreeWalker.AllSatisfy(physical, rulePath, defaults, rules, DavPermission.Delete, comparison))
                {
                    await Forbidden(http, "Forbidden: denied descendant inside collection");
                    return;
                }
                if (Directory.Exists(physical)) Directory.Delete(physical, true);
                else if (File.Exists(physical)) File.Delete(physical);
                else
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                http.Response.StatusCode = StatusCodes.Status204NoContent;
                return;

            case "COPY":
            case "MOVE":
                var dest = await MapDestinationAsync(http, prefix);
                if (dest is null)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                var (destPhysical, destRulePath, destIsRoot) = dest.Value;
                if (destIsRoot)
                {
                    await Forbidden(http, "Forbidden: destination is a mount point");
                    return;
                }
                var overwriteDenied = http.Request.Headers["Overwrite"].ToString()
                    .Equals("F", StringComparison.OrdinalIgnoreCase);
                // 对齐参考 Allowed()/AllowedDestination()：MOVE 源要求 R+D，COPY 源要求 R；
                // 目标（不存在→C，已存在→U，由 Overwrite 头参与）。
                var srcNeed = method == "MOVE"
                    ? (DavPermission.Read | DavPermission.Delete)
                    : DavPermission.Read;

                if (Directory.Exists(physical))
                {
                    await CopyOrMoveCollectionAsync(http, method, physical, rulePath,
                        destPhysical, destRulePath, srcNeed, overwriteDenied,
                        defaults, rules, comparison, Allowed, locks, presentedTokens);
                    return;
                }

                if (!File.Exists(physical))
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                if (Directory.Exists(destPhysical))
                {
                    http.Response.StatusCode = StatusCodes.Status409Conflict;
                    await http.Response.WriteAsync("Conflict: destination is a collection");
                    return;
                }
                var destNeed = File.Exists(destPhysical)
                    ? DavPermission.Update
                    : DavPermission.Create;
                if (overwriteDenied && File.Exists(destPhysical))
                {
                    http.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                    await http.Response.WriteAsync("Precondition Failed: destination exists, Overwrite is F");
                    return;
                }
                if (!Allowed(rulePath, srcNeed) || !Allowed(destRulePath, destNeed))
                {
                    await Forbidden(http);
                    return;
                }
                if (!WriteUnlocked(physical, deep: false) || !WriteUnlocked(destPhysical, deep: false))
                {
                    await Locked(http);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destPhysical)!);
                if (method == "COPY") File.Copy(physical, destPhysical, overwrite: true);
                else File.Move(physical, destPhysical, overwrite: true);
                http.Response.StatusCode = StatusCodes.Status201Created;
                return;

            case "LOCK":
                if (!Allowed(rulePath, DavPermission.Update) && !Allowed(rulePath, DavPermission.Create))
                {
                    await Forbidden(http);
                    return;
                }
                var depth = http.Request.Headers["Depth"].ToString();
                var depthInfinity = !depth.Equals("0", StringComparison.OrdinalIgnoreCase);
                var timeout = DavLockManager.ParseTimeout(http.Request.Headers["Timeout"].ToString());

                string body;
                using (var reader = new StreamReader(http.Request.Body))
                    body = await reader.ReadToEndAsync();
                var (exclusive, owner) = DavLockManager.ParseLockBody(body);

                // 刷新：出示了覆盖该路径的旧令牌 → 续期；否则创建新锁。
                DavLockManager.LockEntry? entry = null;
                foreach (var t in presentedTokens)
                {
                    entry = locks.Refresh(physical, t, timeout);
                    if (entry is not null)
                        break;
                }
                entry ??= locks.Create(physical, exclusive, depthInfinity, owner, timeout);
                if (entry is null)
                {
                    // 他人持有不兼容锁 → 423 Locked。
                    await Locked(http);
                    return;
                }
                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.ContentType = "application/xml";
                http.Response.Headers["Lock-Token"] = $"<{entry.Token}>";
                var scopeXml = entry.Exclusive ? "<D:exclusive/>" : "<D:shared/>";
                var timeoutXml = entry.ExpiresAtUtc == DateTime.MaxValue
                    ? "Infinite"
                    : $"Second-{(long)(entry.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds}";
                await http.Response.WriteAsync(
                    $"<?xml version=\"1.0\" encoding=\"utf-8\"?><D:prop xmlns:D=\"DAV:\"><D:lockdiscovery>" +
                    $"<D:activelock><D:locktype><D:write/></D:locktype><D:lockscope>{scopeXml}</D:lockscope>" +
                    $"<D:depth>{(entry.DepthInfinity ? "infinity" : "0")}</D:depth>" +
                    $"<D:owner>{System.Security.SecurityElement.Escape(entry.Owner)}</D:owner>" +
                    $"<D:timeout>{timeoutXml}</D:timeout>" +
                    $"<D:locktoken><D:href>{entry.Token}</D:href></D:locktoken>" +
                    $"<D:lockroot><D:href>{Href(rulePath)}</D:href></D:lockroot></D:activelock>" +
                    $"</D:lockdiscovery></D:prop>");
                return;

            case "UNLOCK":
                // 对齐参考：UNLOCK 要求 Create 或 Update，且令牌须匹配。
                if (!Allowed(rulePath, DavPermission.Create) && !Allowed(rulePath, DavPermission.Update))
                {
                    await Forbidden(http);
                    return;
                }
                var unlockTokens = DavLockManager.PresentedTokens(http.Request);
                var unlocked = unlockTokens.Any(t => locks.Unlock(physical, t));
                if (!unlocked)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await http.Response.WriteAsync("Bad Request: Lock-Token must match an active lock");
                    return;
                }
                http.Response.StatusCode = StatusCodes.Status204NoContent;
                return;

            default:
                await NotImplemented(http, $"{method} not implemented");
                return;
        }
        }
        finally
        {
            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var status = finalStatus != 0 ? finalStatus : http.Response.StatusCode;
            logger.LogInformation("DAV {Method} {Path} {User} {Ip} → {Status} ({Elapsed:0}ms)",
                auditMethod, auditPath, auditUser, clientIp, status, elapsedMs);
            await WriteAuditAsync(http, fsql, auditMethod, auditPath, auditUser, status);
        }
    }

    private static readonly string[] AuditedMethods =
        ["PUT", "MKCOL", "DELETE", "COPY", "MOVE", "LOCK", "UNLOCK", "PROPPATCH"];

    /// <summary>
    /// 审计落库：变更动词全记；读动词只记失败（401/403/409/412/423/5xx）。
    /// 失败不影响请求（best-effort）。
    /// </summary>
    private static async Task WriteAuditAsync(
        HttpContext http, IFreeSql fsql, string method, string rulePath, string username, int status)
    {
        var isMutation = AuditedMethods.Contains(method);
        var isFailure = status is 401 or 403 or 409 or 412 or 423 or 429 || status >= 500;
        if (!isMutation && !isFailure)
            return;
        try
        {
            var dest = http.Request.Headers["Destination"].ToString();
            if (dest.Length > 1000)
                dest = dest[..1000];
            var path = rulePath.Length > 1000 ? rulePath[..1000] : rulePath;
            await fsql.Insert(new DavAuditLog
            {
                Username = username,
                Method = method,
                VirtualPath = path,
                Destination = dest,
                StatusCode = status,
            }).ExecuteAffrowsAsync();
        }
        catch
        {
            // 审计落库失败静默，不中断正常请求
        }
    }

    // ---------- 内部辅助 ----------

    /// <summary>
    /// 单区间 Range 支持（RFC 9110 §14）。多区间与 If-Range 暂不实现（返回 200 全量，
    /// 客户端可接受）。无 Range/不可满足区间 200，单区间命中 206 + Content-Range。
    /// </summary>
    private static async Task ServeFileAsync(HttpContext http, string physical, FileInfo info, bool headOnly)
    {
        http.Response.Headers["Accept-Ranges"] = "bytes";
        http.Response.Headers["Last-Modified"] = info.LastWriteTimeUtc.ToString("R");
        var total = info.Length;

        var rangeHeader = http.Request.Headers["Range"].ToString();
        // If-Range：值不匹配当前 ETag/日期时，RFC 9110 §13.1.5 要求忽略 Range 返回全量。
        if (!IfRangeAllowsPartial(http.Request, info))
            rangeHeader = string.Empty;

        var ranges = ParseRanges(rangeHeader, total);
        if (ranges is null || ranges.Count == 0)
        {
            // 无 Range，或全部区间不可满足：整文件 200。
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentLength = total;
            if (headOnly)
                return;
            await http.Response.SendFileAsync(physical);
            return;
        }

        if (ranges.Count == 1)
        {
            var (from, to) = ranges[0];
            var length = to - from + 1;
            http.Response.StatusCode = StatusCodes.Status206PartialContent;
            http.Response.ContentLength = length;
            http.Response.Headers["Content-Range"] = $"bytes {from}-{to}/{total}";
            if (headOnly)
                return;
            await using var fs = new FileStream(physical, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(from, SeekOrigin.Begin);
            await CopyRangeAsync(fs, http.Response.Body, length, http.RequestAborted);
            return;
        }

        // 多区间：multipart/byteranges（RFC 9110 §14.6）。边界每次随机，
        // Content-Length 需预先算准（各段头部长度 + 数据 + 结尾边界）。
        // 注意：段头里的 Content-Type 必须是**文件类型**，且要在改写
        // Response.ContentType 之前取到——否则会把 multipart 自身类型写进段头，
        // 导致预算与实际写出不符（Kestrel 报 Content-Length mismatch）。
        var fileContentType = http.Response.ContentType ?? "application/octet-stream";
        var boundary = "DAV" + Guid.CreateVersion7().ToString("N");
        var bodyLength = ComputeMultipartLength(ranges, total, boundary, fileContentType);
        http.Response.StatusCode = StatusCodes.Status206PartialContent;
        http.Response.ContentType = $"multipart/byteranges; boundary={boundary}";
        http.Response.ContentLength = bodyLength;
        if (headOnly)
            return;

        await using var multi = new FileStream(physical, FileMode.Open, FileAccess.Read, FileShare.Read);
        var body = http.Response.Body;
        foreach (var (from, to) in ranges)
        {
            await body.WriteAsync(
                Encoding.ASCII.GetBytes(BuildPartHeader(boundary, from, to, total, fileContentType)),
                http.RequestAborted);
            multi.Seek(from, SeekOrigin.Begin);
            await CopyRangeAsync(multi, body, to - from + 1, http.RequestAborted);
            await body.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), http.RequestAborted);
        }
        await body.WriteAsync(Encoding.ASCII.GetBytes($"--{boundary}--\r\n"), http.RequestAborted);
    }

    private static string BuildPartHeader(string boundary, long from, long to, long total, string contentType) =>
        $"\r\n--{boundary}\r\nContent-Type: {contentType}\r\nContent-Range: bytes {from}-{to}/{total}\r\n\r\n";

    /// <summary>预算 multipart/byteranges 实体长度（与写出顺序严格一致）。</summary>
    internal static long ComputeMultipartLengthForTest(
        List<(long From, long To)> ranges, long total, string boundary, string? contentType) =>
        ComputeMultipartLength(ranges, total, boundary, contentType);

    private static long ComputeMultipartLength(
        List<(long From, long To)> ranges, long total, string boundary, string? contentType)
    {
        var ct = contentType ?? "application/octet-stream";
        long length = 0;
        foreach (var (from, to) in ranges)
        {
            length += Encoding.ASCII.GetByteCount(BuildPartHeader(boundary, from, to, total, ct));
            length += to - from + 1;
            length += 2; // 段尾 CRLF
        }
        length += Encoding.ASCII.GetByteCount($"--{boundary}--\r\n");
        return length;
    }

    /// <summary>
    /// If-Range 判定：无该头 → 允许 Range；有 → 值与 ETag 或 Last-Modified 匹配才允许，
    /// 否则按 RFC 忽略 Range（返回全量 200）。
    /// </summary>
    private static bool IfRangeAllowsPartial(HttpRequest request, FileInfo info)
    {
        var ifRange = request.Headers["If-Range"].ToString();
        if (string.IsNullOrWhiteSpace(ifRange))
            return true;
        ifRange = ifRange.Trim();

        // 强 ETag 比较：与自身生成的弱/强 ETag 一致即可（本服务用 LastWrite+Length 派生）
        var etag = BuildETag(info);
        if (ifRange.StartsWith('"') || ifRange.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            return string.Equals(ifRange, etag, StringComparison.Ordinal);

        // 日期比较：HTTP-date，允许相等（秒级精度）
        return DateTime.TryParse(ifRange, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var when)
            && Math.Abs((when - info.LastWriteTimeUtc).TotalSeconds) < 1.0;
    }

    /// <summary>ETag = 长度 + 最后写入时间的十六进制（弱 ETag，够用于 If-Range）。</summary>
    internal static string BuildETag(FileInfo info) =>
        $"W/\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";

    /// <summary>
    /// 解析 Range 头为多个区间（RFC 9110 §14）。返回 null 表示无 Range/格式非法（→ 200 全量）；
    /// 不可满足的区间被丢弃，全部不可满足时返回空列表（同样 200 全量）。
    /// </summary>
    internal static List<(long From, long To)>? ParseRanges(string header, long total)
    {
        if (total <= 0)
            return null;
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return null;

        var result = new List<(long From, long To)>();
        foreach (var raw in header["bytes=".Length..].Split(','))
        {
            var spec = raw.Trim();
            if (spec.Length == 0)
                continue;
            var parsed = ParseOneRange(spec, total);
            if (parsed is not null)
                result.Add(parsed.Value);
        }
        // 去重合并相邻/重叠区间，避免重复传输数据（也减少 multipart 段数）。
        return MergeRanges(result);
    }

    private static (long From, long To)? ParseOneRange(string spec, long total)
    {
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return null;
        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();

        long from, to;
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out var suffix) || suffix <= 0)
                return null;
            from = Math.Max(0, total - suffix);
            to = total - 1;
        }
        else
        {
            if (!long.TryParse(startText, out from) || from < 0 || from >= total)
                return null;
            if (endText.Length == 0)
                to = total - 1;
            else if (!long.TryParse(endText, out var e))
                return null;
            else
                to = Math.Min(e, total - 1);
            if (to < from)
                return null;
        }
        return (from, to);
    }

    /// <summary>按起点排序并合并重叠/相邻区间。</summary>
    internal static List<(long From, long To)> MergeRanges(List<(long From, long To)> ranges)
    {
        if (ranges.Count <= 1)
            return ranges;
        var sorted = ranges.OrderBy(r => r.From).ToList();
        var merged = new List<(long From, long To)> { sorted[0] };
        for (var i = 1; i < sorted.Count; i++)
        {
            var last = merged[^1];
            if (sorted[i].From <= last.To + 1)
                merged[^1] = (last.From, Math.Max(last.To, sorted[i].To));
            else
                merged.Add(sorted[i]);
        }
        return merged;
    }

    private static async Task CopyRangeAsync(Stream source, Stream destination, long count, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var want = (int)Math.Min(buffer.Length, count);
            var read = await source.ReadAsync(buffer.AsMemory(0, want), ct);
            if (read <= 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            count -= read;
        }
    }

    private static string NormalizePrefix(string prefix)
    {
        // 注意：PathString.StartsWithSegments 对 "/dav/" 式尾斜杠前缀判定深层路径为 false，
        // 故此处统一去尾斜杠（根 "/" 除外），调用方用 TrimStart('/') 取虚拟路径。
        if (!prefix.StartsWith('/')) prefix = "/" + prefix;
        prefix = prefix.TrimEnd('/');
        return prefix.Length == 0 ? "/" : prefix;
    }

    private static Task Locked(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status423Locked;
        return http.Response.WriteAsync("Locked");
    }

    private static Task Forbidden(HttpContext http, string message = "Forbidden")
    {
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        return http.Response.WriteAsync(message);
    }

    private static Task NotImplemented(HttpContext http, string message)
    {
        http.Response.StatusCode = StatusCodes.Status501NotImplemented;
        return http.Response.WriteAsync(message);
    }

    private static async Task<List<DavRule>> LoadRulesAsync(IFreeSql fsql, DavUser user, string protocol)
    {
        // 扁平排序：适用规则（全局 + 本用户角色 + 本用户）统一按 SortOrder 从小到大，
        // 最后一条命中的规则生效（严格 hacdias 语义；SortOrder 跨目标可比）。
        // 协议作用域：Protocols 留空 = 适配所有协议；否则只取命中当前协议的规则。
        var roleIds = await fsql.Select<DavUserRole>().Where(r => r.UserId == user.Id).ToListAsync(r => r.RoleId);
        var rules = await fsql.Select<DavRule>().Where(r => r.IsEnabled).OrderBy(r => r.SortOrder).ToListAsync();
        return rules
            .Where(r => (r.UserId == null && r.RoleId == null) // 全局
                || (r.RoleId != null && roleIds.Contains(r.RoleId.Value)) // 角色
                || (r.UserId == user.Id)) // 用户
            .Where(r => DavRuleScope.AppliesTo(r.Protocols, protocol))
            .OrderBy(r => r.SortOrder)
            .ToList();
    }

    /// <summary>
    /// 集合 COPY/MOVE。对齐参考：COPY 为过滤式部分拷贝（无 Read 的条目被留下，
    /// 不整体拒绝）；MOVE 要求整树 R+D（<c>allowedThroughout</c>），有拒绝后代则
    /// 整体 403；目标已存在时无合并语义（Overwrite:F → 412，否则集合目标 409）。
    /// </summary>
    private static async Task CopyOrMoveCollectionAsync(
        HttpContext http, string method,
        string physical, string rulePath,
        string destPhysical, string destRulePath,
        DavPermission srcNeed, bool overwriteDenied,
        DavPermission defaults, List<DavRule> rules,
        StringComparison comparison,
        Func<string, DavPermission, bool> allowed,
        DavLockManager locks, HashSet<string> presentedTokens)
    {
        var destExists = Directory.Exists(destPhysical) || File.Exists(destPhysical);
        if (destExists && overwriteDenied)
        {
            http.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            await http.Response.WriteAsync("Precondition Failed: destination exists, Overwrite is F");
            return;
        }
        if (destExists)
        {
            http.Response.StatusCode = StatusCodes.Status409Conflict;
            await http.Response.WriteAsync("Conflict: destination exists, collection merge not supported");
            return;
        }

        if (!allowed(rulePath, srcNeed) || !allowed(destRulePath, DavPermission.Create))
        {
            await Forbidden(http);
            return;
        }
        // 锁：COPY 只查目标（读源不需解锁）；MOVE 源整树 + 目标都要可写。
        if (method == "MOVE" && !locks.CheckWrite(physical, presentedTokens, deep: true))
        {
            await Locked(http);
            return;
        }
        if (!locks.CheckWrite(destPhysical, presentedTokens, deep: false))
        {
            await Locked(http);
            return;
        }

        if (method == "MOVE")
        {
            if (!DavTreeWalker.AllSatisfy(physical, rulePath, defaults, rules, srcNeed, comparison))
            {
                await Forbidden(http, "Forbidden: denied descendant inside collection");
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPhysical)!);
                Directory.Move(physical, destPhysical);
            }
            catch (IOException)
            {
                // 跨卷/跨挂载：退化为全量拷贝 + 删源（整树已鉴权，无需过滤）。
                DavTreeWalker.CopyWhereAllowed(physical, destPhysical, rulePath, defaults, rules, comparison);
                Directory.Delete(physical, true);
            }
            http.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        // COPY：过滤式部分拷贝，拒绝条目被留下。
        Directory.CreateDirectory(Path.GetDirectoryName(destPhysical)!);
        DavTreeWalker.CopyWhereAllowed(physical, destPhysical, rulePath, defaults, rules, comparison);
        http.Response.StatusCode = StatusCodes.Status201Created;
    }

    private async Task<(string Physical, string RulePath, bool IsRoot)?> MapDestinationAsync(HttpContext http, string prefix)
    {
        var destHeader = http.Request.Headers["Destination"].ToString();
        if (string.IsNullOrEmpty(destHeader) || !Uri.TryCreate(destHeader, UriKind.Absolute, out var destUri))
            return null;
        var destPath = WebUtility.UrlDecode(destUri.AbsolutePath);
        if (!destPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var destVirtual = destPath[prefix.Length..].TrimStart('/');
        // 目标同样规范化（越根 → 400；Destination 是请求头，需一次解码）。
        var canonicalDest = DavPathSecurity.CanonicalizeVirtualPath(destVirtual);
        if (canonicalDest is null)
            return null;
        destVirtual = canonicalDest;
        var (mountName, rel) = DavPathSecurity.SplitMount(destVirtual);
        var mount = await http.RequestServices.GetRequiredService<IFreeSql>()
            .Select<DavMount>().Where(m => m.IsEnabled && m.Name == mountName).FirstAsync();
        var root = mount?.PhysicalPath ?? _dav.Directory;
        var destRel = mount is not null ? rel : destVirtual;
        try
        {
            return (DavPathSecurity.ResolveSafePath(root, destRel), "/" + destVirtual.TrimStart('/'), string.IsNullOrEmpty(destRel));
        }
        catch (DavPathTraversalException)
        {
            return null;
        }
    }

    private static async Task PropfindAsync(
        HttpContext http, string physical, string rulePath,
        DavPermission defaults, List<DavRule> rules, StringComparison comparison,
        List<DavMount> mounts, bool listMounts)
    {
        if (!Directory.Exists(physical) && !File.Exists(physical))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var depth = http.Request.Headers["Depth"].ToString();
        var sb = new StringBuilder();
        AppendResource(sb, rulePath, physical);

        if (!Directory.Exists(physical))
        {
            // 文件自身：depth 无意义，直接返回单条。
        }
        else if (depth == "1" || string.IsNullOrEmpty(depth))
        {
            if (listMounts)
                AppendMounts(sb, mounts, defaults, rules, comparison);
            else
                AppendDirectChildren(sb, physical, rulePath, defaults, rules, comparison);
        }
        else if (depth.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            var visited = new HashSet<string>(PlatformPath.Comparer);
            if (listMounts)
            {
                AppendMounts(sb, mounts, defaults, rules, comparison);
                // infinity 递归进每个可见挂载
                foreach (var mount in mounts)
                {
                    var childRule = "/" + mount.Name;
                    if (!DavPermissionEvaluator.Evaluate(childRule, defaults, rules, comparison)
                        .HasFlag(DavPermission.Read))
                        continue;
                    if (!Directory.Exists(mount.PhysicalPath))
                        continue;
                    AppendChildrenRecursive(sb, mount.PhysicalPath, childRule,
                        defaults, rules, comparison, visited, 1);
                }
            }
            else
            {
                AppendChildrenRecursive(sb, physical, rulePath, defaults, rules, comparison, visited, 0);
            }
        }

        http.Response.StatusCode = 207;
        http.Response.ContentType = "application/xml";
        await http.Response.WriteAsync(Multistatus(sb.ToString()));
    }

    /// <summary>
    /// 虚拟根挂载入口（对齐参考 multiDir.rootEntries）：每个启用的挂载为一个集合条目，
    /// 无 Read 权限的隐藏；物理路径不存在的挂载跳过（防 AppendResource 抛 500）。
    /// </summary>
    private static void AppendMounts(
        StringBuilder sb, List<DavMount> mounts,
        DavPermission defaults, List<DavRule> rules, StringComparison comparison)
    {
        foreach (var mount in mounts)
        {
            // 挂载级不可见：不列出但可直达（权限照常校验）。
            if (!mount.IsVisible)
                continue;
            var childRule = "/" + mount.Name;
            var access = DavPermissionEvaluator.EvaluateEffective(childRule, defaults, rules, comparison);
            if (!access.Permissions.HasFlag(DavPermission.Read))
                continue;
            if (!access.IsVisible)
                continue;
            if (!Directory.Exists(mount.PhysicalPath))
                continue;
            AppendResource(sb, childRule, mount.PhysicalPath);
        }
    }

    private static void AppendDirectChildren(
        StringBuilder sb, string physical, string rulePath,
        DavPermission defaults, List<DavRule> rules, StringComparison comparison)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(physical);
        }
        catch
        {
            return;
        }
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            var childRule = rulePath.TrimEnd('/') + "/" + name;
            // 集合列表隐藏无 Read 权限的条目（hacdias 语义），以及被标记为“隐藏”的条目
            // （隐藏=不在列表出现但知道 URL 仍可访问）。
            var access = DavPermissionEvaluator.EvaluateEffective(childRule, defaults, rules, comparison);
            if (!access.Permissions.HasFlag(DavPermission.Read) || !access.IsVisible)
                continue;
            AppendResource(sb, childRule, entry);
        }
    }

    private const int MaxPropfindDepth = 128;

    private static void AppendChildrenRecursive(
        StringBuilder sb, string physicalDir, string rulePath,
        DavPermission defaults, List<DavRule> rules, StringComparison comparison,
        HashSet<string> visited, int depth)
    {
        if (depth > MaxPropfindDepth)
            return;
        string full;
        try
        {
            full = Path.GetFullPath(physicalDir);
        }
        catch
        {
            return;
        }
        if (!visited.Add(full))
            return; // 目录 symlink 环：已访问即止
        AppendDirectChildrenCollect(sb, physicalDir, rulePath, defaults, rules, comparison, visited, depth);
    }

    private static void AppendDirectChildrenCollect(
        StringBuilder sb, string physicalDir, string rulePath,
        DavPermission defaults, List<DavRule> rules, StringComparison comparison,
        HashSet<string> visited, int depth)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(physicalDir);
        }
        catch
        {
            return;
        }
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            var childRule = rulePath.TrimEnd('/') + "/" + name;
            var access = DavPermissionEvaluator.EvaluateEffective(childRule, defaults, rules, comparison);
            if (!access.Permissions.HasFlag(DavPermission.Read) || !access.IsVisible)
                continue;
            AppendResource(sb, childRule, entry);
            if (Directory.Exists(entry))
                AppendChildrenRecursive(sb, entry, childRule, defaults, rules, comparison, visited, depth + 1);
        }
    }

    private static void AppendResource(StringBuilder sb, string rulePath, string physical)
    {
        var isDir = Directory.Exists(physical);
        var lastWrite = isDir
            ? Directory.GetLastWriteTimeUtc(physical)
            : File.GetLastWriteTimeUtc(physical);
        var length = isDir ? 0 : new FileInfo(physical).Length;
        sb.Append("<D:response><D:href>").Append(Href(rulePath)).Append("</D:href>")
          .Append("<D:propstat><D:prop>")
          .Append("<D:displayname>").Append(SecurityElement.Escape(Path.GetFileName(rulePath.TrimEnd('/')))).Append("</D:displayname>")
          .Append("<D:getlastmodified>").Append(lastWrite.ToString("R")).Append("</D:getlastmodified>")
          .Append("<D:resourcetype>").Append(isDir ? "<D:collection/>" : string.Empty).Append("</D:resourcetype>");
        if (!isDir)
            sb.Append("<D:getcontentlength>").Append(length).Append("</D:getcontentlength>");
        sb.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
    }

    private static string Href(string rulePath)
    {
        var prefix = "/dav/";
        return prefix + string.Join('/', rulePath.TrimStart('/').Split('/').Select(Uri.EscapeDataString));
    }

    private static string Multistatus(string inner) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">{inner}</D:multistatus>";

    private static string ContentType(string physical) =>
        Path.GetExtension(physical).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html",
            ".txt" => "text/plain",
            ".xml" => "application/xml",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
}
