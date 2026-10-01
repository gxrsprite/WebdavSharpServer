using FmiSrl.FtpServer.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;

namespace WebdavSharp.Server.Ftp;

/// <summary>
/// FTP 服务生命周期托管：<see cref="FtpServer"/> 需显式 StartAsync/StopAsync。
/// <para>
/// 启停与监听地址/端口来自 <see cref="DavServiceSettings"/>（DB），
/// 管理页可改、即时重启生效；配置文件只提供初始种子。
/// 同一个实例既是 <see cref="IHostedService"/>（随主机启停），
/// 也接受管理页/API 直接调用 <c>StartNowAsync/StopNowAsync/RestartAsync</c>。
/// </para>
/// </summary>
public sealed class DavFtpHostedService(
    FtpServer server,
    IOptions<FtpServerConfigurationOptions> ftpOptions,
    IOptions<DavServerOptions> options,
    DavServiceSettings settings,
    ILogger<DavFtpHostedService> logger) : IHostedService
{
    private readonly DavServerOptions _dav = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>当前是否在监听（内存状态，重启后按 DB 重算）。</summary>
    public bool IsRunning { get; private set; }

    // ---- 主机生命周期 ----

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var s = await settings.GetAsync(DavProtocols.Ftp, cancellationToken);
        if (!s.IsEnabled)
        {
            logger.LogInformation("FTP 未启用（管理页 /admin/services 可开启）");
            return;
        }
        var (ok, msg) = await StartCoreAsync(s);
        if (ok)
            logger.LogInformation("FTP 服务已启动");
        else
            logger.LogError("FTP 启动失败：{Msg}（WebDAV/SMB 不受影响，可继续使用）", msg);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopCoreAsync();
        logger.LogInformation("FTP 服务已停止");
    }

    // ---- 管理页/API 手动控制 ----

    public async Task<(bool Ok, string Message)> StartNowAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning)
                return (true, "FTP 本来就在运行");
            var s = await settings.GetAsync(DavProtocols.Ftp);
            if (!s.IsEnabled)
                return (false, "FTP 当前为禁用状态，请先在管理页启用");
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
                return (true, "FTP 本来就没在跑");
            await StopCoreAsync();
            return (true, "FTP 已停止");
        }
        finally { _gate.Release(); }
    }

    public async Task<(bool Ok, string Message)> RestartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            var s = await settings.GetAsync(DavProtocols.Ftp);
            if (!s.IsEnabled)
                return (true, "FTP 已停止（当前为禁用状态）");
            return await StartCoreAsync(s);
        }
        finally { _gate.Release(); }
    }

    // ---- 内核（调用方已持锁） ----

    private async Task<(bool Ok, string Message)> StartCoreAsync(DavServiceSetting s)
    {
        try
        {
            // 绑定参数每次启动都从 DB 重读：管理页改完端口/IP 后重启即生效。
            // 前提是 FtpServer 在 StartAsync 时才消费 options（已用改端口实测验证过）。
            var o = ftpOptions.Value;
            o.ListeningIp = string.IsNullOrWhiteSpace(s.Address) ? _dav.Address : s.Address;
            o.FtpPort = s.Port;
            await server.StartAsync();
            IsRunning = true;
            logger.LogInformation("FTP 监听 {Ip}:{Port}", o.ListeningIp, o.FtpPort);
            return (true, $"FTP 已启动（{o.ListeningIp}:{o.FtpPort}）");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            logger.LogError(ex, "FTP 启动失败");
            return (false, $"启动失败：{ex.Message}（端口被占用？Linux 上 <1024 需 root？）");
        }
    }

    private async Task StopCoreAsync()
    {
        try
        {
            await server.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "停止 FTP 时出错（忽略，继续标记为已停）");
        }
        IsRunning = false;
    }
}
