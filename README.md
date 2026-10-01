# WebdavSharpServer

基于 .NET 10 的跨平台文件服务：**WebDAV + FTP + SMB** 三协议共用同一套用户 / 挂载 / 规则，自带轻量 **Blazor Server 管理后台**。

> 权限语义参考 [hacdias/webdav](https://github.com/hacdias/webdav)（`permissions: CRUD`、按用户覆盖、`path` 前缀 / `regex` 规则"最后一条命中生效"、多虚拟挂载 `directories`）。

## 功能一览

- **三协议一体**：WebDAV ✅ / FTP ✅ / SMB ✅（SMB1/2/3），共用 `dav_user`、`dav_mount`、`dav_rule` 三张表，规则可用 `Protocols` 列限定生效协议（空 = 全部协议）。
- **虚拟挂载**：多个物理目录挂为虚拟根条目（如 `/NAS`、`/public`），也支持按用户设置主目录（与全局挂载互斥、优先）。
- **权限模型**：`C` 创建 / `R` 读取 / `U` 更新 / `D` 删除，`none` 显式拒绝；适用规则按 `SortOrder` 从小到大、**最后一条命中生效**；`path` 前缀匹配，`regex` 全路径匹配；跨挂载 COPY/MOVE/DELETE 要求所有触及点都有权限。
- **管理后台**：手写轻量 Blazor Server（无 BootstrapBlazor 依赖），`/admin`：用户 / 角色 / 挂载 / 规则 / 服务开关 / 审计日志。
- **安全**：PBKDF2-SHA256 存口令（SMB 另存 NT Hash 用于 NTLMv2）；改密/禁用递增 `AuthVersion` 使 Cookie 即时失效；暴力破解防护（同 IP+用户 5 分钟 5 次失败封 15 分钟，`429` + `Retry-After`）；审计日志（写操作全记、读操作只记失败）；健康探针 `/healthz`、`/readyz`。
- **跨平台**：Windows / Linux / macOS，x64 + arm64；SQLite 用微软官方 `FreeSql.Provider.SqliteCore` + `SQLitePCLRaw.bundle_e_sqlite3`（树莓派 / Apple Silicon 可用）。

## 仓库结构

```text
WebdavSharpServer/
├── WebdavSharp.slnx                 # solution（Core / Server / Tests）
├── Directory.Build.props             # 全仓统一 net10 / Nullable / ImplicitUsings
├── src/
│   ├── WebdavSharp.Core/             # 纯类库：实体 / 权限求值 / 路径安全 / 认证
│   │   ├── Entities/                 #   DavUser / DavRole / DavMount / DavRule / DavAuditLog …
│   │   └── Services/                 #   DavAccessService / DavSession / DavPermissionEvaluator …
│   └── WebdavSharp.Server/           # Web 服务：WebDAV 中间件 + FTP + SMB + Blazor 后台
│       ├── WebDav/                   #   DavMiddleware（HTTP 动词分发 + 权限卡点）
│       ├── Ftp/                      #   FTP 适配（认证 + 逐操作鉴权）
│       ├── Smb/                      #   SMB 适配（NTLMv2 + 逐路径权限卡点）
│       ├── Components/               #   Blazor 管理页（Login/Users/Mounts/Rules/Services）
│       ├── appsettings.json          #   默认配置（无密钥）
│       └── SeedData.cs               #   首次运行种子：admin 角色/用户/示例挂载
├── tests/WebdavSharp.Tests/          # xUnit（权限求值 / 路径穿越 / RAP 编解码 …）
├── docs/                             # admin-guide / permission-model / protocols / …
├── scripts/
│   └── publish-webdavsharp.ps1       # 发布脚本
└── third_party/SMBLibrary/           # vendored 上游 v1.5.7 + WebdavSharp 补丁（LGPL-3.0）
```

## 快速开始

环境要求：**.NET 10 SDK**（`dotnet --version` ≥ 10）。

```powershell
dotnet build WebdavSharp.slnx
dotnet run --project src/WebdavSharp.Server
```

- WebDAV：`http://localhost:5210/dav/`（Basic：`admin / admin`）
- 管理后台：`http://localhost:5210/admin`（Cookie 登录：`admin / admin`）
- FTP：默认关闭，`Dav:FtpEnabled=true` 后 `localhost:2121`
- SMB：由 `dav_service` 表控制，开发默认 `0.0.0.0:5000`

首次运行自动创建 `LocalPaths/webdavsharp.db` 并种子化（开发环境 `AutoSyncStructure=true` 自动建表）。

## 配置

`src/WebdavSharp.Server/appsettings.json`（`Dav` 段，首次播种默认值，之后以数据库 `dav_service` 表为准）：

| 项 | 默认 | 说明 |
|---|---|---|
| `Dav:Address` / `Dav:Port` / `Dav:Prefix` | `0.0.0.0` / `5210` / `/dav/` | WebDAV 监听与挂载前缀（Kestrel） |
| `Dav:Directory` | `LocalPaths/dav-root` | 全局文件根（相对路径锚到 ContentRoot） |
| `Dav:Database` / `ConnectionString` | `Sqlite` / `Data Source=LocalPaths/webdavsharp.db` | 切 PostgreSQL 只改这两行（需另引 `FreeSql.Provider.PostgreSQL`） |
| `Dav:FtpEnabled` / `Dav:FtpPort` | `false` / `2121` | FTP 开关与端口 |
| `Dav:Seed:AdminUsername/Password` | `admin / admin` | 首次种子；口令支持 `{env}VAR` 占位（变量缺失则启动失败，绝不静默设空） |
| `Dav:NoPassword` | `false` | `true` 时 DAV Basic 只认用户名（委托上游可信代理验口令；管理后台始终验口令） |
| `Dav:MaxFailedLogins` 等 | `5 / 5min / 15min` | 暴力破解防护阈值 |
| `Dav:AuditRetainDays` | `90` | 审计保留天数（0 关闭） |

生产模板见 `src/WebdavSharp.Server/appsettings.Production.example.json`，复制为 `appsettings.Production.json`（或环境变量覆盖，如 `Dav__Port=6065`），关闭 `AutoSyncStructure`，详见 [`docs/admin-guide.md`](docs/admin-guide.md)。

服务开关页：`/admin/services`（FTP/SMB 改地址端口后点"重启"；WebDAV 开关即时生效，地址端口改后需重启应用）。

## 权限模型速览

详见 [`docs/permission-model.md`](docs/permission-model.md)。

- 适用规则（全局 / 角色 / 用户）统一按 `SortOrder` 从小到大，**最后一条命中生效**。
- `path` 前缀匹配（尾斜杠同时覆盖集合自身）；`regex` 字面全路径匹配。
- 用户主目录设置后，整个虚拟空间映射到该目录（与全局挂载互斥）。

## 客户端对接

- Windows 资源管理器 → 映射网络驱动器填 `http://host:5210/dav/`（Basic）。
- rclone / Cyberduck / WinSCP：WebDAV + Basic。
- 手机 CX 文件管理器：SMB，实测走 SMB1（含 RAP `NetShareEnum` 根枚举与空目录兼容补丁，见下）。
- 已知限制：集合 COPY/MOVE 501、LOCK 为兼容性假锁（Office 可保存，无真排他）。

## 构建 / 测试 / 发布

```powershell
dotnet build WebdavSharp.slnx --nologo
dotnet test tests/WebdavSharp.Tests --nologo
powershell -ExecutionPolicy Bypass -File scripts/publish-webdavsharp.ps1
# → 产物 artifacts/publish/WebdavSharp（不入库）
# 自包含指定 RID：scripts/publish-webdavsharp.ps1 -Runtime linux-arm64
```

跨平台发布示例：`dotnet publish -c Release -r linux-arm64 --self-contained false`（已验证 linux-x64 / linux-arm64 / osx-arm64），详见 [`docs/cross-platform.md`](docs/cross-platform.md)。

## SMB 说明（含 vendored 补丁）

SMB 服务端基于 [SMBLibrary](https://github.com/TalAloni/SMBLibrary)（LGPL-3.0），每个虚拟挂载 = 一个 SMB 共享。因 CX 文件管理器（JCIFS 系）根枚举走 SMB1 RAP（`TRANS \PIPE\LANMAN` + `NetShareEnum`，上游未实现），本仓库在 `third_party/SMBLibrary` 中 vendored 上游 **v1.5.7** 并打补丁：

1. 新增 `RapHelper.cs`：最小 [MS-RAP] `NetShareEnum`（level 0/1）编解码；
2. `TransactionHelper.cs`：`\PIPE\LANMAN` 接 `RapHelper`，未知 RAP opcode 回 `NOT_SUPPORTED`；
3. `Transaction2SubcommandHelper.cs`：空目录 `FindFirst2` 回 `SUCCESS + 0 条目`（原 `NO_SUCH_FILE` 被 CX 当致命错误）；
4. `AVPairKey.Timestamp`：`0x0006` → `0x0007`（对齐 MS-NLMP）。

完整差异与跟随上游方式见 [`third_party/SMBLibrary/README.webdavsharp.md`](third_party/SMBLibrary/README.webdavsharp.md)。**闭源分发需保留可替换该库的能力，并遵守 LGPL-3.0 义务**（保留该目录与 `License.txt`）。

## 文档

- [`docs/admin-guide.md`](docs/admin-guide.md) — 管理后台与生产运行指南
- [`docs/permission-model.md`](docs/permission-model.md) — 权限模型
- [`docs/protocols.md`](docs/protocols.md) — 三协议共用模型
- [`docs/client-compat.md`](docs/client-compat.md) — 客户端兼容
- [`docs/cross-platform.md`](docs/cross-platform.md) — 跨平台说明
- [`docs/handoff.md`](docs/handoff.md) — 项目交接（含 CX 联调结论）

## 许可证

- 自研代码（`src/`、`tests/`、`docs/`、`scripts/` 及仓库根配置文件）：**Apache-2.0**（见 [`LICENSE`](LICENSE)，归属说明见 [`NOTICE`](NOTICE)）。
  ```
  Copyright 2026 WebdavSharpServer contributors
  ```
- `third_party/SMBLibrary`：**LGPL-3.0**（见该目录 `License.txt`），修改与分发须遵守其条款；**闭源分发需保留可替换该库的能力**。
