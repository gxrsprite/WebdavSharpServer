# 跨平台说明

目标：Windows / Linux / macOS 都能跑（x64；ARM64 见下方限制）。

## 平台相关实现要点

| 关注点 | 处理 |
|---|---|
| 物理路径大小写 | `PlatformPath.Comparison/Comparer`：Win/macOS → 不敏感，Linux → **敏感**。用于路径归属判定、遍历去重、锁命名空间、大小写探测缓存键 |
| 虚拟路径（URL/规则）大小写 | `DavCaseProbe` 按 backing 挂载根**探测**（翻转尾名首字母后 stat），失败回退平台默认 |
| 根路径形态 | Windows 盘符根 `C:\` 保留尾分隔符（去尾会变“驱动器相对路径”）；Unix 根 `/` 单独处理（去尾会变空串） |
| 路径拼接 | 一律 `Path.Combine` + `Path.DirectorySeparatorChar`；虚拟路径恒用 `/` |
| 目录浏览控件 | Windows 列盘符（含卷标）；Unix 列挂载点并确保 `/` 在首位 |
| 系统根 | `PlatformPath.SystemRoot`（不再用 `Environment.SystemDirectory`，那是 Windows 专用） |
| 测试夹具 | 用临时目录构造，不写死盘符；盘符根用例带 `IsWindows()` 守卫 |

## 数据库平台矩阵

`Dav:Database` 可选 `Sqlite`（默认）/ `PostgreSQL` / `MySql` / `SqlServer`。

SQLite 走**微软官方适配器**：`FreeSql.Provider.SqliteCore`（基于 `Microsoft.Data.Sqlite.Core`）
+ `SQLitePCLRaw.bundle_e_sqlite3` 提供原生库。各 RID **实测**：

| RID | 原生库 | 实测格式 |
|---|---|---|
| linux-x64 | `libe_sqlite3.so` | ELF ✅ |
| linux-arm64 | `libe_sqlite3.so` | ELF ✅ |
| osx-x64 / osx-arm64 | `libe_sqlite3.dylib` | Mach-O ✅ |
| win-x64 / win-x86 / win-arm64 | `e_sqlite3.dll` | PE ✅ |

> 历史坑：早期用的 `FreeSql.Provider.Sqlite`（`System.Data.SQLite`）只带
> linux-x64/osx-x64/win-x64/win-x86 原生库，**ARM64（树莓派、Apple Silicon）没有**，
> 且 Unix 下原生库名义上叫 `SQLite.Interop.dll`（实体是 ELF/Mach-O，容易被扩展名误导）。
> 换成 Microsoft.Data.Sqlite 系后 ARM64 一并解决——这也是选它的主要原因。

启动时会调用 `SQLitePCL.Batteries_V2.Init()` 实际验证原生库可加载（幂等）；
失败则 fail fast 并提示改用 PostgreSQL/MySQL，不会留到运行期抛晦涩的
`DllNotFoundException`。（`Microsoft.Data.Sqlite.Core` 不自带 bundle，必须显式初始化。）

### 数据库推荐

- 开发：SQLite（零依赖、单文件）
- 生产 Linux/macOS（含 ARM64）：SQLite 或 PostgreSQL 均可；
  多实例/高并发建议 PostgreSQL（Npgsql 纯托管）或 MySQL

## 发布

```bash
# 任意平台（推荐，不依赖 PowerShell）
dotnet publish src/WebdavSharp.Server -c Release -r linux-x64 --self-contained false -o out
dotnet publish src/WebdavSharp.Server -c Release -r linux-arm64 --self-contained false -o out
dotnet publish src/WebdavSharp.Server -c Release -r osx-arm64 --self-contained false -o out

# Windows（PowerShell 7）
pwsh -File scripts/publish-webdavsharp.ps1
```

`scripts/publish-webdavsharp.ps1` 需要 PowerShell 7（`pwsh`）；
其他平台直接用上面的 `dotnet publish`。部署时把工作目录切到发布目录再启动
（ContentRoot 取当前目录）。

## 文件换行

本目录带 `.gitattributes`：以 LF 入库、按平台检出。
Windows 上开发、Linux 上部署时不会因为 CRLF 产生噪音 diff。

## 已知未覆盖

- 发布脚本仅 PowerShell 版（其他平台用 `dotnet publish`）
- macOS / Linux 上尚未做真机端到端冒烟：当前实测平台为 Windows x64；
  Linux x64 / linux-arm64 / osx-arm64 **发布物已逐 RID 验证**（原生库格式 + 框架脚本就位），
  但未在真机上跑过请求链路。CI 加对应 runner 即可补齐。
