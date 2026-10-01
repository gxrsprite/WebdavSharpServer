# 客户端兼容矩阵（实测）

## CX 文件浏览器（Android，JCIFS 系 SMB1）— 2026-09-16 模拟器实测通过

环境：模拟器 `emulator-5554` → `10.0.2.2:5000`，admin/admin；
服务端 SMB1+SMB2+SMB3 全开（开发联调标配，见 `appsettings.Development.json`；
抓包确认本 CX 版本数据面只走 SMB1，SMB3 开关不影响它）。

| 操作 | 服务端动词链 | 结果 |
|---|---|---|
| 认证 | SMB1 negotiate → NTLMv2 SessionSetup | ✅ |
| 根目录 | TRANS `\\PIPE\\LANMAN` NetShareEnum level 1 → `public`、`NAS` | ✅（曾因未实现 RAP 显示“加载错误”，已修） |
| 列目录 | TRANS2_FIND_FIRST2（含中文名，`private` 被规则隐藏） | ✅ |
| 空目录 | FIND_FIRST2 → SUCCESS+0（曾回 NO_SUCH_FILE，CX 报“拒绝访问”，已修） | ✅ 显示“清空文件夹” |
| 建目录 | NTCreateAndX (DIRECTORY_FILE) | ✅ |
| 写文件 | CreateFile + WriteFile + EOF（41KB/4KB，md5 与源一致） | ✅ |
| 读文件 | 网内复制走 ReadFile（递归目录复制字节一致） | ✅ |
| 改名 | FileRenameInformationType2 | ✅ |
| 删文件 | FileDispositionInformation（立即删除） | ✅ |

已知客户端侧 quirks（非服务端 bug）：
- 模拟器里保存的旧连接若存了过期密码，NTLM 直接失败、随后所有操作
  无请求；删掉重建（主机/端口/用户名/密码）即可。定位时先看服务端
  `SMB 登录失败` 日志，不要先怀疑协议。
- Gboard 拼音会对 `adb shell input text` 的 `.` 做中文转换、拉丁做拼音
  切分；联调输入 IP/密码建议切英文子类型或暂时 `ime disable`。

## MT管理器（Android）— 2026-10-01 真机验证通过

SMB / WebDAV / FTP 三协议连接与基本文件操作（列目录、上传、下载）均验证可用。

## ES文件管理器（Android）— 2026-10-01 真机验证通过

SMB / WebDAV / FTP 三协议连接与基本文件操作（列目录、上传、下载）均验证可用。

## Windows MiniRedir（`net use` / Explorer，WebClient 服务）— 2026-09-14 实测通过

环境：本机 `http://127.0.0.1:5210/dav/public`，Basic(admin/admin)。

| 操作 | 客户端动作 | 服务端动词链 | 结果 |
|---|---|---|---|
| 挂载 | `net use Y: <url> /user:admin admin` | OPTIONS → PROPFIND | ✅（需 `BasicAuthLevel=2` 才允许 http Basic，见下） |
| 列表 | Explorer / `dir` | PROPFIND depth 1 | ✅ |
| 建目录 | `New-Item -ItemType Directory` | MKCOL | ✅ 201 |
| 写文件 | `Out-File` | PUT（新建→201） | ✅ 内容一致读回 |
| 读文件 | `Get-Content` | GET | ✅ |
| 改名 | `Rename-Item` | MOVE（单文件） | ✅ 201 |
| 同目录复制 | `Copy-Item` | COPY + Overwrite | ✅ |
| 删文件 | `Remove-Item <file>` | DELETE | ✅ 204 |
| 删目录 | `rmdir /s/q`（cmd） | DELETE（整树 D 判定） | ✅ 204 |

已知客户端侧 quirks（非服务端 bug）：
- PowerShell `Remove-Item -Recurse <dir>` 会枚举出 `.` 伪条目并尝试删除它，
  报 `Null character in path`；改用 `cmd rmdir /s /q` 正常。IIS 等标准服同现象。
- `dir` 显示 `.`/`..` 行是重定向器合成导航项，PROPFIND 自身条目符合 RFC 4918。

前置条件（Windows 默认不允许 http Basic）：
```powershell
Set-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters `
  -Name BasicAuthLevel -Value 2
Restart-Service WebClient -Force
```

## Windows 资源管理器（Explorer）— 2026-10-01 验证通过

SMB 与 FTP 连接及基本文件操作（列目录、上传、下载）验证可用。
其他 Windows 客户端（rclone / WinSCP / FileZilla 等）尚未测试。

## HttpClient 脚本矩阵（回归冒烟，见各轮记录）

OPTIONS / PROPFIND(0,1,infinity) / GET / PUT / MKCOL / DELETE /
COPY·MOVE（文件+集合）/ LOCK·UNLOCK·423 / Overwrite:F→412 /
Basic 401 / Cookie 管理后台 200 —— 全部通过。

## 待测

- MS Office（Word/Excel）保存链路：LOCK → PUT → UNLOCK 序列（服务端已实现真锁，
  需 Office 实测确认 `If` 头写法兼容）。
- rclone / Cyberduck / WinSCP / macOS Finder / GNOME Nautilus。
- HTTPS 反向代理后（`BehindProxy` + `X-Forwarded-For`）的客户端表现。
