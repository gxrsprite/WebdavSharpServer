# FreeSql.Provider.Custom（NovaDb 兼容分支）

vendored 上游 `dotnetcore/FreeSql` 的 `Providers/FreeSql.Provider.Custom`，
在 **MySql 方言**上打补丁，使其能驱动 `NewLife.NovaDb`（经标准 ADO.NET
`NovaConnection` + `NovaClientFactory`）。

- 上游基线：FreeSql `3.5.309`（与本仓 `FreeSql` 主包同版本，见 `FreeSql.Provider.Custom.csproj`）
- 只引入了 `base + MySql 方言`（`Custom` / `CustomMySql` 两个 `DataType` 可用）；
  Oracle / SqlServer / PostgreSQL 方言文件**未引入**，对应 `DataType` 不可用。
- `AssemblyName` 必须保持 `FreeSql.Provider.Custom`：FreeSql 内核按
  `"FreeSql.Custom.MySql.CustomMySqlProvider`1,FreeSql.Provider.Custom"`
  反射加载实现，改名即无法解析。
- 引用方一律 `ProjectReference` 本工程，**不要**再引用官方
  `FreeSql.Provider.Custom` NuGet 包（程序集同名）。

用法（见 `Server/Program.cs`）：

```csharp
var fsql = new FreeSqlBuilder()
    .UseConnectionFactory(DataType.CustomMySql, () => new NovaConnection("Data Source=./data/db"))
    .Build();
fsql.SetDbProviderFactory(NovaClientFactory.Instance);
```

## NovaDb 基线：1.0.2026.1121-beta0244

实测基线从 `1.0.2026.901` 升级到 `1.0.2026.1121-beta0244`（2026-11-21 构建）。
上游在 1024 稳定版之后集中补了 **MySQL 方言兼容**（1120-beta，master 上 11/20 的
40 个提交），直接消掉了本分支大部分补丁：

| 上游提交 | 能力 | 本分支变化 |
|---|---|---|
| 3951087 | 字符串反斜杠转义、`LIMIT m,n`、`` 库.表 ``/`` .表 ``、单引号别名、`FOR UPDATE`、**BIT 映射布尔**、`CHANGE COLUMN`/`RENAME TABLE` 语法 | 删去库限定符、分页改写、bit→bool 映射；**反斜杠加倍反过来必须恢复** |
| fc2b74f | bool 按 0/1 比较、`CHAR_LENGTH`、`LOCATE`（含三参数）、`UTC_TIMESTAMP`、`information_schema.Extra` 输出 `auto_increment` | 删 `LOCATE→INSTR`、`CHAR_LENGTH→LENGTH`、bool 内联 `TRUE/FALSE` |
| 2072877 | `CHANGE COLUMN`、`RENAME TABLE` 实际执行（含文件组搬迁） | CodeFirst 改名迁移可用 |
| 740a9c1 | `CREATE TABLE` 内联 `[UNIQUE] INDEX`、复合主键明确报 NotSupported | 删内联索引剥离 |
| e082829 | 列修饰符（`UNSIGNED/COLLATE/ON UPDATE`）、列级 `UNIQUE`、索引前缀长度 | 建表不再需要绕行 |
| 26be89d + 742eb2b | 规范类型名（`DataTypeNames`）、**列长度成为真实约束**、元数据输出真实长度 | 类型判等改为「同家族 + 长度一致」 |
| 76f4795 | 删表清理行日志文件 | — |
| **7b81b4d**（1121-beta0244） | **SELECT 走主键/二级索引（PK LOOKUP / PK RANGE / INDEX LOOKUP）+ LIMIT 下推**；索引文件版本升 2、旧文件自动重建 | 无补丁变动，但查询性能质变：5 万行点查 48ms → 0ms（本仓实测） |

> 等上游发稳定版（如 1.0.2026.12xx）后把 csproj 版本换掉重跑测试即可，
> 补丁清单不需要再变。

## 当前补丁清单（`NOVA:` / `NovaDb` 注释标记）

