# 协议与规则作用域（WebDAV / SMB / FTP）

三个协议**共用同一套**用户表、挂载表与规则表。规则通过 `dav_rule.Protocols` 限定生效范围。

## 协议列 `Protocols`

| 取值 | 含义 |
|---|---|
| 空（默认） | **适配所有协议**（WebDAV、SMB、FTP 都生效） |
| `webdav` | 仅 WebDAV |
| `smb` | 仅 SMB |
| `ftp` | 仅 FTP |
| `smb,ftp` | SMB 与 FTP（逗号分隔，大小写不敏感、可含空格） |

入库前由 `DavRuleScope.Normalize` 统一格式（去重、小写、固定顺序 `webdav,smb,ftp`），
未知协议名会被丢弃。管理后台「访问规则」页用三个勾选框设置，**全不勾 = 适配所有**。

## 生效规则（三协议一致）

1. 适用规则 = 全局 + 本用户角色 + 本用户
2. **按协议过滤**（`Protocols` 空则总是适用）
3. 按 `SortOrder` 升序应用，**最后一条命中的规则生效**
4. 无命中回退 `Dav:DefaultPermissions`

协议过滤在**两处**执行（`DavAccessService.LoadRulesAsync` 与 `DavSession` 构造），
第二处是防御性冗余：即使调用方误传未过滤的规则集，也不会让 SMB 会话套用 WebDAV 专属规则。

## 路径与挂载映射

三者共用同一个**虚拟命名空间**，因此规则写法完全一致：

| 协议 | 根 | 示例 |
|---|---|---|
| WebDAV | `/dav/` | `/dav/public/open/a.txt` |
| FTP | FTP 根（`/`） | `/public/open/a.txt` |
| SMB | 每个虚拟挂载 = 一个共享 | `\\host\public\open\a.txt` |

对应关系：FTP 的 `/public/open/a.txt` 与 WebDAV 的 `/dav/public/open/a.txt`
指向同一物理文件、受同一条规则约束。SMB 因为共享名即挂载名，
`\\host\NAS\剧集` 等价于 `/NAS/剧集`。

## 权限字母在三协议下的映射

| 权限 | WebDAV | FTP | SMB |
|---|---|---|---|
| `R` 读取 | GET/HEAD/PROPFIND | RETR、LIST、NLST、SIZE、CWD | 读打开、列目录 |
| `C` 创建 | PUT(新)、MKCOL | STOR(新)、MKD | 新建 |
| `U` 更新 | PUT(覆盖)、PROPPATCH | STOR(覆盖) | 写打开 |
| `D` 删除 | DELETE、MOVE(源) | DELE、RMD、RNFR/RNTO(源) | 删除 |

集合类操作（FTP `RMD`、移动目录）与 WebDAV 一致做**整树判定**：任一后代缺权限则整体拒绝。

## 实现结构

```
Core/Services/DavAccessService.cs   认证 + 装载挂载/规则 + 建立会话
Core/Services/DavSession.cs         会话：解析物理路径、权限判定、列表过滤
Core/Services/DavRuleScope.cs       协议列解析与规范化
Server/WebDav/DavMiddleware.cs      WebDAV 适配（HTTP 动词）
Server/Ftp/*                        FTP 适配（IAuthenticationProvider / IFileSystemProvider）
```

新增协议时只需实现“认证 + 逐操作鉴权”的适配层，规则与路径语义自动复用。

## 端口与配置

| 协议 | 配置项（首次启动的初始值） | 默认 |
|---|---|---|
| WebDAV | `Dav:Address` / `Dav:Port` / `Dav:Prefix` | `127.0.0.1` / `5210` / `/dav/` |
| FTP | `Dav:FtpEnabled` / `Dav:FtpAddress` / `Dav:FtpPort` | `false` / 跟随 Address / `2121` |
| FTP 被动模式 | `Dav:FtpPassivePortStart` / `FtpPassivePortEnd` | 0（交给系统分配） |
| SMB | `Dav:SmbEnabled` / `Dav:SmbAddress` / `Dav:SmbPort` / `Dav:SmbEnableSmb1/2/3` | `false` / 跟随 Address / `445` / 关闭/开启/开启 |

## 服务管理（管理页 /admin/services）

每个协议独立启停、独立配监听 IP/端口，存在 `dav_service` 表里。
**配置文件只管首次启动的初始值**：表空时播种一次，之后以 DB 为准，
改配置文件重启不会覆盖管理页改过的值。

- **FTP/SMB**：保存后点“重启”即按新 IP/端口重新监听（重启也会重建 SMB 共享、
  顺带拾取挂载变更）。启动失败只记错误，不影响其他协议。
- **WebDAV**：开关即时生效（中间件旁路，503）；IP/端口是 Kestrel 宿主绑定，
  改完需**重启应用**。关掉它不会锁死后台（/admin 与 /healthz 照常）。
- 同等能力也暴露为管理 API（与后台同一套 Cookie 鉴权）：
  `GET /api/admin/services`、`PUT /api/admin/services/{webdav|ftp|smb}`、
  `POST /api/admin/services/{protocol}/restart`（webdav 返回运行状态，不重启宿主）。

FTP 默认**不启用**（`FtpEnabled: false`），需要时显式打开。
默认端口 2121 是非特权端口，Linux 上无需 root。

SMB 默认**不启用**（`SmbEnabled: false`）。注意两点：

1. **NTLM 需要 NT 哈希**：服务端验证 NTLMv2 响应必须持有 NT 哈希或明文口令，
   PBKDF2 哈希不够用（Samba/AD 同样存 NT 哈希）。
   因此 `dav_user.NtHash` 为空的用户**无法 SMB 登录**，直到（重）设密码
   （新建用户、重置密码、种子用户都会自动写入；旧库中已存在的用户需改一次密码）。
   NT 哈希是无盐 MD4，仅在需要 SMB 登录时保留。
2. **端口可配**：`Dav:SmbPort` 默认 445，改成别的端口是正式支持的配置。
   适用场景：445 被占用、多实例共存、联调。但注意 Windows 资源管理器 /
   net use、macOS Finder、手机客户端只连 445，非标准端口只对支持指定端口的
   客户端有效（如 `smbclient -p`、pysmb）。Windows 上 445/139 常被系统自带
   文件共享（LanmanServer）占用——此时服务会记一条错误日志并继续跑
   WebDAV/FTP，不会崩。
   Linux 上绑定 445 需 root 或 `setcap cap_net_bind_service`。

## 已知未覆盖

- **FTP 操作未进审计日志**（审计目前只在 WebDAV 中间件埋点）
- FTP 未做 SITE 命令扩展、无 FTPS（明文 FTP；如需 TLS 应加 `AUTH TLS` 支持）
- SMB 已用 pysmb 独立客户端在自定义端口上联调通过（登录、列目录、规则隐藏/拒绝、
  读写删文件与目录）；生产 445 端口需另行实测（本机被系统文件共享占用）。
  SMBLibrary 为 LGPL-3.0，闭源分发需注意
