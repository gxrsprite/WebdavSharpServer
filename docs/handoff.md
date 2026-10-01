# WebdavSharp 项目交接文档

> 更新时间：2026-09-16（CX 兼容已达成，见 §7/§9/§13）

## 1. 项目目标

WebdavSharp 是基于 .NET 10 的跨平台文件服务，提供：

- WebDAV
- FTP
- SMB
- 管理后台（Blazor Server）
- 用户、角色、挂载点、访问规则和审计日志

WebDAV、FTP、SMB 共用用户表、挂载表和规则表。规则的 `Protocols` 字段为空时表示适用于全部协议。

## 2. 当前服务状态

开发环境当前使用：

| 服务 | 地址 | 状态 |
|---|---|---|
| WebDAV / 管理后台 | `0.0.0.0:5210` | 已启用 |
| FTP | `0.0.0.0:2121` | 开发环境曾启用，按数据库状态为准 |
| SMB | `0.0.0.0:5000` | 开发联调使用，SMB1/SMB2 开启、SMB3 关闭 |

管理后台：

```text
http://<服务器IP>:5210/admin
```

开发种子账号：`admin / admin`。生产环境必须立即修改密码。

## 3. 服务配置模型

服务设置保存在 SQLite 的 `dav_service` 表中，每个协议一行：

- `Protocol`
- `IsEnabled`
- `Address`
- `Port`

配置文件 `Dav:*` 只用于首次播种默认值，后续以数据库为准。管理页：

```text
/admin/services
```

FTP/SMB 修改地址或端口后需要点击“重启”。WebDAV 的开关即时生效；WebDAV 地址和端口由 Kestrel 管理，修改后需要重启应用。

## 4. 关键代码位置

```text
src/WebdavSharp.Core/Entities/DavServiceSetting.cs
src/WebdavSharp.Core/Services/DavServiceSettings.cs
src/WebdavSharp.Core/Services/DavAccessService.cs
src/WebdavSharp.Core/Services/DavSession.cs
src/WebdavSharp.Core/Services/DavRuleScope.cs
src/WebdavSharp.Core/Services/NtHash.cs

src/WebdavSharp.Server/Program.cs
src/WebdavSharp.Server/SeedData.cs
src/WebdavSharp.Server/WebDav/DavMiddleware.cs
src/WebdavSharp.Server/Ftp/DavFtpHostedService.cs
src/WebdavSharp.Server/Smb/DavSmbHostedService.cs
src/WebdavSharp.Server/Smb/DavNtlmMechanism.cs
src/WebdavSharp.Server/Smb/DavSmbFileStore.cs
src/WebdavSharp.Server/Components/Pages/Services.razor
```

## 5. SMB 认证和兼容性

SMB 使用自定义 NTLMv2 机制：

- `dav_user.NtHash` 保存 NT 哈希（MD4(UTF-16LE(password))）
- PBKDF2 哈希不能单独用于 SMB NTLMv2 验证
- 新建用户和重置密码时会自动写入 NT 哈希
- 旧用户若没有 `NtHash`，需要重新设置密码

`DavNtlmMechanism` 已支持：

- NTLMv2 proof 校验
- `KEY_EXCH` 密钥交换
- RC4 解密 `EncryptedRandomSessionKey`
- 128-bit/56-bit 加密能力声明
- Sign/Seal 能力声明
- 完整 NTLM TargetInfo AV 序列

## 6. CX 文件浏览器联调结论

已从真实手机提取 CX APK：

```text
artifacts/cx-apk/cx-file-explorer.apk
```

已安装到 Android 模拟器 `omt-root35`。为支持模拟器，安装过：

- Microsoft OpenJDK 17
- Android Emulator Hypervisor Driver 2.2
- VirtualMachinePlatform

模拟器访问宿主机 WebdavSharp 时使用：

```text
主机：10.0.2.2
端口：5000
用户名：admin
密码：admin
```

物理手机使用宿主机局域网地址，例如：

```text
主机：10.0.0.45
端口：5000
```

## 7. CX SMB 行为（已解决，2026-09-16 实测通过）

