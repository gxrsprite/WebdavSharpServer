using System.Runtime.InteropServices;
using System.Security.Claims;
using FmiSrl.FtpServer.Server.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Serilog;
using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;
using WebdavSharp.Server;
using WebdavSharp.Server.Components;
using WebdavSharp.Server.WebDav;

var builder = WebApplication.CreateBuilder(args);

// ---- Serilog：尽早接管，使启动期日志（含下方 Dav roots）也走 Serilog ----
// 级别与 Console 输出模板来自 appsettings.json 的 "Serilog" 段；
// **文件 sink 一律在代码里加**，路径锚到 ContentRoot（与 Dav 路径锚定一致）——
// 若把相对路径写在配置里，Serilog 会跟随进程 CWD，换个目录启动就写到别处。
var serilogConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext();

// 配置里没定义任何 sink 时兜底，避免完全没有输出
if (!builder.Configuration.GetSection("Serilog:WriteTo").Exists())
    serilogConfig.WriteTo.Console();

serilogConfig.WriteTo.File(
    Path.Combine(builder.Environment.ContentRootPath, "LocalPaths", "logs", "webdavsharp-.log"),
    rollingInterval: RollingInterval.Day,
    retainedFileCountLimit: 14,
    shared: true,
    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

Log.Logger = serilogConfig.CreateLogger();
builder.Host.UseSerilog();
var dav = builder.Configuration.GetSection(DavServerOptions.SectionName).Get<DavServerOptions>() ?? new();
var contentRoot = builder.Environment.ContentRootPath;
AnchorDavPaths(contentRoot, dav);
builder.Services.Configure<DavServerOptions>(builder.Configuration.GetSection(DavServerOptions.SectionName));
// 关键：中间件/种子服务通过 IOptions 拿到的必须是锚定后的同一份，
// 否则相对路径（LocalPaths/...）会在各处按 CWD 重复拼接，悄悄造出第二套库/数据根。
// （实测教训：曾出现 Server/LocalPaths/dav-root/LocalPaths/dav-root 嵌套怪目录。）
builder.Services.PostConfigure<DavServerOptions>(o => AnchorDavPaths(contentRoot, o));

static void AnchorDavPaths(string root, DavServerOptions o)
{
    o.Directory = Abs(root, o.Directory);
    if (o.Database.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var m = System.Text.RegularExpressions.Regex.Match(o.ConnectionString, @"Data Source=(?<p>[^;]+)");
        if (m.Success)
        {
            var dbPath = Abs(root, m.Groups["p"].Value.Trim());
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            o.ConnectionString = $"Data Source={dbPath}";
        }
    }
    Directory.CreateDirectory(o.Directory);
}

static string Abs(string root, string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(root, p));

// ---- FreeSql（SQLite 默认；生产切 PG/MySQL 只改 Dav:Database + 连接串） ----
var dataType = dav.Database.ToLowerInvariant() switch
{
    "postgresql" or "pgsql" => FreeSql.DataType.PostgreSQL,
    "mysql" => FreeSql.DataType.MySql,
    "sqlserver" => FreeSql.DataType.SqlServer,
    _ => FreeSql.DataType.Sqlite,
};

// SQLite 原生库初始化与平台检查（跨平台）。
// 用 Microsoft.Data.Sqlite 系（FreeSql.Provider.SqliteCore）+ SQLitePCLRaw.bundle_e_sqlite3：
// 该 bundle 带 linux-x64/arm64、osx-x64/arm64、win-x64/arm86/arm64 等全架构 e_sqlite3 原生库。
// Microsoft.Data.Sqlite.Core 不自带 bundle，必须显式初始化 Batteries（幂等）。
// 初始化失败时给出可操作提示，而不是留到运行期抛晦涩的 DllNotFoundException。
if (dataType == FreeSql.DataType.Sqlite)
{
    try
    {
        SQLitePCL.Batteries_V2.Init();
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"SQLite 原生库初始化失败（RID={RuntimeInformation.RuntimeIdentifier}，" +
            $"架构={RuntimeInformation.ProcessArchitecture}）：{ex.Message}。" +
            "请确认已引用 SQLitePCLRaw.bundle_e_sqlite3 且发布物包含对应 RID 的 e_sqlite3 原生库；" +
            "或改用 PostgreSQL/MySQL（Dav:Database）。", ex);
    }
}

var fsql = new FreeSql.FreeSqlBuilder()
    .UseConnectionString(dataType, dav.ConnectionString)
    .UseAutoSyncStructure(dav.AutoSyncStructure)
    .Build();
