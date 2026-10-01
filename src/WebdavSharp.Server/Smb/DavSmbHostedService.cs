using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Server;
using SMBLibrary.Services;
using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;

namespace WebdavSharp.Server.Smb;

/// <summary>
/// SMB 服务生命周期与共享注册。
/// <para>
/// 映射策略（已确认）：**每个启用的虚拟挂载 = 一个 SMB 共享**，
/// 于是 <c>\\host\NAS\剧集</c> 与 WebDAV 的 <c>/NAS/剧集</c>、FTP 的 <c>/NAS/剧集</c>
/// 指向同一物理位置并受同一条规则约束。
/// </para>
/// <para>
/// 启停与监听地址/端口来自 <see cref="DavServiceSettings"/>（DB），
/// 管理页可改、重启即生效；配置文件只提供初始种子。方言开关仍走配置文件。
/// 同一个实例既是 <see cref="IHostedService"/>（随主机启停），
/// 也接受管理页/API 直接调用 <c>StartNowAsync/StopNowAsync/RestartAsync</c>。
/// 重启会重建共享列表，增删挂载后点一次重启即生效。
/// </para>
/// <para>
/// 端口：SMB 走 445（Direct TCP）或 139（NetBIOS over TCP），协议固定、不可自定义；
/// Linux 上绑定 445 需要 root 或 <c>setcap cap_net_bind_service</c>。
/// </para>
/// </summary>
public sealed class DavSmbHostedService(
    DavAccessService access,
    IServiceProvider services,
    IOptions<DavServerOptions> options,
    DavServiceSettings settings,
    ILogger<DavSmbHostedService> logger) : IHostedService
{
    private readonly DavServerOptions _dav = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SMBServer? _server;

    /// <summary>当前是否在监听（内存状态，重启后按 DB 重算）。</summary>
    public bool IsRunning { get; private set; }

    // ---- 主机生命周期 ----

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var s = await settings.GetAsync(DavProtocols.Smb, cancellationToken);
        if (!s.IsEnabled)
        {
            logger.LogInformation("SMB 未启用（管理页 /admin/services 可开启）");
            return;
        }
        var (ok, msg) = await StartCoreAsync(s, cancellationToken);
        if (!ok)
            logger.LogError("SMB 启动失败：{Msg}（WebDAV/FTP 不受影响，可继续使用）", msg);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopCore();
        logger.LogInformation("SMB 服务已停止");
        return Task.CompletedTask;
    }

    // ---- 管理页/API 手动控制 ----

    public async Task<(bool Ok, string Message)> StartNowAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning)
                return (true, "SMB 本来就在运行");
            var s = await settings.GetAsync(DavProtocols.Smb);
            if (!s.IsEnabled)
                return (false, "SMB 当前为禁用状态，请先在管理页启用");
            return await StartCoreAsync(s);
        }
        finally { _gate.Release(); }
    }

    public async Task<(bool Ok, string Message)> StopNowAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning)
                return (true, "SMB 本来就没在跑");
            StopCore();
            return (true, "SMB 已停止");
        }
        finally { _gate.Release(); }
    }

    public async Task<(bool Ok, string Message)> RestartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            StopCore();
            var s = await settings.GetAsync(DavProtocols.Smb);
            if (!s.IsEnabled)
                return (true, "SMB 已停止（当前为禁用状态）");
            return await StartCoreAsync(s);
        }
        finally { _gate.Release(); }
    }

    // ---- 内核（调用方已持锁；主机 Stop 除外，它只做幂等停止） ----

    private async Task<(bool Ok, string Message)> StartCoreAsync(
        DavServiceSetting s, CancellationToken cancellationToken = default)
    {
        var mounts = await access.LoadMountsAsync(cancellationToken);
        if (mounts.Count == 0)
            return (false, "没有任何启用的虚拟挂载（SMB 共享由挂载生成），请先到“虚拟挂载”页添加");

        var shares = new SMBShareCollection();
        foreach (var mount in mounts)
        {
            var store = new DavSmbFileStore(
                access, mount, services.GetRequiredService<ILogger<DavSmbFileStore>>());
            shares.Add(new FileSystemShare(mount.Name, store, CachingPolicy.NoCaching));
            logger.LogInformation("SMB 共享已注册：{Share} -> {Path}", mount.Name, mount.PhysicalPath);
        }

        // CX 等 Android 客户端先连接根目录，再通过 IPC$/srvsvc 的
        // NetShareEnum 获取 public、NAS 等共享列表。没有 IPC$ 时，
        // 认证虽成功，但根目录会直接显示“加载错误”。
        var srvsvc = new ServerService(_dav.SmbServerName, mounts.Select(m => m.Name).ToList());
        shares.Add(new FileSystemShare(
            "IPC$",
            new NamedPipeStore([srvsvc]),
            CachingPolicy.NoCaching));
        logger.LogInformation("SMB IPC$ 共享已注册：srvsvc 可枚举 {Count} 个共享", mounts.Count);

        var gss = new GSSProvider(new DavNtlmMechanism(
            access, services.GetRequiredService<ILogger<DavNtlmMechanism>>(), _dav.SmbServerName));

        _server = new SMBServer(shares, gss);

        var addressText = string.IsNullOrWhiteSpace(s.Address) ? _dav.Address : s.Address;
        var address = ParseAddress(addressText);

        // 445/139 常被系统自带文件共享占用（Windows 上是 LanmanServer，PID 4）。
        try
        {
            if (s.Port == 445)
            {
                _server.Start(address, SMBTransportType.DirectTCPTransport,
                    _dav.SmbEnableSmb1, _dav.SmbEnableSmb2, _dav.SmbEnableSmb3);
            }
            else
            {
                // 非标准端口：SMBLibrary 的公开 Start() 写死 445，只能反射内部重载。
                // 主流客户端（Explorer/net use/Finder/手机）只连 445，此模式仅面向
                // 支持指定端口的客户端（smbclient -p、pysmb 等），适用于 445 被占用、
                // 多实例、联调场景。
                logger.LogWarning(
                    "SMB 使用非标准端口 {Port}（主流客户端只连 445，请确认客户端支持指定端口）", s.Port);
                var startWithPort = typeof(SMBServer).GetMethod(
                    "Start",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    binder: null,
                    types: [
                        typeof(IPAddress), typeof(SMBLibrary.SMBTransportType), typeof(int),
                        typeof(bool), typeof(bool), typeof(bool), typeof(TimeSpan?),
                    ],
                    modifiers: null)
                    ?? throw new MissingMethodException("SMBServer", "Start(IPAddress, SMBTransportType, int, ...)");
                startWithPort.Invoke(_server, [
                    address, SMBTransportType.DirectTCPTransport, s.Port,
                    _dav.SmbEnableSmb1, _dav.SmbEnableSmb2, _dav.SmbEnableSmb3, null,
                ]);
            }
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            _server = null;
            return (false,
                $"无法绑定 {address}:{s.Port}：{ex.Message}（通常是系统自带文件共享已占用；" +
                "Windows 上需停止 LanmanServer 服务，Linux 上 1024 以下端口需 root 或 setcap cap_net_bind_service）");
        }
        catch (Exception ex)
        {
            _server = null;
            return (false, $"启动失败：{ex.Message}");
        }

        IsRunning = true;
        var names = string.Join(",", mounts.Select(m => m.Name));
        logger.LogInformation(
            "SMB 服务已启动：{Address}:{Port} 共享={Shares} 服务器名={Name}（SMB1={S1} SMB2={S2} SMB3={S3}）",
            address, s.Port, names, _dav.SmbServerName,
            _dav.SmbEnableSmb1, _dav.SmbEnableSmb2, _dav.SmbEnableSmb3);
        return (true, $"SMB 已启动（{address}:{s.Port}，共享：{names}）");
    }

    private void StopCore()
    {
        try
        {
            _server?.Stop();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "停止 SMB 服务时出错（忽略，继续标记为已停）");
        }
        _server = null;
        IsRunning = false;
    }

    private static IPAddress ParseAddress(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "0.0.0.0")
            return IPAddress.Any;
        if (text == "::" )
            return IPAddress.IPv6Any;
        return IPAddress.TryParse(text, out var ip) ? ip : IPAddress.Any;
    }
}
