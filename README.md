# WebdavSharpServer

基于 **.NET 10** 的跨平台文件服务：**WebDAV + FTP + SMB** 共用统一的用户、虚拟挂载与权限规则，并提供轻量 Blazor Server 管理后台。

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

## 客户端与说明

- Windows 资源管理器、Android 文件管理器等 WebDAV / FTP / SMB 客户端说明：[`docs/client-compat.md`](docs/client-compat.md)
- 管理后台与生产部署：[`docs/admin-guide.md`](docs/admin-guide.md)
- 跨平台部署：[`docs/cross-platform.md`](docs/cross-platform.md)
- 协议共用模型：[`docs/protocols.md`](docs/protocols.md)

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