builder.Services.AddSingleton<IFreeSql>(fsql);
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddScoped<DavAuthService>();
builder.Services.AddSingleton<WebdavSharp.Server.WebDav.DavLockManager>();
builder.Services.AddSingleton<WebdavSharp.Server.WebDav.DavBruteForceGuard>();
builder.Services.AddHostedService<WebdavSharp.Server.Services.DavAuditCleanupService>();

// ---- 协议无关访问脊柱：WebDAV / SMB / FTP 共用认证、挂载与规则 ----
builder.Services.AddSingleton<DavAccessService>();

// ---- 协议服务配置（DB 驱动；配置文件只提供初始种子，见 SeedData） ----
builder.Services.AddSingleton<DavServiceSettings>();

// ---- FTP 服务（FmiSrl.FtpServer）：认证与文件系统都接入我们的规则 ----
// 常驻注册、启动与否看 DB（管理页可改）：未启用时不绑定端口，零副作用。
builder.Services.AddFtpServer()
    .UseAuthenticationProvider<WebdavSharp.Server.Ftp.DavFtpAuthenticationProvider>()
    .UseFileSystemProvider<WebdavSharp.Server.Ftp.DavFtpFileSystemProvider>();

builder.Services.Configure<FmiSrl.FtpServer.Server.FtpServerConfigurationOptions>(o =>
{
    // 只放与 DB 无关的项；监听 IP/端口每次启动都由 DavFtpHostedService 按 DB 重写。
    // 被动模式端口范围：留 0 则交给系统分配（NAT 后建议显式指定并放行）
    if (dav.FtpPassivePortStart > 0 && dav.FtpPassivePortEnd >= dav.FtpPassivePortStart)
    {
        o.PasvMinPort = dav.FtpPassivePortStart;
        o.PasvMaxPort = dav.FtpPassivePortEnd;
    }
    o.ServerName = "WebdavSharp";
});

builder.Services.AddSingleton<WebdavSharp.Server.Ftp.DavFtpHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebdavSharp.Server.Ftp.DavFtpHostedService>());

// ---- SMB 服务（SMBLibrary）：每个虚拟挂载 = 一个共享，规则在 INTFileStore 层强制 ----
// 同样常驻注册、启动与否看 DB。
builder.Services.AddSingleton<WebdavSharp.Server.Smb.DavSmbHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebdavSharp.Server.Smb.DavSmbHostedService>());

// ---- 管理后台 Cookie 认证（WebDAV Basic 走中间件，见 DavMiddleware） ----
builder.Services.AddAuthentication("DavAdmin")
    .AddCookie("DavAdmin", options =>
    {
        options.Cookie.Name = "DavAdmin_Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.MaxAge = TimeSpan.FromDays(15); // 对齐 WebBlazor 15 天
        options.LoginPath = "/admin/login";
        options.AccessDeniedPath = "/admin/login";
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (dav.Cors.Enabled)
    {
        // "*" + Credentials 在 ASP.NET Core 下非法（WithOrigins("*") 会启动炸）；
        // 用 SetIsOriginAllowed 回显 Origin，对齐 hacdias allowed_hosts ['*'] 行为。
        if (dav.Cors.AllowedHosts.Contains("*"))
            policy.SetIsOriginAllowed(_ => true);
        else
            policy.WithOrigins(dav.Cors.AllowedHosts);
        policy.AllowAnyHeader()
            .WithMethods("COPY", "DELETE", "GET", "HEAD", "LOCK", "UNLOCK",
                "MKCOL", "MOVE", "OPTIONS", "PATCH", "POST", "PROPFIND", "PROPPATCH", "PUT")
            .WithExposedHeaders("Dav", "Content-Range", "Lock-Token");
        if (dav.Cors.Credentials)
            policy.AllowCredentials();
    }
}));

builder.WebHost.UseUrls($"http://{dav.Address}:{dav.Port}");

var app = builder.Build();

// 启动时打印解析后的绝对路径：相对路径锚到 ContentRoot，
// 若启动目录不对会悄悄建出第二套库/数据根（split-brain），这行日志是第一发现手段。
app.Logger.LogInformation(
    "Dav roots: ContentRoot={ContentRoot} Directory={Directory} Db={ConnectionString} Prefix={Prefix}",
    contentRoot, dav.Directory, dav.ConnectionString, dav.Prefix);

if (dav.NoPassword)
{
    app.Logger.LogWarning("Dav:NoPassword=true：DAV Basic 不验口令（委托上游代理），请确保服务只监听可信来源！");
}

SeedData.Initialize(app.Services);

if (dav.Cors.Enabled)
    app.UseCors();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ---- 管理后台登录/登出（表单 POST，供 Login.razor 纯 HTML 表单用） ----
