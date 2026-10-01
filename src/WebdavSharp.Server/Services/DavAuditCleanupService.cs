using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;

namespace WebdavSharp.Server.Services;

/// <summary>
/// 审计保留后台任务：按 <c>Dav:AuditRetainDays</c> 定期删除过期审计行。
/// 首次启动 1 分钟后跑一次，之后按 <c>Dav:AuditCleanupIntervalHours</c> 间隔。
/// 失败只记日志，不影响服务（best-effort）。
/// </summary>
public sealed class DavAuditCleanupService(
    IServiceProvider services,
    IOptions<DavServerOptions> options,
    ILogger<DavAuditCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retainDays = options.Value.AuditRetainDays;
        if (retainDays < 1)
        {
            logger.LogInformation("审计自动清理已关闭（AuditRetainDays={Days}）。", retainDays);
            return;
        }

        var interval = TimeSpan.FromHours(options.Value.AuditCleanupIntervalHours);
        if (interval < TimeSpan.FromMinutes(1))
            interval = TimeSpan.FromMinutes(1);

        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var n = Purge(scope.ServiceProvider.GetRequiredService<IFreeSql>(), retainDays);
                if (n > 0)
                    logger.LogInformation("审计清理：删除 {Count} 条 {Days} 天前记录。", n, retainDays);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "审计清理失败，下个周期重试。");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>删除早于保留天数的审计行，返回删除条数（可单测）。</summary>
    public static int Purge(IFreeSql fsql, int retainDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retainDays);
        return fsql.Delete<DavAuditLog>().Where(l => l.Time < cutoff).ExecuteAffrows();
    }
}
