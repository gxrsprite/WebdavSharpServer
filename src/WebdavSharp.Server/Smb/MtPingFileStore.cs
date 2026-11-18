using Microsoft.Extensions.Logging;
using SMBLibrary;

namespace WebdavSharp.Server.Smb;

/// <summary>
/// MT 管理器（Android）的连通性探测共享 <c>MT_PING$</c> 的文件存储：
/// MT 会周期性 Tree Connect 这个固定名字的伪共享来探测服务器是否可达，
/// 探测失败会让它退避约 60 秒（表现为下载卡在 99% 很久才完成）。
/// Samba 社区的通用做法就是提供一个同名共享。这里只放行树连接，
/// 一切文件操作都拒绝——该共享不暴露任何内容，也不出现在 srvsvc 枚举里。
/// </summary>
public sealed class MtPingFileStore(ILogger logger) : INTFileStore
{
    public NTStatus CreateFile(out object handle, out FileStatus fileStatus, string path, AccessMask desiredAccess,
        SMBLibrary.FileAttributes fileAttributes, ShareAccess shareAccess, CreateDisposition createDisposition,
        CreateOptions createOptions, SecurityContext securityContext)
    {
        handle = null!;
        fileStatus = FileStatus.FILE_DOES_NOT_EXIST;
        return NTStatus.STATUS_ACCESS_DENIED;
    }

    public NTStatus CloseFile(object handle) => NTStatus.STATUS_SUCCESS;

    public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
    {
        data = [];
        return NTStatus.STATUS_ACCESS_DENIED;
    }

    public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data)
    {
        numberOfBytesWritten = 0;
        return NTStatus.STATUS_ACCESS_DENIED;
    }

    public NTStatus FlushFileBuffers(object handle) => NTStatus.STATUS_ACCESS_DENIED;

    public NTStatus QueryDirectory(out List<QueryDirectoryFileInformation> result, object handle, string fileName,
        FileInformationClass informationClass)
    {
        result = [];
        return NTStatus.STATUS_ACCESS_DENIED;
    }

    public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass)
    {
        result = null!;
        return NTStatus.STATUS_ACCESS_DENIED;
    }

    public NTStatus SetFileInformation(object handle, FileInformation information) => NTStatus.STATUS_ACCESS_DENIED;

    public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass)
    {
        result = null!;
        return NTStatus.STATUS_INVALID_INFO_CLASS;
    }

    public NTStatus SetFileSystemInformation(FileSystemInformation information) => NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle, SecurityInformation securityInformation)
    {
        result = null!;
        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    public NTStatus SetSecurityInformation(object handle, SecurityInformation securityInformation,
        SecurityDescriptor securityDescriptor) => NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock) => NTStatus.STATUS_ACCESS_DENIED;

    public NTStatus UnlockFile(object handle, long byteOffset, long length) => NTStatus.STATUS_SUCCESS;

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
}
