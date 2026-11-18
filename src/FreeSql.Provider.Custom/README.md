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

## NovaDb 补丁清单（`NOVA:` 注释标记）

实测基线：`NewLife.NovaDb 1.0.2026.901`，嵌入模式。探针脚本与结论见
`docs/novadb.md`。

1. **去库限定符**（`MySql/CustomMySqlUtils.cs`）：
   Nova 解析器不支持 `` `库`.`表` `` 两段式（遇到 `.` 报错），而嵌入模式是单库，
   `QuoteSqlNameAdapter(db, table)` 直接退化为 `` `table` ``。
2. **分页改写**（`MySql/Curd/CustomMySqlSelect.cs`）：
   Nova 不支持 `limit offset,count` 逗号形式，改写为 `LIMIT n OFFSET m`；
   只有 skip 时用 `LIMIT 2147483647 OFFSET m` 兜底。
3. **bool 映射**（`MySql/CustomMySqlCodeFirst.cs`）：`bit(1)` → `bool`
  （Nova 无 `BIT` 类型）。Nova 上报 bool 列为 `tinyint(1)`，比对时按家族判等。
4. **CodeFirst 内省 SQL**（同上）：`information_schema.columns` 查询去掉
   `LOCATE()`（Nova 无此函数），`is_identity` 写死 `0`
   （Nova 的元数据不暴露自增标记；本仓实体无自增列）；
   内省查询一律**不用单引号别名**（Nova 对系统视图上 `'xxx'` 别名有解析 quirk，
   会报“without a row context”；C# 侧本就按位置取值，别名无用）。
   另 `CREATE TABLE` 体内行内 `INDEX/UNIQUE INDEX` 定义会被剥离，
   改为独立 `CREATE [UNIQUE] INDEX` 执行（Nova 不支持表内建索引）；
   复合主键不支持——`DavUserRole` 已改为代理主键 + 唯一索引。
5. **类型判等归一**（同上 `NovaDbTypeEquals`）：Nova 上报归一化类型
   （`varchar(50)→varchar(255)`、`char(36)→varchar(255)`、`enum(…)→varchar(255)`、
   `datetime(3)→datetime`），按基类型家族判等，否则每次同步都误报变更。
6. **字符串函数**（`MySql/CustomMySqlExpression.cs`）：含通配符的
   `Contains/StartsWith/EndsWith` 由 `LOCATE()` 改 `INSTR()`；
   `string.Length` 由 `CHAR_LENGTH()` 改 `LENGTH()`（Nova 的 `LENGTH` 按字符计）；
   `IndexOf` 双参数改 `INSTR()-1`，三参数直接抛 `NotSupportedException`。
7. **日期函数**（同上）：`AddDays/AddHours/…` 由 `date_add()` 改 `DATEADD('UNIT',n,col)`；
   `Subtract/CompareTo` 用 `TIMESTAMPDIFF('SECOND',…)`；
   `DayOfWeek` 用 `(WEEKDAY(x)-1)`，`Today` 用 `DATE_FORMAT` 等价实现。
   `NOW()` 大写（Nova 函数名大小写不敏感，实测小写亦可，统一大写求稳）。
8. **反斜杠转义**（`MySql/CustomMySqlAdo/CustomMySqlAdo.cs`）：
   Nova 字面量只有 `''` 转义。去掉 MySQL 式的 `"\"→"\\"` 加倍，
   否则 Windows 路径（`C:\data\x`）入库会被写成双反斜杠。
9. **bool 内联**（同上）：bool 值内联为 `TRUE/FALSE` 关键字而非 `1/0`。
   Nova 的 `CompareValues` 不做 bool↔数字隐转，`bool列 = 1` 恒为 false
   （WHERE 直接查不出行——登录第一关就过不了）。
10. **主键 NOT NULL**（`MySql/CustomMySqlCodeFirst.cs` `NovaPrimaryDbType`）：
    Nova 建表不会像 MySQL 那样隐式收紧主键列可空性；可空主键列会导致
    每次同步误报变更。建表时给**实体侧非空**的主键列补 `NOT NULL`
    （可空 string 主键保持原样，否则永远收敛不了）。
11. **空库名回填**（同上）：嵌入模式连接串常无 `Database=`，此时
    `information_schema` 按空库名过滤永远查不到表。用 `SELECT DATABASE()`
    （Nova 原生支持，返回目录名）回填当前库名。
12. **建索引幂等**（同上 `ExecuteDDLStatements`）：独立 `CREATE INDEX`
    无 `IF NOT EXISTS` 写法，“已存在”错误按幂等吞掉（精确匹配错误文本），
    覆盖双实例同时首次启动的竞态。
13. **归一判等优先**（同上）：`NovaDbTypeEquals` 命中即跳过整条上游
    类型细化链，否则 `tinyint/varchar` 分支会把归一结果覆盖回“变更”。

## 已知局限（上游 NovaDb 侧缺口，改 SQL 也绕不过去）

- `FOR UPDATE`、三参数 `IndexOf`、自增列元数据、`CHANGE COLUMN` 改名、
  `RENAME TABLE` 改表名、`utc_timestamp()`：用到即 loud fail。
  本仓当前查询均已规避（`DateTime.UtcNow/Now` 只在 C# 侧求值，不进 lambda）。
- `LENGTH()` 在 Nova 按字符计、MySQL 按字节计：中文 `Length` 查询语义不同，
  本仓暂无此类查询。
- `information_schema` 的 `EXTRA` 恒为空：`AUTO_INCREMENT` 列会被当普通列，
  本仓实体无自增列，不受影响。
- `DbFirst`（反向工程）未移植验证：内省 SQL 仍是上游 MySQL 原样（含 `LOCATE`），
  在 Nova 上调即报错；本仓不用 DbFirst，保持原样。

## 升级上游基线步骤

1. 按本目录文件列表从上游同路径复制新版源码（不要覆盖 `*.csproj` / 本 `README.md`）。
2. 重新应用上面 13 组补丁（均以 `NovaDb` 关键字注释标记，`grep -rn NovaDb` 即可列出）。
3. 跑 `tests/WebdavSharp.Tests` 的 NovaDb 用例（`SyncStructure` 二次空跑 + 全实体 CRUD）。
