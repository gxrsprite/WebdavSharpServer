# NovaDb（默认数据库）

本仓默认库已从 SQLite 切换为
[NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb)
（纯 C# 嵌入式/服务双模数据库，关系 + 时序 + MQ + KV 四引擎）。
实测基线：`NewLife.NovaDb 1.0.2026.901`，嵌入模式。
FreeSql 经本仓改写的 `CustomMySql` 方言分支接入，
补丁清单见 [`src/FreeSql.Provider.Custom/README.md`](../src/FreeSql.Provider.Custom/README.md）。

## 为什么换

| 维度 | SQLite（旧默认） | NovaDb（新默认） |
|---|---|---|
| 原生依赖 | `e_sqlite3` 各 RID 原生库（`Batteries_V2.Init()`，fail fast） | **纯托管，零原生依赖**，arm64 / musl / 受限环境开箱即用 |
| 存储形态 | 单文件 `*.db` | **目录即库**（每表 `.data/.idx/.wal` 文件组） |
| 备份/迁移 | 拷文件（写时需注意 WAL） | **拷目录**，无 dump/restore |
| 并发模型 | 文件锁，写串行 | MVCC（Read Committed），读写分离友好 |
| 网络模式 | 无（SQLite 专属） | 同一套 `NovaConnection` 切 `Server=` 即走 TCP，未来多实例可切服务模式 |
| 成熟度 | 久经考验 | **v1 新引擎**，SQL 仅常用子集（本仓用量已全覆盖实测） |
| ORM | 官方 `SqliteCore` provider | 本仓分支（二三十处小补丁，有单测锁定） |

一句话：用“新引擎的小补丁成本”换“零原生依赖 + 目录即库 + 未来的服务模式”。
`Sqlite` / `PostgreSQL` / `MySql` 仍可切回去（见下）。

## 接入方式

```csharp
// Program.cs BuildNovaDb：DataType 复用 CustomMySql（MySQL 方言最接近 Nova），
// ADO.NET 层换成 NovaConnection，DbProviderFactory 换成 NovaClientFactory。
var fsql = new FreeSqlBuilder()
    .UseConnectionFactory(DataType.CustomMySql, () => new NovaConnection("Data Source=LocalPaths/novadb"))
    .UseAutoSyncStructure(dav.AutoSyncStructure)
    .Build();
fsql.SetDbProviderFactory(NovaClientFactory.Instance);
```

- `AssemblyName` 必须保持 `FreeSql.Provider.Custom`（FreeSql 内核按该程序集名反射
  加载 `CustomMySqlProvider`），所以**不要**再引用官方 `FreeSql.Provider.Custom` 包。
- 实体零改动（`[Table]/[Column]/[Navigate]` 照旧），仅 `DavUserRole` 由复合主键改为
  代理主键 + 唯一索引（Nova 不支持 `PRIMARY KEY (a,b)`，见下）。

## 配置

```json
{ "Dav": {
    "Database": "NovaDb",
    "ConnectionString": "Data Source=LocalPaths/novadb",
    "AutoSyncStructure": true
} }
```

注意 `Data Source` 是**目录**（相对路径锚到 ContentRoot，启动日志打印绝对路径）。
切回 SQLite：`Database=Sqlite` + `Data Source=LocalPaths/webdavsharp.db`
（仍走 `FreeSql.Provider.SqliteCore` + `Batteries_V2.Init()`，代码保留双轨）。
生产 PG/MySQL 不变。

## SQL 兼容矩阵（实测，11 轮探针）

FreeSql（本仓分支）发射 → Nova 执行：

| 形状 | 结论 |
|---|---|
| `SELECT/WHERE/ORDER BY/GROUP BY/HAVING/LIMIT n OFFSET m`、反引号/双引号标识符、`@param` 参数 | ✅ |
| `INSERT/UPDATE/DELETE`（内联字面量）、`ON DUPLICATE KEY UPDATE` | ✅ |
| `COUNT/SUM/AVG/MIN/MAX`、`INNER/LEFT/RIGHT JOIN`、子查询 `IN/EXISTS` | ✅ |
| `LIKE`（大小写不敏感）、`INSTR`、`CONCAT`、`IFNULL/COALESCE`、`CASE WHEN`、`CAST` | ✅ |
| `CREATE TABLE`（`varchar/int(11)/char(36)/datetime(3)`、 backtick、COMMENT、`Engine=InnoDB`、`AUTO_INCREMENT`） | ✅（分支已剥离行内索引） |
| `ALTER TABLE ADD/MODIFY COLUMN/COMMENT`、`CREATE/DROP INDEX x ON t`、`TRUNCATE` | ✅ |
| `NOW()/YEAR()/DATEADD()/DATE_FORMAT()/LAST_INSERT_ID()` 等 60+ 函数 | ✅（分支重写了 `date_add/char_length/locate` 等） |
| `limit m,n` 逗号分页 | ❌ Nova 拒收 → 分支改写为 `LIMIT n OFFSET m` |
| `` `库`.`表` `` 两段限定 | ❌ Nova 拒收 → 分支直接去限定符（单库，无损） |
| `BIT` 类型 | ❌ → 分支 bool 映射改 `BOOL` |
| `bool列 = 1` | ❌（Nova 不做 bool↔数字隐转）→ 分支内联 `TRUE/FALSE` |
| 复合主键 `PRIMARY KEY (a,b)`、表内 `UNIQUE INDEX` | ❌ → `DavUserRole` 改代理主键；分支剥离行内索引转独立 `CREATE INDEX` |
| 系统视图单引号别名（`'xxx'`） | ❌（解析 quirk）→ 内省 SQL 已去别名 |
| `utc_timestamp()`、`FOR UPDATE`、`CHANGE COLUMN`、`RENAME TABLE`、三参数 `IndexOf` | ❌ 用到即 loud fail（本仓查询已全部规避） |
| 反斜杠字面量加倍（MySQL 式 `\\`） | ❌（Nova 无此转义）→ 分支已去掉，否则 Windows 路径入库翻倍 |

## 实体变更

- `DavUserRole`：复合主键 → `Id Guid` 代理主键 +
  `[Index("ux_dav_user_role", "UserId,RoleId", true)]`。
  所有用法都是按列查/删/增，对调用方透明；`ManyToMany` 导航不受影响。
- 其余实体：**无变更**。枚举（`DavRuleKind`）按 MySQL 语义存字符串
  （`enum('Path','Regex')` → Nova `String`），SQLite 时代是存 int；
  旧库数据不迁移（见下），新库无影响。

## 运维差异

- **备份**：停服拷 `LocalPaths/novadb/` 整个目录（运行中拷可能拿到 WAL 半截，
  与 SQLite 在线备份一个道理）。
- **SQLite → Nova 数据迁移**：不做自动迁移。旧 `webdavsharp.db` 留着能回滚；
  新库首次启动自动建表 + 种子 admin（`Dav:Seed`）。开发期数据 chaining 的
  `LocalPaths/dav-root` 文件根不受影响。
- **原生库**：Nova 分支不再 `Batteries_V2.Init()`，也不再需要
  `SQLitePCLRaw.bundle_e_sqlite3`（包仍保留，切回 Sqlite 时用）。
- **WAL**：默认 `NORMAL`（1s 刷盘）；金融级可 `;WalMode=Full`（性能换安全）。
- **网络模式**（未启用）：`Server=127.0.0.1;Port=3306;Database=mydb` + 独立
  `NovaServer` 进程；嵌入/网络同一套 `NovaConnection` API，切换成本低。

## 回滚

`appsettings.local.json`（不入库）覆盖即可：

```json
{ "Dav": { "Database": "Sqlite", "ConnectionString": "Data Source=LocalPaths/webdavsharp.db" } }
```

## 上游缺口与建议（按严重性分级，均有最小复现）

### 一、硬缺口：改 SQL 也绕不过去（真正需要上游修的 issue）

1. `CHANGE COLUMN` / `RENAME TABLE` —— DDL 改名迁移做不到，CodeFirst 改列/改表名直接 loud fail。
2. `FOR UPDATE` —— 不支持，解析即报错，没有行锁语义。
3. 三参数 `IndexOf` —— 直接抛 `NotSupportedException`。
4. 自增列元数据 `information_schema.columns.EXTRA` 恒空 —— 拿不到 `AUTO_INCREMENT` 标记（本仓实体无自增列，暂不受影响）。
5. `DbFirst` 反向工程 —— 内省 SQL 含 `LOCATE` 等，在 Nova 上报错，本仓分支未移植验证（本仓不用 DbFirst）。

### 二、行为差异：本仓已绕过，但属引擎行为缺陷（值得报）

6. `CompareValues` 的 bool↔数字隐转 —— `bool列 = 1` 恒 false，非常反直觉（本仓内联 `TRUE/FALSE` 绕过）。
7. 系统视图单引号别名 `'xxx'` —— 报 “without a row context”（本仓去掉别名绕过）。
8. 反斜杠字面量 —— Nova 只有 `''` 转义，无 MySQL 的 `\\` 加倍（本仓已去掉加倍，否则 Windows 路径入库出错）。
9. `LENGTH()` 按字符计（MySQL 按字节）—— 中文 `Length` 查询语义不同（本仓暂无此类查询）。

### 三、建议级：语法糖/兼容性，本仓已打补丁绕过（提给上游只为少打补丁、生态更友好）

10. `BIT` 类型别名（`bit(1)` → Boolean），FreeSql/主流 ORM 开箱即用。
11. `LIMIT m,n` 逗号分页（MySQL 兼容）。
12. 容忍 `` `库`.`表` `` / ``.`表` `` 空限定符（单库直接忽略库名）。
13. `LOCATE()` / `CHAR_LENGTH()` 别名（已有 `INSTR()` / `LENGTH()`）。
14. `UTC_TIMESTAMP()`（可用 `NOW()` + 客户端换算）。

各条补丁位置见 [`src/FreeSql.Provider.Custom/README.md`](../src/FreeSql.Provider.Custom/README.md) 的 `NOVA:` 标记。