实测基线：`NewLife.NovaDb 1.0.2026.1121-beta0244`，嵌入模式。
探针与结论见 `docs/novadb.md`。

1. **类型判等（长度感知）**（`MySql/CustomMySqlCodeFirst.cs` `NovaDbTypeEquals`）：
   Nova 上报**规范类型名**（字符串一律 `string(n)`、bool 为 `bool`），
   实体侧是 MySQL 方言的 `varchar(n)/char(n)/bit(1)/enum(...)`。按
   「基类型家族 + 长度」判等：同长视为一致（避免每次启动空跑 MODIFY），
   长度变化视为真实变更（改长/改型能正确迁移）。
2. **日期函数**（`MySql/CustomMySqlExpression.cs`）：上游仍不支持 MySQL 的
   `DATE_ADD(col, INTERVAL n unit)` 写法、`CURDATE()`，且 `TIMESTAMPDIFF`
   只认 `DATEADD`/自有签名。改为 `DATEADD('UNIT', n, col)`、
   `DATE_FORMAT` 等价 `Today`、`(WEEKDAY(x)-1)` 还原 .NET `DayOfWeek`。
3. **反斜杠字面量加倍**（`MySql/CustomMySqlAdo/CustomMySqlAdo.cs`）：
   上游 1120 起**支持**反斜杠转义（标准序列还原），所以必须恢复 MySQL 式
   `\` → `\\` 加倍；否则 Windows 路径 `C:\new\test` 里的 `\n/\t`
   会被服务端当控制字符。单引号仍用 `''` 转义。
4. **主键 NOT NULL**（`CustomMySqlCodeFirst.NovaPrimaryDbType`）：Nova 建表
   不会像 MySQL 那样隐式收紧主键非空；可空主键会让每次同步误报变更。
5. **空库名回填**（`CustomMySqlCodeFirst.GetComparisonDDLStatements`）：
   嵌入模式连接串常无 `Database=`，`information_schema` 按 `TABLE_SCHEMA`
   （= 目录名）过滤时查不到表。用 `SELECT DATABASE()` 回填当前库名。
6. **建索引幂等**（`CustomMySqlCodeFirst.ExecuteDDLStatements`）：
   `CREATE INDEX` 没有 `IF NOT EXISTS`（探针确认上游仍不支持），
   “已存在”错误按精确文本吞掉，覆盖双实例同时首次启动的竞态。

## 已知局限（上游 NovaDb 侧缺口，改 SQL 也绕不过去）

- **复合主键**：上游明确报 `不支持复合主键，请改用单列代理主键`
  （740a9c1 把含糊的语法错误换成了明确提示）。本仓 `DavUserRole`
  保持代理主键 + 唯一索引。
- `DATE_ADD(..., INTERVAL n unit)`、`CURDATE()`、三参数 `TIMESTAMPDIFF`
  仍未支持（本分支改写成 `DATEADD` 等，见补丁 2）。
- `CREATE INDEX IF NOT EXISTS` 仍未支持（见补丁 6）。
- 自增列元数据：`information_schema.Extra` 现在会输出 `auto_increment`，
  但本仓实体无自增列，未做验证。
- `DbFirst`（反向工程）未移植验证：内省 SQL 仍是上游 MySQL 原样；本仓不用。

## 升级上游基线步骤

1. 按本目录文件列表从上游同路径复制新版源码（不要覆盖 `*.csproj` / 本 `README.md`）。
2. 重新应用上面 6 组补丁（均以 `NovaDb` 关键字注释标记，`grep -rn NovaDb` 即可列出）。
3. 跑 `tests/WebdavSharp.Tests` 的 NovaDb 用例（`SyncStructure` 二次空跑 +
   全字段 CRUD + 列长度约束 + `CHANGE COLUMN`/`RENAME TABLE`）。
4. 用 `grep -rn "Nova" ../../docs/novadb.md` 核对兼容矩阵，更新缺口清单。
