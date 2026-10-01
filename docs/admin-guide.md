# 管理后台与运行指南

## 1. 访问入口

| 入口 | 地址（默认） | 认证 |
|---|---|---|
| WebDAV | `http://127.0.0.1:5210/dav/` | HTTP Basic（与管理后台同一账号） |
| 管理后台 | `http://127.0.0.1:5210/admin` | Cookie（`DavAdmin_Auth`，15 天） |

首次运行种子账号：`admin / admin`（`Dav:Seed` 可改，口令支持 `{env}VAR` 占位——
变量缺失/为空则启动失败，绝不静默设空口令）。生产务必改密：
`appsettings.local.json`（不入库）覆盖，或直接在管理后台重置。
顶层配置另支持 ASP.NET Core 环境覆盖（等价于参考的 `WD_` 前缀），
如 `Dav__Port=6065`、`Dav__NoPassword=true`。

## 1.6 安全：暴力破解防护与探针

- 同一（客户端 IP + 用户名）5 分钟内失败满 5 次即封禁 15 分钟（`Dav:MaxFailedLogins` /
  `FailedLoginWindowMinutes` / `LoginBanMinutes` 可调），命中返回 `429` + `Retry-After`；
  成功登录清零。失败与封禁都进审计日志（`401`/`429` 行）。
- 真实客户端 IP：`Dav:BehindProxy=true` 时取 `X-Forwarded-For` 首个，
  否则取连接远端；每次 DAV 请求打一条访问日志（动词/路径/用户/IP/状态/耗时）。
- 健康探针（免认证）：`/healthz` 存活，`/readyz` 就绪（含 DB 可读检查，
  失败 `503`）。

## 1.5 委托认证（NoPassword）

`Dav:NoPassword=true` 时 DAV Basic 只认用户名、不验口令（对齐 hacdias `noPassword`，
口令校验交给上游可信代理；启动会打 WARNING 日志）。约束：

- 服务必须只监听可信来源（`127.0.0.1` / 内网代理后）；
- 管理后台 Cookie 登录**始终验口令**，不受该开关影响；
- 禁用/不存在用户依然拒绝。

## 2. 本地开发数据（不入库）

- `LocalPaths/dav-root/` — 真实文件根（含示例 `public/`）。
- `LocalPaths/webdavsharp.db` — SQLite 库（根 `.gitignore` 的 `*.db` 已排除）。
- 参考项目本地克隆放 `artifacts/reference/`（如 hacdias/webdav 对照），不入库。

## 1.7 日志（Serilog）

日志走 **Serilog**，配置在 `appsettings.json` 的 `Serilog` 段：

| 输出 | 位置 | 说明 |
|---|---|---|
| 控制台 | stdout | 模板来自配置；配置里无 sink 时代码兜底 |
| 滚动文件 | `LocalPaths/logs/webdavsharp-YYYYMMDD.log` | 按天滚动，保留 14 天，`shared: true` |

要点：

- **文件 sink 一律在代码里注册**，路径锚到 `ContentRoot`。若把相对路径写进配置，
  Serilog 会跟随进程 CWD——换个目录启动就写到别处（与 Dav 路径锚定策略保持一致）。
- 开发环境（`appsettings.Development.json`）把 `MinimumLevel` 降到 `Debug`，
  可看到 FTP 库的命令级日志（`Received [1]: CWD /public/open`）与我们自己的权限拒绝告警。
  觉得吵就在 `Serilog:MinimumLevel:Override` 里给对应命名空间提级。
- 启动期日志（含 `Dav roots:` 那行）也已走 Serilog；退出时 `Log.CloseAndFlushAsync()`
  保证缓冲刷盘，崩溃时不丢最后几条。
- 日志目录在 `LocalPaths/` 下，**已被 gitignore，不会入库**。

## 2.6 审计日志
管理后台“审计日志”页：变更操作（PUT/MKCOL/DELETE/COPY/MOVE/LOCK/UNLOCK/PROPPATCH）
全量记录；读操作只记失败（401/403/409/412/423/5xx），正常轮询不落库。
支持用户名/动词过滤与按保留天数清理。UTC 时间，倒序 200 条。
后台任务 `DavAuditCleanupService` 按 `Dav:AuditRetainDays`（默认 90，
0 关闭）自动清理，间隔 `Dav:AuditCleanupIntervalHours`（默认 24h，
首次启动 1 分钟后跑一次）。

## 2.5 用户主目录

用户设置主目录（用户页“主目录”列，对齐 hacdias per-user `directory`）后，
其整个虚拟空间直接映射到该目录，不再做挂载名拆分（与全局 `directories` 互斥、
优先）；规则仍按虚拟路径匹配，锁命名空间随物理路径天然隔离。
相对路径锚到全局根（`Dav:Directory`）。留空恢复全局挂载。

## 3. 生产配置

1. 复制 `src/WebdavSharp.Server/appsettings.Production.example.json`
   为生产 `appsettings.Production.json`（或环境变量覆盖）；
2. 关闭 `AutoSyncStructure`（发版前先在测试库同步表结构）；
3. 切库只改两行：`Dav:Database=PostgreSQL` + `Dav:ConnectionString`
   （需另引 `FreeSql.Provider.PostgreSQL`，与 WebBlazor 同版本）；
4. 发布：`powershell -ExecutionPolicy Bypass -File scripts/publish-webdavsharp.ps1`
   （需 PowerShell 7，`pwsh` 启动）→ 产物 `artifacts/publish/WebdavSharp`（不入库）。
   已演练：Release 发布物 + `appsettings.Production.json`（端口 5220、
   `{env}` 种子口令、`AutoSyncStructure=false` 新库）独立运行——
   环境种子登录 200、默认口令 401、CORS 预检回显 Origin、管理页 200。
   注意：`dotnet foo.dll` 的 ContentRoot 取当前目录，生产必须 `cd` 到发布目录再启动；
   发布脚本每次清空输出目录，手写的 `appsettings.Production.json` 放发布目录会被清，
   请用部署流水线/挂载方式注入。
5. CORS：`AllowedHosts: ["*"]` + `Credentials: true` 在 ASP.NET Core 下用
   `SetIsOriginAllowed` 回显实现（`WithOrigins("*")` 会启动炸，等价参考
   `allowed_hosts: ['*']` 行为）。

## 4. 密码与会话

- 口令存 `PBKDF2-SHA256`（`PasswordHasher`，自包含无第三方包）。
- 改密/禁用递增 `DavUser.AuthVersion`：管理 Cookie 绑定该版本即时失效；
  WebDAV Basic 每次请求都验口令，天然即时失效。
- 借鉴 WebBlazor 点：暂不做“遗留明文列一次性迁移”
  （`PasswordHasher.Verify` 遇到非 PHC 格式直接不通过，避免静默放行）。

## 5. 客户端兼容速查

- Windows 资源管理器 → 映射网络驱动器填 `http://host:5210/dav/`（Basic）。
- rclone / Cyberduck / WinSCP 均以 WebDAV + Basic 对接。
- v1 已知限制：集合 COPY/MOVE 501、LOCK 为兼容性假锁（Office 可保存，
  无真排他），见 `docs/permission-model.md` 路线图。