抓包文件：

```text
artifacts/cx-smb.pcap        # 旧：NOT_IMPLEMENTED 时代
artifacts/cx-rap-new.pcap    # 旧：坏凭据时代的 NTLM 失败
artifacts/cx-del.pcap        # 新：含删除/重命名/上传全流程
```

CX（JCIFS 系）根目录加载走 SMB1 RAP（不是 `srvsvc/NetShareEnum`）：

```text
IPC$ -> TRANS \\PIPE\\LANMAN (NetShareEnum level 1, "WrLeh"/"B13BWz")
```

修复前服务端回 `STATUS_NOT_IMPLEMENTED` → CX 显示“加载错误”。
修复（见 §13）：vendored SMBLibrary + `RapHelper`，返回磁盘共享
（`IPC$` 排除，与 `srvsvc` 一致）。CX 真机验证：根目录列出
`NAS`（45 项）、`public`（2 项），截图见 `artifacts/cx-final.png`。

第二个坑：空目录。`FindFirst2` 在“存在但 0 匹配”时按 MS-CIFS
SHOULD 回 `STATUS_NO_SUCH_FILE`，CX 把它当致命错误
（“加载错误！拒绝访问”并断开会话）。已改为回 `SUCCESS + 0 条目`
（`SID=0`）；路径不存在时仍报错。Explorer/pysmb/impacket 对两种
回法都兼容，回归全过。CX 验证：空目录显示“清空文件夹”，见
`artifacts/cx-empty.png`。

## 8. 已验证的 SMB 操作

使用 Impacket 和 pysmb 做过回归验证：

- NTLM 登录
- SMB3/SMB2 协商
- `public`、`NAS` 共享
- 规则隐藏 `private`
- 读取文件
- 上传文件
- 删除文件
- 目录删除权限控制
- 重命名

最近一次测试结果：

```text
单元测试：187/187
Impacket SMB 测试：5/5
pysmb SMB 测试：8/8
```

## 9. CX 删除问题的结论（已解决，2026-09-16）

之前“CX 删除失败时服务端只看到认证或重命名请求”的根本原因是
**模拟器里保存的连接用了过期密码**（`SMBPrefs.xml` 中的加密口令
不是 `admin`），NTLM 阶段就被拒，删除请求根本没发出来——不是协议问题。

用抓包向量离线重算 proof（`tests` 临时验证，已删）确认服务端校验正确；
删除脏连接、重建 `10.0.2.2:5000/admin/admin` 后，CX 删除一次成功：
服务端收到标准 `FileDispositionInformation`，立即删除逻辑处理，
文件落盘消失，CX 界面同步。抓包见 `artifacts/cx-del.pcap`。

结论：普通文件删除逻辑无需再改。以后遇到“客户端无请求”，先查认证。

## 10. 运行和测试命令

启动服务：

```powershell
dotnet run --project src/WebdavSharp.Server
```

构建：

```powershell
dotnet build WebdavSharp.slnx --nologo
```

测试：

```powershell
dotnet test tests/WebdavSharp.Tests --nologo
```

SMB 联调探针：

```powershell
python artifacts/smb-impacket-probe.py
python artifacts/smb-probe.py 5000
```

ADB 工具：

```text
F:\小米工具\platform-tools
D:\AndroidReverseTools\platform-tools
```

CX 包名：

```text
com.cxinventor.file.explorer
```

抓包示例：

```powershell
adb -s emulator-5554 shell "su -c 'tcpdump -i any -s 0 -w /data/local/tmp/cx.pcap tcp port 5000 &'"
adb -s emulator-5554 shell "su -c 'pkill tcpdump'"
adb -s emulator-5554 pull /data/local/tmp/cx.pcap artifacts/cx-smb.pcap
```

## 11. 当前未完成事项（2026-09-16 修订）

