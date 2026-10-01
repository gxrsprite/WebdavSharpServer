using Microsoft.Extensions.Logging;
using SMBLibrary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WebdavSharp.Core.Entities;
using WebdavSharp.Core.Services;

namespace WebdavSharp.Server.Smb;

/// <summary>
/// SMB 的 <c>INTFileStore</c> 实现：**每个共享对应一个虚拟挂载**，
/// 所有文件操作都先经 <see cref="DavSession"/> 做规则校验，与 WebDAV / FTP 同一套语义。
/// <para>
/// 为什么在文件存储层做（而不是共享层）：<c>FileSystemShare.HasAccess</c> 不是 virtual，
/// 无法覆写；而 <c>INTFileStore</c> 是每个操作的必经之路，正是规则的天然卡点。
/// </para>
/// <para>
/// 路径映射：SMB 相对路径 <c>\剧集\a.txt</c>（共享 NAS）→ 虚拟路径 <c>/NAS/剧集/a.txt</c>，
/// 因此 WebDAV 下的 <c>/NAS/剧集</c>、FTP 下的 <c>/NAS/剧集</c>、SMB 下 <c>\\host\NAS\剧集</c>
/// 指向同一物理位置、受同一条规则约束。
/// </para>
/// </summary>
public sealed class DavSmbFileStore(
    DavAccessService access,
    DavMount mount,
    ILogger<DavSmbFileStore> logger) : INTFileStore
{
    private static readonly DateTime Epoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>打开的句柄：缓存会话与权限判定结果，避免每个后续操作重复查库。</summary>
    private sealed class Handle
    {
        public required DavSession Session { get; init; }
        public required string VirtualPath { get; init; }
        public required string RulePath { get; init; }
        public required string PhysicalPath { get; init; }
        public DavMount? Mount { get; init; }
        public DavPermission Granted { get; init; }
        public bool IsDirectory { get; init; }
        public FileStream? Stream { get; set; }

        /// <summary>客户端通过 FileDispositionInformation 请求删除：实际删除推迟到 CloseFile。</summary>
        public bool DeleteOnClose { get; set; }
    }

    private string VirtualOf(string smbPath)
    {
        var rel = (smbPath ?? string.Empty).Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(rel) ? mount.Name : mount.Name + "/" + rel;
    }

    private static DateTime? ToDateTime(DateTime? t) =>
        t is null ? null : (t.Value.Kind == DateTimeKind.Utc ? t.Value : t.Value.ToUniversalTime());

    private static long FileId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return BitConverter.ToInt64(hash, 0) & long.MaxValue;
    }

    // Explorer uses directory attribute bits (not only FILE_ATTRIBUTE_DIRECTORY)
    // when deciding whether to process desktop.ini folder metadata.  Preserve the
    // physical ReadOnly/System/Hidden bits so customized folder icons and folder
    // pictures keep working over SMB.
    private static SMBLibrary.FileAttributes AttributesOf(string path, bool isDirectory)
    {
        try
        {
            var attributes = (uint)File.GetAttributes(path);
            const uint supported = 0x0002FFFF; // MS-FSCC FILE_ATTRIBUTE_* bits we expose
            attributes &= supported;
            if (isDirectory) attributes |= (uint)SMBLibrary.FileAttributes.Directory;
            return attributes == 0
                ? (isDirectory ? SMBLibrary.FileAttributes.Directory : SMBLibrary.FileAttributes.Normal)
                : (SMBLibrary.FileAttributes)attributes;
        }
        catch
        {
            return isDirectory ? SMBLibrary.FileAttributes.Directory : SMBLibrary.FileAttributes.Normal;
        }
    }

    private async Task<DavSession?> SessionAsync(SecurityContext? ctx)
    {
        var session = await access.BuildSessionForUsernameAsync(ctx?.UserName, DavProtocols.Smb);
        if (session is null)
            logger.LogWarning("SMB 操作被拒：账号不可用 user={User}", ctx?.UserName);
        return session;
    }

    /// <summary>按所需权限解析路径；失败返回 null 并记录原因。</summary>
    private async Task<(DavSession Session, DavSession.Location Loc)?> AuthorizeAsync(
        SecurityContext? ctx, string smbPath, DavPermission need, string action)
    {
        var session = await SessionAsync(ctx);
        if (session is null)
            return null;

        var loc = session.Resolve(VirtualOf(smbPath));
        if (loc is null)
        {
            logger.LogWarning("SMB {Action} 路径越界：user={User} path={Path}", action, ctx?.UserName, smbPath);
            return null;
        }

        if (!session.Allowed(loc, need))
        {
            logger.LogWarning("SMB {Action} 权限不足：user={User} path={Path} need={Need}",
                action, ctx?.UserName, loc.RulePath, need);
            return null;
        }

        return (session, loc);
    }

    // ---------- 打开 / 关闭 ----------

    public NTStatus CreateFile(out object handle, out FileStatus fileStatus, string path, AccessMask desiredAccess,
        SMBLibrary.FileAttributes fileAttributes, ShareAccess shareAccess, CreateDisposition createDisposition,
        CreateOptions createOptions, SecurityContext securityContext)
    {
        // 注意：out 参数不能进 lambda，故用「同步包装 + 异步核心」返回元组
        var r = CreateFileCoreAsync(path, desiredAccess, createDisposition, createOptions, securityContext)
            .GetAwaiter().GetResult();
        handle = r.Handle!;
        fileStatus = r.FileStatus;
        return r.Status;
    }

    private async Task<(NTStatus Status, object? Handle, FileStatus FileStatus)> CreateFileCoreAsync(
        string path, AccessMask desiredAccess, CreateDisposition createDisposition,
        CreateOptions createOptions, SecurityContext securityContext)
    {
        object? handle = null;
        var fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

        {
            var session = await SessionAsync(securityContext);
            if (session is null)
                return (NTStatus.STATUS_LOGON_FAILURE, handle, fileStatus);

            var virtualPath = VirtualOf(path);
            var loc = session.Resolve(virtualPath);
            if (loc is null)
            {
                logger.LogWarning("SMB CreateFile 路径越界：user={User} path={Path}", securityContext?.UserName, path);
                return (NTStatus.STATUS_ACCESS_DENIED, handle, fileStatus);
            }

            var dirRequested = (createOptions & CreateOptions.FILE_DIRECTORY_FILE) != 0;
            var nonDirRequested = (createOptions & CreateOptions.FILE_NON_DIRECTORY_FILE) != 0;
            var physicalIsDir = Directory.Exists(loc.PhysicalPath);
            var physicalIsFile = File.Exists(loc.PhysicalPath);
            var exists = physicalIsDir || physicalIsFile;

            // Do not let a directory probe turn a regular file handle into a
            // directory. Explorer/WinRAR probe archive paths with this bit;
            // SMBLibrary clients tolerate opening the existing file handle.
            if (nonDirRequested && physicalIsDir)
                return (NTStatus.STATUS_FILE_IS_A_DIRECTORY, handle, fileStatus);

            // 创建语义：CREATE / OPEN_IF / OVERWRITE_IF 都可能新建
            var mayCreate = createDisposition is CreateDisposition.FILE_CREATE
                or CreateDisposition.FILE_OPEN_IF
                or CreateDisposition.FILE_OVERWRITE_IF;
            var createOnly = createDisposition is CreateDisposition.FILE_CREATE;

            if (createOnly && exists)
                return (NTStatus.STATUS_OBJECT_NAME_COLLISION, handle, fileStatus);
            if (createDisposition == CreateDisposition.FILE_OPEN && !exists)
                return (NTStatus.STATUS_OBJECT_NAME_NOT_FOUND, handle, fileStatus);

            // 权限映射：读需要 R；写需要 U（存在）或 C（新建）；删除需要 D
            var need = DavPermission.None;
            // Windows 发的常是具体位（FILE_READ_DATA=0x1 / FILE_WRITE_DATA=0x2），
            // 枚举里只有 GENERIC_*，故按原始位判定；MAXIMUM_ALLOWED 视为读写全要。
            var mask = (uint)desiredAccess;
            var wantsRead = (mask & 0x1) != 0 || (mask & 0x80000000) != 0 || (mask & 0x02000000) != 0;
            var wantsWrite = (mask & 0x2) != 0 || (mask & 0x40000000) != 0 || (mask & 0x02000000) != 0;
            var wantsDelete = (desiredAccess & AccessMask.DELETE) != 0;

            if (wantsRead) need |= DavPermission.Read;
            if (wantsWrite) need |= exists ? DavPermission.Update : DavPermission.Create;
            if (!exists && mayCreate) need |= DavPermission.Create;
            if (wantsDelete) need |= DavPermission.Delete;

            if (need != DavPermission.None && !session.Allowed(loc, need))
            {
                logger.LogWarning("SMB CreateFile 权限不足：user={User} path={Path} need={Need}",
                    securityContext?.UserName, loc.RulePath, need);
                return (NTStatus.STATUS_ACCESS_DENIED, null, fileStatus);
            }

            var result = new Handle
            {
                Session = session,
                VirtualPath = virtualPath,
                RulePath = loc.RulePath,
                PhysicalPath = loc.PhysicalPath,
                Mount = loc.Mount,
                Granted = need,
                IsDirectory = physicalIsDir || (!exists && dirRequested),
            };

            if (result.IsDirectory)
            {
                if (!exists)
                {
                    try
                    {
                        Directory.CreateDirectory(loc.PhysicalPath);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "SMB 创建目录失败：{Path}", loc.PhysicalPath);
                        return (NTStatus.STATUS_ACCESS_DENIED, handle, fileStatus);
                    }
                    fileStatus = FileStatus.FILE_CREATED;
                }
                else
                {
                    fileStatus = FileStatus.FILE_OPENED;
                }
            }
            else
            {
                try
                {
                    var parent = Path.GetDirectoryName(loc.PhysicalPath);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);

                    var mode = exists && createDisposition == CreateDisposition.FILE_OVERWRITE
                        ? FileMode.Truncate
                        : exists ? FileMode.Open : FileMode.CreateNew;

                    if (exists && !wantsWrite)
                        mode = FileMode.Open; // 只读打开不应截断

                    var fileAccess = wantsWrite
                        ? (wantsRead ? FileAccess.ReadWrite : FileAccess.Write)
                        : FileAccess.Read;
                    // 只读方式打开时若文件不存在，仍需按 Create 语义建空文件
                    if (!exists && !wantsWrite)
                    {
                        using var _ = File.Create(loc.PhysicalPath);
                        mode = FileMode.Open;
                    }

                    result.Stream = new FileStream(loc.PhysicalPath, mode, fileAccess,
                        FileShare.ReadWrite | FileShare.Delete);
                    fileStatus = exists ? FileStatus.FILE_OPENED : FileStatus.FILE_CREATED;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SMB 打开文件失败：{Path}", loc.PhysicalPath);
                    return (NTStatus.STATUS_ACCESS_DENIED, handle, fileStatus);
                }
            }

            handle = result;
            return (NTStatus.STATUS_SUCCESS, handle, fileStatus);
    }
    }

    public NTStatus CloseFile(object handle)
    {
        if (handle is not Handle h)
            return NTStatus.STATUS_INVALID_HANDLE;

        h.Stream?.Dispose();
        h.Stream = null;

        if (h.DeleteOnClose)
        {
            h.DeleteOnClose = false;
            // 关闭时重新鉴权（会话期间规则可能被改），与 FTP RMD / WebDAV DELETE 同语义
            if (!h.Session.Allowed(h.RulePath, DavPermission.Delete))
            {
                logger.LogWarning("SMB 关闭时删除被拒（规则已变）：path={Path}", h.RulePath);
                return NTStatus.STATUS_ACCESS_DENIED;
            }
            try
            {
                if (h.IsDirectory || Directory.Exists(h.PhysicalPath))
                {
                    if (!Directory.Exists(h.PhysicalPath))
                        return NTStatus.STATUS_SUCCESS; // 已不在，视为删完
                    // 整树鉴权：任一后代缺 D 则整体拒绝（与 FTP/WebDAV 一致）
                    var comparison = h.Session.ComparisonFor(h.Mount?.PhysicalPath ?? h.PhysicalPath);
                    if (!DavTreeWalker.AllSatisfy(h.PhysicalPath, h.RulePath, h.Session.Defaults,
                            h.Session.Rules, DavPermission.Delete, comparison))
                    {
                        logger.LogWarning("SMB 目录删除被拒（含无权限子项）：path={Path}", h.RulePath);
                        return NTStatus.STATUS_ACCESS_DENIED;
                    }
                    Directory.Delete(h.PhysicalPath, true);
                }
                else
                {
                    if (!File.Exists(h.PhysicalPath))
                        return NTStatus.STATUS_SUCCESS;
                    File.Delete(h.PhysicalPath);
                }
                logger.LogInformation("SMB 已删除：{Path}", h.RulePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SMB 删除失败：{Path}", h.PhysicalPath);
                return NTStatus.STATUS_ACCESS_DENIED;
            }
        }
        return NTStatus.STATUS_SUCCESS;
    }

    // ---------- 读写 ----------

    public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
    {
        data = [];
        if (handle is not Handle { Stream: { } stream })
            return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[maxCount];
            var read = stream.Read(buffer, 0, maxCount);
            data = buffer[..read];
            return NTStatus.STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMB 读取失败：{Path}", h0(handle));
            return NTStatus.STATUS_ACCESS_DENIED;
        }
    }

    public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data)
    {
        numberOfBytesWritten = 0;
        if (handle is not Handle { Stream: { } stream })
            return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            stream.Write(data, 0, data.Length);
            stream.Flush();
            numberOfBytesWritten = data.Length;
            return NTStatus.STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMB 写入失败：{Path}", h0(handle));
            return NTStatus.STATUS_ACCESS_DENIED;
        }
    }

    public NTStatus FlushFileBuffers(object handle)
    {
        if (handle is Handle { Stream: { } stream })
        {
            try { stream.Flush(true); } catch { /* 忽略 */ }
        }
        return NTStatus.STATUS_SUCCESS;
    }

    private static string h0(object handle) => handle is Handle h ? h.PhysicalPath : "?";

    // ---------- 目录列举 ----------

    public NTStatus QueryDirectory(out List<QueryDirectoryFileInformation> result, object handle, string fileName,
        FileInformationClass informationClass)
    {
        result = [];
        if (handle is not Handle h || !h.IsDirectory)
            return NTStatus.STATUS_INVALID_HANDLE;

        IReadOnlyList<DavEntry> entries;
        try
        {
            // 复用会话的列表过滤：无 Read 权限与不可见项都不返回（与 WebDAV/FTP 一致）
            entries = h.Session.List(h.VirtualPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMB 列举目录失败：{Path}", h.PhysicalPath);
            return NTStatus.STATUS_ACCESS_DENIED;
        }

        // SMB clients use QueryDirectory both for enumeration ("*" / "*.*")
        // and for looking up one exact entry.  Returning the first entry for an
        // exact lookup makes WinRAR open that directory instead of the archive.
        var pattern = string.IsNullOrWhiteSpace(fileName) ? "*" : fileName;
        var matcher = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        foreach (var e in entries)
        {
            if (!Regex.IsMatch(e.Name, matcher, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;

            var info = BuildFileInfo(e, informationClass);
            if (info is not null)
                result.Add(info);
        }
        return result.Count == 0 ? NTStatus.STATUS_NO_SUCH_FILE : NTStatus.STATUS_SUCCESS;
    }

    private static QueryDirectoryFileInformation? BuildFileInfo(DavEntry e, FileInformationClass informationClass)
    {
        var lastWrite = ToDateTime(e.LastWriteTimeUtc) ?? Epoch;
        long size = e.IsDirectory ? 0 : e.Length;
        var attributes = AttributesOf(e.PhysicalPath, e.IsDirectory);

        return informationClass switch
        {
            FileInformationClass.FileDirectoryInformation => new FileDirectoryInformation
            {
                FileIndex = (uint)FileId(e.PhysicalPath),
                FileName = e.Name,
                EndOfFile = size,
                AllocationSize = size,
                CreationTime = lastWrite,
                LastAccessTime = lastWrite,
                LastWriteTime = lastWrite,
                ChangeTime = lastWrite,
                FileAttributes = attributes,
            },
            FileInformationClass.FileFullDirectoryInformation => new FileFullDirectoryInformation
            {
                FileIndex = (uint)FileId(e.PhysicalPath),
                FileName = e.Name,
                EndOfFile = size,
                AllocationSize = size,
                CreationTime = lastWrite,
                LastAccessTime = lastWrite,
                LastWriteTime = lastWrite,
                ChangeTime = lastWrite,
                FileAttributes = attributes,
            },
            FileInformationClass.FileBothDirectoryInformation => new FileBothDirectoryInformation
            {
                FileIndex = (uint)FileId(e.PhysicalPath),
                FileName = e.Name,
                EndOfFile = size,
                AllocationSize = size,
                CreationTime = lastWrite,
                LastAccessTime = lastWrite,
                LastWriteTime = lastWrite,
                ChangeTime = lastWrite,
                FileAttributes = attributes,
            },
            FileInformationClass.FileIdBothDirectoryInformation => new FileIdBothDirectoryInformation
            {
                FileIndex = (uint)FileId(e.PhysicalPath),
                FileId = (ulong)FileId(e.PhysicalPath),
                FileName = e.Name,
                EndOfFile = size,
                AllocationSize = size,
                CreationTime = lastWrite,
                LastAccessTime = lastWrite,
                LastWriteTime = lastWrite,
                ChangeTime = lastWrite,
                FileAttributes = attributes,
            },
            FileInformationClass.FileIdFullDirectoryInformation => new FileIdFullDirectoryInformation
            {
                FileIndex = (uint)FileId(e.PhysicalPath),
                FileId = (ulong)FileId(e.PhysicalPath),
                FileName = e.Name,
                EndOfFile = size,
                AllocationSize = size,
                CreationTime = lastWrite,
                LastAccessTime = lastWrite,
                LastWriteTime = lastWrite,
                ChangeTime = lastWrite,
                FileAttributes = attributes,
            },
            FileInformationClass.FileNamesInformation => new FileNamesInformation
            {
                FileIndex = (uint)FileId(e.PhysicalPath),
                FileName = e.Name,
            },
            _ => null,
        };
    }

    // ---------- 信息查询 ----------

    public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass)
    {
        result = null!;
        if (handle is not Handle h)
            return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            var isDir = h.IsDirectory;
            var length = isDir ? 0L : new FileInfo(h.PhysicalPath).Length;
            var lastWrite = isDir
                ? ToDateTime(Directory.GetLastWriteTimeUtc(h.PhysicalPath)) ?? Epoch
                : ToDateTime(File.GetLastWriteTimeUtc(h.PhysicalPath)) ?? Epoch;
            SMBLibrary.FileAttributes attrs = isDir ? SMBLibrary.FileAttributes.Directory : SMBLibrary.FileAttributes.Normal;

            result = informationClass switch
            {
                FileInformationClass.FileInternalInformation => new FileInternalInformation
                {
                    IndexNumber = FileId(h.PhysicalPath),
                },
                FileInformationClass.FileBasicInformation => new FileBasicInformation
                {
                    CreationTime = lastWrite,
                LastAccessTime = lastWrite,
                LastWriteTime = lastWrite,
                ChangeTime = lastWrite,
                    FileAttributes = attrs,
                },
                FileInformationClass.FileStandardInformation => new FileStandardInformation
                {
                    EndOfFile = length,
                    AllocationSize = length,
                    Directory = isDir,
                    NumberOfLinks = 1,
                },
                FileInformationClass.FileAllInformation => new FileAllInformation
                {
                    InternalInformation = new FileInternalInformation { IndexNumber = FileId(h.PhysicalPath) },
                    NameInformation = new FileNameInformation { FileName = Path.GetFileName(h.PhysicalPath) },
                    StandardInformation = new FileStandardInformation
                    {
                        EndOfFile = length, AllocationSize = length, Directory = isDir, NumberOfLinks = 1,
                    },
                    BasicInformation = new FileBasicInformation { LastWriteTime = lastWrite, FileAttributes = attrs },
                },
                FileInformationClass.FileNetworkOpenInformation => new FileNetworkOpenInformation
                {
                    EndOfFile = length, AllocationSize = length, LastWriteTime = lastWrite, FileAttributes = attrs,
                },
                FileInformationClass.FileNameInformation => new FileNameInformation
                {
                    FileName = Path.GetFileName(h.PhysicalPath),
                },
                _ => null!,
            };
            return result is null ? NTStatus.STATUS_INVALID_INFO_CLASS : NTStatus.STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMB 查询文件信息失败：{Path}", h.PhysicalPath);
            return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
        }
    }

    public NTStatus SetFileInformation(object handle, FileInformation information)
    {
        if (handle is not Handle h)
            return NTStatus.STATUS_INVALID_HANDLE;

        logger.LogInformation("SMB SetFileInformation：path={Path} type={Type}",
            h.RulePath, information.GetType().Name);
        try
        {
            switch (information)
            {
                case FileDispositionInformation disposition:
                    if (!h.Session.Allowed(h.RulePath, DavPermission.Delete))
                    {
                        logger.LogWarning("SMB 删除被拒：path={Path}", h.RulePath);
                        return NTStatus.STATUS_ACCESS_DENIED;
                    }
                    // MT 管理器发起删除后会立刻检查目录，不能只等 CloseFile 才删除；
                    // Windows 允许在 FileShare.Delete 下删除已打开文件。先立即执行，
                    // CloseFile 再次检查不存在时会幂等成功。
                    h.DeleteOnClose = disposition.DeletePending;
                    if (!h.DeleteOnClose)
                        return NTStatus.STATUS_SUCCESS;
                    h.Stream?.Dispose();
                    h.Stream = null;
                    if (h.IsDirectory || Directory.Exists(h.PhysicalPath))
                    {
                        if (!Directory.Exists(h.PhysicalPath))
                            return NTStatus.STATUS_SUCCESS;
                        var comparison = h.Session.ComparisonFor(h.Mount?.PhysicalPath ?? h.PhysicalPath);
                        if (!DavTreeWalker.AllSatisfy(h.PhysicalPath, h.RulePath, h.Session.Defaults,
                                h.Session.Rules, DavPermission.Delete, comparison))
                            return NTStatus.STATUS_ACCESS_DENIED;
                        Directory.Delete(h.PhysicalPath, true);
                    }
                    else if (File.Exists(h.PhysicalPath))
                    {
                        File.Delete(h.PhysicalPath);
                    }
                    h.DeleteOnClose = false;
                    logger.LogInformation("SMB 已删除：path={Path}", h.RulePath);
                    return NTStatus.STATUS_SUCCESS;

                case FileEndOfFileInformation endOfFile when h.Stream is { } stream:
                    // Android/MT 常在写完后显式提交 EOF；不支持该请求会让客户端把
                    // 整个上传标记为失败，即使前面的 Write 已经成功。
                    stream.SetLength(endOfFile.EndOfFile);
                    return NTStatus.STATUS_SUCCESS;

                case FileAllocationInformation:
                    // Windows/Android 客户端的预分配提示不是必须操作；实际长度由
                    // Write/EndOfFileInformation 决定。
                    return NTStatus.STATUS_SUCCESS;

                case FileBasicInformation:
                    // 时间/属性是可选元数据，底层文件时间由写入自然维护；返回成功，
                    // 避免 MT 将元数据请求误判为上传失败。
                    return NTStatus.STATUS_SUCCESS;

                case FileRenameInformationType1 rename:
                {
                    return RenameFile(h, rename.FileName, rename.ReplaceIfExists);
                }

                case FileRenameInformationType2 rename:
                {
                    return RenameFile(h, rename.FileName, rename.ReplaceIfExists);
                }

                default:
                    logger.LogWarning("SMB 不支持 SetFileInformation 类型：path={Path} type={Type}",
                        h.RulePath, information.GetType().Name);
                    return NTStatus.STATUS_NOT_SUPPORTED;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMB 设置文件信息失败：{Path}", h.PhysicalPath);
            return NTStatus.STATUS_ACCESS_DENIED;
        }
    }

    private NTStatus RenameFile(Handle h, string fileName, bool replaceIfExists)
    {
        var destVirtual = VirtualOf(fileName);
        var destLoc = h.Session.Resolve(destVirtual);
        if (destLoc is null)
            return NTStatus.STATUS_ACCESS_DENIED;

        if (!h.Session.Allowed(h.RulePath, DavPermission.Delete))
        {
            logger.LogWarning("SMB 重命名源无删除权限：{Path}", h.RulePath);
            return NTStatus.STATUS_ACCESS_DENIED;
        }

        var destExists = File.Exists(destLoc.PhysicalPath) || Directory.Exists(destLoc.PhysicalPath);
        if (!h.Session.Allowed(destLoc, destExists ? DavPermission.Update : DavPermission.Create))
        {
            logger.LogWarning("SMB 重命名目标无权限：{Path}", destLoc.RulePath);
            return NTStatus.STATUS_ACCESS_DENIED;
        }

        h.Stream?.Dispose();
        h.Stream = null;
        if (h.IsDirectory)
            Directory.Move(h.PhysicalPath, destLoc.PhysicalPath);
        else
            File.Move(h.PhysicalPath, destLoc.PhysicalPath, overwrite: replaceIfExists && destExists);
        return NTStatus.STATUS_SUCCESS;
    }

    public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass)
    {
        result = null!;
        try
        {
            var root = Path.GetPathRoot(mount.PhysicalPath) ?? mount.PhysicalPath;
            var drive = new DriveInfo(root);
            result = informationClass switch
            {
                FileSystemInformationClass.FileFsVolumeInformation => new FileFsVolumeInformation
                {
                    VolumeLabel = "WebdavSharp",
                    VolumeSerialNumber = 0x12345678,
                },
                FileSystemInformationClass.FileFsSizeInformation => new FileFsSizeInformation
                {
                    TotalAllocationUnits = drive.IsReady ? drive.TotalSize / 1_048_576 : 0,
                    AvailableAllocationUnits = drive.IsReady ? drive.AvailableFreeSpace / 1_048_576 : 0,
                    SectorsPerAllocationUnit = 1,
                    BytesPerSector = 1_048_576,
                },
                FileSystemInformationClass.FileFsAttributeInformation => new FileFsAttributeInformation
                {
                    FileSystemName = "NTFS",
                    FileSystemAttributes = FileSystemAttributes.CaseSensitiveSearch
                        | FileSystemAttributes.UnicodeOnDisk,
                    MaximumComponentNameLength = 255,
                },
                _ => null!,
            };
            return result is null ? NTStatus.STATUS_INVALID_INFO_CLASS : NTStatus.STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMB 查询卷信息失败：{Path}", mount.PhysicalPath);
            return NTStatus.STATUS_NOT_SUPPORTED;
        }
    }

    // ---------- 明确不支持（返回标准 NTStatus，客户端会优雅降级） ----------

    public NTStatus SetFileSystemInformation(FileSystemInformation information) => NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle, SecurityInformation securityInformation)
    {
        result = null!;
        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    public NTStatus SetSecurityInformation(object handle, SecurityInformation securityInformation,
        SecurityDescriptor securityDescriptor) => NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock)
    {
        if (handle is not Handle { Stream: { } stream } h)
            return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            // MT 管理器上传前会请求独占范围锁。FileStream 的 OS 锁足以覆盖
            // 本机并发访问；SMBLibrary 负责把失败转换为标准 SMB 状态。
            stream.Lock(byteOffset, length);
            return NTStatus.STATUS_SUCCESS;
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "SMB 文件锁未获得：{Path} offset={Offset} length={Length}",
                h.RulePath, byteOffset, length);
            return NTStatus.STATUS_LOCK_NOT_GRANTED;
        }
        catch (PlatformNotSupportedException)
        {
            // 某些文件系统不提供 OS 区间锁；不应因此阻止普通上传。
            return NTStatus.STATUS_SUCCESS;
        }
    }

    public NTStatus UnlockFile(object handle, long byteOffset, long length)
    {
        if (handle is not Handle { Stream: { } stream } h)
            return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            stream.Unlock(byteOffset, length);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "SMB 文件解锁失败：{Path} offset={Offset} length={Length}",
                h.RulePath, byteOffset, length);
        }
        catch (PlatformNotSupportedException)
        {
            // 无 OS 锁的平台没有需要释放的锁。
        }
        return NTStatus.STATUS_SUCCESS;
    }

    public NTStatus NotifyChange(out object ioRequest, object handle, NotifyChangeFilter completionFilter,
        bool watchTree, int outputBufferSize, OnNotifyChangeCompleted onNotifyChangeCompleted, object context)
    {
        ioRequest = null!;
        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    public NTStatus Cancel(object ioRequest) => NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength)
    {
        output = [];
        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    /// <summary>把 async 逻辑包进同步接口（SMBLibrary 的 INTFileStore 是同步签名）。</summary>
    private static NTStatus Run(Func<Task<NTStatus>> body) => body().GetAwaiter().GetResult();
}
