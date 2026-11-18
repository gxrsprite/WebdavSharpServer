# 跨平台说明

WebdavSharpServer 基于 .NET 10，目标平台包括 Windows、Linux 和 macOS，支持 x64、arm64 等 .NET 支持的架构。实际部署时选择对应的 .NET Runtime Identifier（RID），并安装匹配的 .NET 10 Runtime（自包含发布除外）。

默认数据库 NovaDb 使用纯 C# 托管实现，不依赖 SQLite 等原生数据库库，因此数据库本身不限制操作系统、CPU 架构或 libc 实现。FreeSql 也可连接其他数据库；选择外部数据库时，跨平台条件还取决于相应驱动和数据库服务。

## 平台相关实现

| 关注点 | 处理 |
|---|---|
| 物理路径大小写 | Windows/macOS 默认不敏感，Linux 默认敏感；路径归属、遍历去重和锁命名空间均按平台处理 |
| 虚拟路径大小写 | 按 backing 挂载根探测，失败时回退平台默认行为 |
| 根路径形态 | Windows 盘符根与 Unix 根分别处理，避免裁剪尾分隔符后语义变化 |
| 路径拼接 | 使用 `Path.Combine` 与系统分隔符；虚拟路径统一使用 `/` |
| 目录浏览控件 | Windows 展示盘符及卷标；Unix 展示挂载点并将 `/` 放在首位 |
| 系统根目录 | 通过跨平台 `PlatformPath.SystemRoot` 获取 |
| 测试夹具 | 使用临时目录，不写死盘符；Windows 盘符用例带平台条件 |

## 数据库

默认配置为 NovaDb 嵌入模式，数据目录默认为 `LocalPaths/novadb`。数据库为纯托管 C# 实现，不需要随应用打包 `e_sqlite3` 等 native library，也不需要 SQLite 原生初始化步骤。

项目保留 FreeSql 的其他数据库接入方式。切换到 SQLite 时，需引用并发布目标 RID 对应的 SQLite 原生运行库；接 PostgreSQL、MySQL 或其他数据库时，则由相应 Provider、驱动及数据库服务决定部署条件。

## 发布

PowerShell 发布脚本：

```powershell
pwsh -File scripts/publish-webdavsharp.ps1 -Runtime linux-arm64
```

也可直接使用 .NET CLI：

```bash
dotnet publish src/WebdavSharp.Server/WebdavSharp.Server.csproj \
  -c Release -r linux-x64 --self-contained false -o out
```

`-Runtime` 可换为目标平台对应 RID，例如 `win-x64`、`linux-arm64`、`osx-arm64`、`linux-musl-x64`。框架依赖发布需目标机预装 .NET 10 Runtime；自包含发布可使用 `-SelfContained`。

当前项目的主要验证环境是 Windows x64；其他操作系统及架构建议在对应目标机完成协议端到端验证。

## 文件换行

仓库 `.gitattributes` 控制文本文件换行，以 LF 入库并按平台检出。
