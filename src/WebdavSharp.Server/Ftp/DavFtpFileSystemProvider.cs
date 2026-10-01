using FmiSrl.FtpServer.Server.Abstractions;
using Microsoft.Extensions.Logging;
using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;

namespace WebdavSharp.Server.Ftp;

/// <summary>
/// FTP 文件系统提供者：**所有**文件操作都经 <see cref="DavSession"/> 做规则校验与路径保护，
/// 与 WebDAV 共用同一套权限语义（首字母映射见各方法注释）。
/// <para>
/// FTP 路径语义：客户端给的是相对虚拟根的 Unix 风格路径（<c>/pub/a.txt</c>），
/// 我们把它映射到同一个虚拟命名空间——因此挂载与规则写法与 WebDAV 完全一致
/// （例如 <c>/NAS/剧集</c> 在 FTP 与 WebDAV 下指向同一物理位置、受同一条规则约束）。
/// </para>
/// </summary>
public sealed class DavFtpFileSystemProvider(
    DavAccessService access,
    ILogger<DavFtpFileSystemProvider> logger) : IFileSystemProvider
{
    /// <summary>
    /// 取会话（已认证连接的后续操作只带 Username）。
    /// 每次重新查库：账号被禁用或规则被改动会立即生效。
    /// </summary>
    private async Task<DavSession> SessionAsync(FtpAuthenticationContext ctx)
    {
        var session = await access.BuildSessionForUsernameAsync(ctx?.Username, DavProtocols.Ftp);
        if (session is null)
            throw new FtpAccessDeniedException($"账号不可用：{ctx?.Username}");
        return session;
    }

    private static string VirtualPathOf(string? ftpPath)
    {
        // FTP 风格 → 虚拟路径（去掉前导 '/'，规范化为相对虚拟根的路径）
        var p = (ftpPath ?? string.Empty).Replace('\\', '/').Trim();
        return p.TrimStart('/');
    }

    /// <summary>解析并鉴权；失败抛 <see cref="FtpAccessDeniedException"/>。</summary>
    private async Task<(DavSession Session, DavSession.Location Location)> AuthorizeAsync(
        FtpAuthenticationContext ctx, string? ftpPath, DavPermission need, string action)
    {
        var session = await SessionAsync(ctx);
        var virtualPath = VirtualPathOf(ftpPath);

        var loc = session.Resolve(virtualPath);
        if (loc is null)
        {
            logger.LogWarning("FTP {Action} 路径越界：user={User} path={Path}", action, ctx?.Username, ftpPath);
            throw new FtpAccessDeniedException("路径非法或越界");
        }

        if (!session.Allowed(loc, need))
        {
            logger.LogWarning("FTP {Action} 权限不足：user={User} path={Path} need={Need}",
                action, ctx?.Username, loc.RulePath, need);
            throw new FtpAccessDeniedException("权限不足");
        }

        return (session, loc);
    }

    // ---------- 读 ----------

    public async Task<IEnumerable<FileSystemEntry>> GetEntriesAsync(FtpAuthenticationContext authContext, string path)
    {
        var (session, loc) = await AuthorizeAsync(authContext, path, DavPermission.Read, "LIST");

        // 虚拟根（无挂载命中）列出挂载入口，与 WebDAV PROPFIND 根一致；
        // 否则 List("") 只会列回退目录的物理子项，虚拟挂载（NAS/D）永远看不见。
        // 注：loc.IsRoot 为 true 时也可能是挂载根（/NAS），那种情况 loc.Mount 非空，
        // 走下面的普通物理列表。
        IReadOnlyList<DavEntry> entries = (loc.Mount is null && loc.IsRoot)
            ? session.ListRootEntries()
            : session.List(VirtualPathOf(path));
        return entries
            .Select(e => new FileSystemEntry(e.Name, e.Length, e.LastWriteTimeUtc.ToLocalTime(), e.IsDirectory))
            .ToList();
    }

    public async Task<FileSystemEntry?> GetEntryAsync(FtpAuthenticationContext authContext, string path)
    {
        var (_, loc) = await AuthorizeAsync(authContext, path, DavPermission.Read, "STAT");

        if (Directory.Exists(loc.PhysicalPath))
        {
            var dirName = Path.GetFileName(loc.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar));
            return new FileSystemEntry(dirName, 0,
                Directory.GetLastWriteTimeUtc(loc.PhysicalPath).ToLocalTime(), true);
        }
        if (!File.Exists(loc.PhysicalPath))
            return null;

        var fi = new FileInfo(loc.PhysicalPath);
        return new FileSystemEntry(fi.Name, fi.Length, fi.LastWriteTimeUtc.ToLocalTime(), false);
    }

    public async Task<Stream> OpenReadAsync(FtpAuthenticationContext authContext, string path)
    {
        var (_, loc) = await AuthorizeAsync(authContext, path, DavPermission.Read, "RETR");
        if (!File.Exists(loc.PhysicalPath))
            throw new FileNotFoundException("文件不存在", loc.PhysicalPath);
        return new FileStream(loc.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public async Task<bool> FileExistsAsync(FtpAuthenticationContext authContext, string path)
    {
        var (_, loc) = await AuthorizeAsync(authContext, path, DavPermission.Read, "EXISTS");
        return File.Exists(loc.PhysicalPath);
    }

    public async Task<bool> DirectoryExistsAsync(FtpAuthenticationContext authContext, string path)
    {
        var (_, loc) = await AuthorizeAsync(authContext, path, DavPermission.Read, "EXISTS");
        return Directory.Exists(loc.PhysicalPath);
    }

    public async Task<long> GetFileSizeAsync(FtpAuthenticationContext authContext, string path)
    {
        var (_, loc) = await AuthorizeAsync(authContext, path, DavPermission.Read, "SIZE");
        return File.Exists(loc.PhysicalPath) ? new FileInfo(loc.PhysicalPath).Length : 0;
    }

    // ---------- 写 ----------

    public async Task<Stream> OpenWriteAsync(FtpAuthenticationContext authContext, string path)
    {
        // 已存在 → 更新(U)；新建 → 创建(C)（与 WebDAV PUT 判定一致）
        var session = await SessionAsync(authContext);
        var virtualPath = VirtualPathOf(path);
        var probe = session.Resolve(virtualPath)
            ?? throw new FtpAccessDeniedException("路径非法或越界");
        var need = File.Exists(probe.PhysicalPath) ? DavPermission.Update : DavPermission.Create;

        var (_, loc) = await AuthorizeAsync(authContext, path, need, "STOR");

        var dir = Path.GetDirectoryName(loc.PhysicalPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        return new FileStream(loc.PhysicalPath, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    public async Task CreateDirectoryAsync(FtpAuthenticationContext authContext, string path)
    {
        var (_, loc) = await AuthorizeAsync(authContext, path, DavPermission.Create, "MKD");
        if (Directory.Exists(loc.PhysicalPath) || File.Exists(loc.PhysicalPath))
            throw new IOException("已存在");
        Directory.CreateDirectory(loc.PhysicalPath);
    }

    public async Task DeleteFileAsync(FtpAuthenticationContext authContext, string path)
    {
        var (_, loc) = await AuthorizeAsync(authContext, path, DavPermission.Delete, "DELE");
        if (File.Exists(loc.PhysicalPath))
            File.Delete(loc.PhysicalPath);
    }

    public async Task DeleteDirectoryAsync(FtpAuthenticationContext authContext, string path)
    {
        var (session, loc) = await AuthorizeAsync(authContext, path, DavPermission.Delete, "RMD");
        if (!Directory.Exists(loc.PhysicalPath))
            throw new DirectoryNotFoundException(loc.PhysicalPath);

        // 整树鉴权（与 WebDAV DELETE 一致）：任一后代缺 D 则整体拒绝
        var comparison = session.ComparisonFor(loc.Mount?.PhysicalPath ?? loc.PhysicalPath);
        if (!DavTreeWalker.AllSatisfy(loc.PhysicalPath, loc.RulePath, session.Defaults, session.Rules,
                DavPermission.Delete, comparison))
            throw new FtpAccessDeniedException("目录内含无删除权限的子项");

        Directory.Delete(loc.PhysicalPath, true);
    }

    public async Task RenameAsync(FtpAuthenticationContext authContext, string oldPath, string newPath)
    {
        // 重命名 = 移动：源需 Delete（并可读），目标按存在与否需 Update/Create
        var session = await SessionAsync(authContext);
        var oldVirtual = VirtualPathOf(oldPath);
        var newVirtual = VirtualPathOf(newPath);

        var (_, src) = await AuthorizeAsync(authContext, oldPath, DavPermission.Delete, "RNFR");

        var destProbe = session.Resolve(newVirtual)
            ?? throw new FtpAccessDeniedException("目标路径非法或越界");
        var destExists = File.Exists(destProbe.PhysicalPath) || Directory.Exists(destProbe.PhysicalPath);
        var (_, dest) = await AuthorizeAsync(authContext, newPath,
            destExists ? DavPermission.Update : DavPermission.Create, "RNTO");

        var destDir = Path.GetDirectoryName(dest.PhysicalPath);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        if (Directory.Exists(src.PhysicalPath))
        {
            // 目录移动：整树需可删（与 WebDAV MOVE 集合一致）
            var comparison = session.ComparisonFor(src.Mount?.PhysicalPath ?? src.PhysicalPath);
            if (!DavTreeWalker.AllSatisfy(src.PhysicalPath, src.RulePath, session.Defaults, session.Rules,
                    DavPermission.Read | DavPermission.Delete, comparison))
                throw new FtpAccessDeniedException("目录内含无权限的子项");
            Directory.Move(src.PhysicalPath, dest.PhysicalPath);
        }
        else if (File.Exists(src.PhysicalPath))
        {
            File.Move(src.PhysicalPath, dest.PhysicalPath, overwrite: destExists);
        }
        else
        {
            throw new FileNotFoundException("源不存在", src.PhysicalPath);
        }
    }
}
