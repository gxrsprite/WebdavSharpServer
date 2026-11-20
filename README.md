# WebdavSharpServer

基于 .NET 10 的跨平台文件服务，支持 WebDAV、FTP 与 SMB，共用统一的用户、挂载与权限规则，并提供轻量 Blazor 管理后台。默认采用纯 C# 实现、无原生库依赖的 NovaDb，也可通过 FreeSql 接入其他数据库。

## 功能

- **三协议服务**：WebDAV、FTP、SMB（SMB1/2/3）；协议可分别启用，权限规则可限定适用协议。
- **虚拟挂载**：多个物理目录映射为虚拟路径，如 `/NAS`、`/public`；支持用户主目录。
- **权限控制**：`C` 创建 / `R` 读取 / `U` 更新 / `D` 删除 / `V` 显示（列出目录和挂载项）；`none` 显式拒绝。规则按优先级排序，最后一条命中的规则生效。
- **管理后台**：用户、角色、挂载、权限规则、服务开关与审计日志。
- **认证与审计**：PBKDF2 口令存储；SMB 使用 NTLMv2；登录失败限制、写操作审计与健康探针。
- **数据库**：默认使用 NovaDb，也可通过 FreeSql 接入其他数据库。
- **跨平台**：默认数据库为纯 C# 实现，无原生库依赖；可跨平台部署。

## 快速开始

环境要求：**.NET 10 SDK**。

```powershell
dotnet build WebdavSharp.slnx
dotnet run --project src/WebdavSharp.Server
```

- WebDAV：`http://localhost:5210/dav/`（Basic：`admin / admin`）
- 管理后台：`http://localhost:5210/admin`（Cookie：`admin / admin`）
- FTP：默认关闭；启用后默认监听 `localhost:2121`
- SMB：由管理后台服务设置控制；开发默认端口 `5000`

首次启动会在 `LocalPaths/novadb/` 初始化 NovaDb 数据目录并创建必要表结构。生产环境配置见 [`docs/admin-guide.md`](docs/admin-guide.md)。

## 权限模型

- `C` 创建、`R` 读取、`U` 更新、`D` 删除、`V` 显示；`none` 显式拒绝。
- 规则按 `SortOrder` 从小到大应用，最后一条命中的规则生效。
- `path` 使用前缀匹配，`regex` 对完整虚拟路径匹配。
- 详见 [`docs/permission-model.md`](docs/permission-model.md)。

## 客户端兼容性（实测）

| 客户端 | 协议与验证结果 |
|---|---|
| Windows 资源管理器 / MiniRedir | WebDAV、SMB、FTP 连接及基本文件操作通过；WebDAV 映射需启用 `BasicAuthLevel=2` |
| CX 文件管理器（Android） | SMB1：认证、共享枚举、列目录、读写、改名、删除通过 |
| MT 管理器（Android） | SMB、WebDAV、FTP 连接及列目录、上传、下载通过 |
| ES 文件管理器（Android） | SMB、WebDAV、FTP 连接及列目录、上传、下载通过 |

更多实测环境、协议操作矩阵、客户端注意事项与待测列表见 [`docs/client-compat.md`](docs/client-compat.md)。

## 文档

- 管理后台与生产部署：[`docs/admin-guide.md`](docs/admin-guide.md)
- 权限模型：[`docs/permission-model.md`](docs/permission-model.md)
- 跨平台部署：[`docs/cross-platform.md`](docs/cross-platform.md)
- 协议共用模型：[`docs/protocols.md`](docs/protocols.md)

## SMBLibrary 补丁

SMB 服务基于 LGPL-3.0 的 SMBLibrary。为兼容 CX 文件管理器的 SMB1 RAP 共享枚举和空目录列举，本仓库在 `third_party/SMBLibrary` 中 vendored 上游 v1.5.7 并维护补丁，涉及 `NetShareEnum`、空目录 `FindFirst2` 返回语义和 NTLM 时间戳 AV Pair。补丁细节与上游同步说明见 [`third_party/SMBLibrary/README.webdavsharp.md`](third_party/SMBLibrary/README.webdavsharp.md)；分发时保留其许可证文件并遵循 LGPL-3.0。

## 构建、测试与发布

```powershell
dotnet build WebdavSharp.slnx --nologo
dotnet test tests/WebdavSharp.Tests --nologo
pwsh -File scripts/publish-webdavsharp.ps1 -Runtime win-x64
```

发布脚本的 `-Runtime` 可指定目标平台 RID；使用 `-SelfContained` 可发布自包含版本。NovaDb 默认实现不依赖原生数据库库。

## 许可证

- 自研代码：Apache-2.0，见 [`LICENSE`](LICENSE) 与 [`NOTICE`](NOTICE)。
- `third_party/SMBLibrary`：LGPL-3.0，见其 `License.txt` 和 [`README.webdavsharp.md`](third_party/SMBLibrary/README.webdavsharp.md)。
