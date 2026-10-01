# third_party/SMBLibrary — vendored 上游源码 + WebdavSharp 补丁

上游：https://github.com/TalAloni/SMBLibrary ，tag `v1.5.7`
（NuGet 引用曾用 `1.5.7.1`；经核对 v1.5.7 与 v1.5.8 的服务端 TRANS 逻辑一致，
且 1.5.8 仍未实现 RAP，vendoring 未降级任何已用行为）。

## 为什么 vendoring（而不用 NuGet 包）

CX 文件管理器（JCIFS 系）枚举服务器根目录走 SMB1 RAP
（`TRANS \PIPE\LANMAN` + `NetShareEnum`），而上游
`TransactionHelper.GetCompleteTransactionResponse` 对 `\PIPE\lanman`
直接返回 `STATUS_NOT_IMPLEMENTED`。该分支在库内部写死，
外部（`ISMBShare`/`INTFileStore`）无法拦截，只能改库源码。

## 本目录相对上游 v1.5.7 的差异（全部）

1. `SMBLibrary/Server/SMB1/RapHelper.cs`（新增）：最小 [MS-RAP]
   `NetShareEnum` 编解码（level 0/1），纯函数、可单测。
   详见 `tests/WebdavSharp.Tests/RapHelperTests.cs`（8 用例，含真实抓包向量）。
2. `SMBLibrary/Server/SMB1/TransactionHelper.cs`：
   - `\PIPE\LANMAN` 分支改为调用 `RapHelper`，用 `SMBShareCollection`
     中的磁盘共享生成枚举（`IPC$` 排除，与 `srvsvc` 行为一致）；
     未知 RAP opcode 回 `STATUS_NOT_SUPPORTED`（对齐 impacket）。
   - `GetTransactionResponse` × 2 与 `GetCompleteTransactionResponse`
     增加 `SMBShareCollection shares` 参数（调用方见下）。
   - 新增 `GetLanmanResponse` 私有方法。
3. `SMBLibrary/Server/SMB1/Transaction2SubcommandHelper.cs`：
   - `FindFirst2` 在“目录存在但 0 匹配”时返回 `SUCCESS + 0 条目`
     （`SID=0`，不建搜索句柄），不再回 `STATUS_NO_SUCH_FILE`。
     MS-CIFS 此处是 SHOULD；CX 把 `NO_SUCH_FILE` 当致命错误
     （“拒绝访问”并断开会话），Explorer/pysmb/impacket 对两种回法都兼容；
     路径不存在时仍走原来的错误分支。
4. `SMBLibrary/Server/SMBServer.SMB1.cs`：两个 `TransactionHelper`
   调用点透传 `m_shares`。
5. `SMBLibrary/SMBLibrary.csproj`：`ILRepack` 目标仅在
   `ILRepack.exe` 存在时执行（否则 Release 构建必失败；
   不合并时 `Utilities.dll` 随产物输出，运行不受影响）。
6. `SMBLibrary/Authentication/NTLM/Structures/Enums/AVPairKey.cs`：
   `Timestamp` 上游笔误写成 `0x0006`（与 `Flags` 冲突），按
   MS-NLMP 2.2.2.1 改为 `0x0007`。不改的话完整 TargetInfo 会在线上
   写出两个 id-6（单测 `Challenge_TargetInfo_HasFullAvSequence` 锁定）。
7. `Directory.Build.props`（新增，本目录）：屏蔽仓库根的全局
   `TargetFramework=net10.0` 等设置，让上游工程按自身
   `net20;net40;netstandard2.0` 构建（P2P 引用实际只编 `netstandard2.0`）。

## 消费方式

`src/WebdavSharp.Server` 用 `ProjectReference` 引用
`SMBLibrary.csproj` + `Utilities.csproj`
（后者因上游 `PrivateAssets=All` 不会自动传递，需显式引用；
`SMBLibrary.Adapters` 包未被实际使用，已移除）。
`tests` 工程经传递引用拿到同一套源码。

## 上游同步

需要跟进上游时：对比本目录与上游同文件的 diff
（补丁均标 `WebdavSharp patch` 注释），rebase 后重跑
`dotnet test` + `artifacts/smb-probe.py` +
`artifacts/smb-impacket-probe.py` + `artifacts/cx-rap-probe.py`。

许可证：LGPL-3.0-or-later（上游 `License.txt` 保留）。
闭源分发前仍需按 `docs/handoff.md` 做合规评估。