app.MapPost("/api/admin/login", async (
    HttpContext http,
    IFreeSql db,
    PasswordHasher hasher,
    IOptions<DavServerOptions> opts) =>
{
    var form = await http.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var auth = http.RequestServices.GetRequiredService<DavAuthService>();
    var user = await auth.AuthenticateBasicAsync(username, password);
    if (user is null)
        return Results.Redirect("/admin/login?error=1");

    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim("auth_version", user.AuthVersion.ToString()),
    };
    await http.SignInAsync("DavAdmin", new ClaimsPrincipal(new ClaimsIdentity(claims, "DavAdmin")),
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(15) });
    return Results.Redirect("/admin");
}).DisableAntiforgery();

app.MapPost("/api/admin/logout", async (HttpContext http) =>
{
    await http.SignOutAsync("DavAdmin");
    return Results.Redirect("/admin/login");
}).DisableAntiforgery();

// ---- 协议服务管理 API（管理页 + 运维脚本用；与 Blazor 页同一套 Cookie 鉴权） ----
app.MapGet("/api/admin/services", async (DavServiceSettings settings) =>
    Results.Ok(await settings.GetAllAsync()))
    .RequireAuthorization().DisableAntiforgery();

app.MapPut("/api/admin/services/{protocol}", async (
    string protocol, ServiceUpdateDto dto, DavServiceSettings settings) =>
{
    var (ok, error) = await settings.SetAsync(
        protocol, dto.IsEnabled, dto.Address, dto.Port);
    return ok ? Results.Ok(await settings.GetAsync(protocol)) : Results.BadRequest(new { error });
}).RequireAuthorization().DisableAntiforgery();

app.MapPost("/api/admin/services/{protocol}/restart", async (
    string protocol,
    DavServiceSettings settings,
    WebdavSharp.Server.Ftp.DavFtpHostedService ftp,
    WebdavSharp.Server.Smb.DavSmbHostedService smb) =>
{
    if (!DavServiceSettings.IsKnownProtocol(protocol))
        return Results.BadRequest(new { error = $"未知协议：{protocol}" });
    // WebDAV 就是宿主自身：开关即时生效，但 IP/端口是 Kestrel 绑定，重启应用才生效。
    if (protocol.Equals(DavProtocols.WebDav, StringComparison.OrdinalIgnoreCase))
    {
        var s = await settings.GetAsync(protocol);
        return Results.Ok(new
        {
            running = s.IsEnabled,
            message = s.IsEnabled ? "WebDAV 运行中（IP/端口改动需重启应用）" : "WebDAV 已禁用（即时生效）",
        });
    }
    var (ok, message) = protocol.Equals(DavProtocols.Ftp, StringComparison.OrdinalIgnoreCase)
        ? await ftp.RestartAsync()
        : await smb.RestartAsync();
    // running 取实例实时状态（禁用态重启会停服务，此时 ok=true 但 running=false，别混淆）
    var running = protocol.Equals(DavProtocols.Ftp, StringComparison.OrdinalIgnoreCase)
        ? ftp.IsRunning
        : smb.IsRunning;
    return Results.Ok(new { ok, running, message });
}).RequireAuthorization().DisableAntiforgery();

// ---- 健康探针（免认证，供编排/负载均衡） ----
app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/readyz", (IFreeSql db) =>
{
    try
    {
        db.Select<DavUser>().Limit(1).ToList();
        return Results.Ok("ready");
    }
    catch
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

// ---- WebDAV（Basic 认证 + 权限卡点，见 WebDav/DavMiddleware.cs） ----
app.UseMiddleware<DavMiddleware>();

// ---- 静态资源：Blazor 框架脚本（_framework/blazor.web.js）等属于静态 Web 资产，
// 必须显式映射，否则管理页只出 SSR 静态 HTML、脚本 404 —— 页面能看但所有
// @onclick 按钮（建用户/加挂载/加规则/清理审计）全部无反应。
// 用 MapStaticAssets（.NET 9+ 推荐，按构建生成的资产清单服务，
// 内置指纹与压缩协商）。注意清单是构建期生成的，改 wwwroot 结构后需重建。
app.MapStaticAssets();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "主机异常终止");
    throw;
}
finally
{
    // 确保退出前把缓冲日志刷盘（否则崩溃时最后几条丢失）
    await Log.CloseAndFlushAsync();
}

/// <summary>协议服务配置更新体（管理 API <c>PUT /api/admin/services/{protocol}</c> 用）。</summary>
public sealed record ServiceUpdateDto(bool IsEnabled, string? Address, int Port);