1. ~~实现 SMB1 RAP `\\PIPE\\LANMAN` 的 `NetShareEnum` 响应~~ ✅ 已完成并真机验证。
2. ~~用 CX 模拟器重新抓包验证根目录加载成功~~ ✅（`artifacts/cx-final.png`）。
3. ~~抓取 CX 删除动作的完整 SMB 报文~~ ✅（`artifacts/cx-del.pcap`，系坏凭据问题，已解决）。
4. ~~评估是否恢复 SMB3 默认支持；CX 联调使用 SMB1+SMB2、关闭 SMB3~~
   ✅ 已解决（2026-09-16）：三个方言全开。实测 CX 数据面纯走 SMB1
   （JCIFS 经典栈），SMB3 开关不影响它；impacket 以 SMB3（0x300，强制签名）
   5/5 通过。`appsettings.Development.json` 已持久化 `SmbEnableSmb1=true`
   （SMB2/SMB3 默认即开），生产环境建议关 SMB1。
5. 生产环境测试标准端口 445；Windows 系统自带 LanmanServer 可能占用 445。
6. 检查并修改开发默认账号密码，避免局域网暴露默认凭据。
7. （新增）`third_party/SMBLibrary` 跟随上游的方式见其中的
   `README.webdavsharp.md`；升级后重跑全部回归。

## 12. 注意事项

- `LocalPaths/`、`artifacts/` 为本地运行数据和联调产物，不应提交账号、密码、数据库或抓包中的敏感信息。
- SMB 非标准端口 `5000` 只有支持自定义端口的客户端才能连接。
- 生产环境开放 `0.0.0.0` 前应配置防火墙和强密码。
- SMBLibrary 为 LGPL-3.0，闭源分发前需要进行许可证合规评估。
  现已 vendoring 到 `third_party/SMBLibrary`（见其中的
  `README.webdavsharp.md`），注意保留该目录与 License.txt。

## 13. CX 兼容补丁总览（2026-09-16）

| # | 位置 | 内容 |
|---|---|---|
| 1 | `third_party/SMBLibrary/.../Server/SMB1/RapHelper.cs`（新增） | 最小 MS-RAP NetShareEnum（level 0/1）编解码 |
| 2 | `.../Server/SMB1/TransactionHelper.cs` | LANMAN 分支接 RapHelper；透传共享表；未知 opcode 回 NOT_SUPPORTED |
| 3 | `.../Server/SMB1/Transaction2SubcommandHelper.cs` | 空目录 FindFirst2 回 SUCCESS+0（原 NO_SUCH_FILE） |
| 4 | `.../Server/SMBServer.SMB1.cs` | 调用点透传 `m_shares` |
| 5 | `src/WebdavSharp.Server.csproj` | NuGet → `ProjectReference`（+显式 `Utilities` 引用，去掉未使用的 Adapters） |
| 6 | `src/WebdavSharp.Core/Services/DavSession.cs`（新增 `ListRootEntries`）+ `Ftp/DavFtpFileSystemProvider.cs` | FTP 根列挂载入口（原只列回退目录物理子项，NAS/D 不可见）；语义与 WebDAV PROPFIND 根一致（2026-09-26） |
| 7 | `src/WebdavSharp.Server/Smb/DavNtlmMechanism.cs`（`BuildTargetInfo` 恢复完整 AV）+ vendored `AVPairKey.Timestamp: 0x0006→0x0007` | 9/21 曾切最小 AV 序列保 Windows，导致 impacket SMB2/3 登录在客户端解析 challenge 时崩（缺 AV_DNS_HOSTNAME）；完整序列才是规范要求，flags 回声 + version 修订保留（2026-09-26） |

验证矩阵（模拟器 `emulator-5554`，CX 真机操作）：认证 / 根目录枚举 /
列目录（含中文名与 `private` 隐藏）/ 空目录 / 新建文件夹 / 重命名
（Type2）/ 删除文件 / 上传（md5 一致）/ 目录递归复制（md5 一致），
截图见 `artifacts/cx-final.png`、`artifacts/cx-empty.png` 等；
自动化回归：单测 199/199、`smb-probe.py` 8/8、
`smb-impacket-probe.py` 5/5、`cx-rap-probe.py`、
`smb-empty-dir-probe.py` 全过。
