# NovaDb（默认数据库）

本仓默认库是
[NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb)
（纯 C# 嵌入式/服务双模数据库，关系 + 时序 + MQ + KV 四引擎）。
实测基线：**`NewLife.NovaDb 1.0.2026.1120-beta1409`**（2026-11-20 构建），嵌入模式。
FreeSql 经本仓改写的 `CustomMySql` 方言分支接入，
补丁清单见 [`src/FreeSql.Provider.Custom/README.md`](../src/FreeSql.Provider.Custom/README.md)。

> 版本选择：1024 稳定版不含 MySQL 方言兼容修复；1120-beta 才包含 master 上
> 11/20 那批提交（`LIMIT m,n`、`库.表`、BIT、`LOCATE/CHAR_LENGTH`、
> `CHANGE COLUMN/RENAME TABLE`、内联索引、bool 0/1、规范类型名等），
> 这些直接消掉了本仓大部分补丁。等上游出稳定版后换版本号重跑测试即可。

## 为什么用 NovaDb

| 维度 | SQLite（旧默认） | NovaDb |
|---|---|---|
| 原生依赖 | `e_sqlite3` 各 RID 原生库（`Batteries_V2.Init()`，fail fast） | **纯托管，零原生依赖**，arm64 / musl / 受限环境开箱即用 |
| 存储形态 | 单文件 `*.db` | **目录即库**（每表 `.data/.idx/.wal` 文件组） |
| 备份/迁移 | 拷文件（写时需注意 WAL） | **拷目录**，无 dump/restore |
| 并发模型 | 文件锁，写串行 | MVCC（Read Committed），读写分离友好 |
| 网络模式 | 无（SQLite 专属） | 同一套 `NovaConnection` 切 `Server=` 即走 TCP，未来多实例可切服务模式 |
| 成熟度 | 久经考验 | **v1 新引擎**，SQL 仅常用子集（本仓用量已全覆盖实测） |
| ORM | 官方 `SqliteCore` provider | 本仓分支（6 组补丁，有单测锁定） |

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

## SQL 兼容矩阵（1120-beta 实测）

FreeSql（本仓分支）发射 → Nova 执行：

| 形状 | 结论 |
|---|---|
| `SELECT/WHERE/ORDER BY/GROUP BY/HAVING`、反引号/双引号标识符、`@param` 参数 | ✅ |
| `LIMIT n`、`LIMIT n OFFSET m`、**`LIMIT m,n` 逗号分页** | ✅（1120 起上游支持逗号形式，分支已恢复原样生成） |
| `` `库`.`表` `` 与 `` .`表` `` 限定 | ✅（1120 起上游兼容，分支已删去限定符补丁） |
| `INSERT/UPDATE/DELETE`（内联字面量）、`ON DUPLICATE KEY UPDATE`、多语句 | ✅ |
| `COUNT/SUM/AVG/MIN/MAX`、`INNER/LEFT/RIGHT JOIN`、子查询 `IN/EXISTS` | ✅ |
| `LIKE`、`INSTR`、**`LOCATE`（含三参数）**、**`CHAR_LENGTH`**、`CONCAT`、`IFNULL/COALESCE`、`CASE WHEN`、`CAST` | ✅ |
| **`BIT(1)` 布尔列**、列级 `UNIQUE`、`UNSIGNED/COLLATE/ON UPDATE` 修饰符、索引前缀长度 | ✅（1120 起） |
| `CREATE TABLE`（`varchar/int/char/datetime`、backtick、COMMENT、`Engine=InnoDB`、`AUTO_INCREMENT`、**内联 `INDEX/UNIQUE INDEX`**） | ✅（1120 起内联索引上游原生支持） |
| `ALTER TABLE ADD/MODIFY/**CHANGE** COLUMN`、`RENAME TABLE`、`ALTER TABLE … RENAME`、`CREATE/DROP INDEX`、`TRUNCATE` | ✅（`CHANGE`/`RENAME` 1120 起） |
| `NOW()/YEAR()/DATEADD()/DATE_FORMAT()/LAST_INSERT_ID()` 等 60+ 函数 | ✅ |
| **`UTC_TIMESTAMP()`**、**`FOR UPDATE`**、`information_schema.columns.EXTRA=auto_increment` | ✅（1120 起） |
| **列长度约束**：`STRING(n)` 的 n 是真实约束，超长抛 `ConstraintViolation` | ⚠️ 行为变化：实体 `[Column(StringLength=…)]` 现在真的生效 |
| `bool列 = 1` / `= '1'` | ✅（1120 起按 0/1 语义比较，分支已恢复内联 1/0） |
| **复合主键 `PRIMARY KEY (a,b)`** | ❌ 上游明确报“不支持复合主键，请改用单列代理主键” |
| `DATE_ADD(col, INTERVAL n unit)`、`CURDATE()`、三参数 `TIMESTAMPDIFF` | ❌ → 分支改写为 `DATEADD`/`DATE_FORMAT`/`WEEKDAY` 等价 |
| `CREATE INDEX IF NOT EXISTS`、重复 `CREATE INDEX` | ❌ → 分支按“已存在”精确吞错（幂等） |
| `DbFirst`（反向工程） | ❌ 未移植验证（本仓不用） |

## SELECT 性能现状（1120-beta 源码核实）

**没有针对 SELECT 路径的性能优化**，这是当前 NovaDb 的核心性能边界：

- `SqlEngine.Select.cs` 中所有单表查询一律走 `table.GetAll(tx)`（line 42/158），
  JOIN 也是 `GetAll` 两端（line 538/562）——**WHERE 条件是查全表后在内存里
  `rows.Where(...)` 过滤**（line 47），不使用索引。
- `NovaTable.GetAll()` 会遍历主键 SkipList 的**全部条目并逐行反序列化**
  （`NovaTable.cs` line 537-563），所以查询成本 ≈ O(表行数)，与是否命中索引无关。
- 引擎层其实**有** `NovaTable.LookupByIndex(indexName, indexKeyValues)`（line 658）
  等索引查找 API，但 SQL 层没有调用；建了二级索引（`DavUserRole` 的唯一索引等）
  对 SELECT 没有任何加速，只在写时维护唯一性。
- 1024→1120 的提交里没有 SELECT 性能优化（都是方言兼容/窗口函数/类型名）；
  上游的性能提交（`516d544` 序列化持久化、`13b4aa6`/`765fab4` Lock 优化、
  `84e7e87` Decimal 编码）都不在 SELECT 查询路径上。

**对本仓的影响与对策**：本仓是配置型数据（用户/角色/规则/挂载/服务设置），
单表行数在几十到几百级，全表扫描的实际开销可忽略，无需改架构。若未来
`DavAuditLog`（审计日志）增长到万行级，管理后台的分页/筛选会退化为全表
反序列化+内存过滤，届时的选择：
1. 保持 NovaDb，用审计保留天数（`Dav:AuditRetainDays`）控制行数上限；
2. 审计改走单独的 SQLite/Server 模式库（本仓已保留 FreeSql 多库切换能力）；
3. 绕开 SQL 直接用 `NovaTable.LookupByIndex` —— 需要本仓分支改造适配层，成本高，
   仅在审计确实成为瓶颈时才考虑。

## 实体变更

- `DavUserRole`：复合主键 → `Id Guid` 代理主键 +
  `[Index("ux_dav_user_role", "UserId,RoleId", true)]`。
  所有用法都是按列查/删/增，对调用方透明；`ManyToMany` 导航不受影响。
- 其余实体：**无变更**。枚举（`DavRuleKind`）按 MySQL 语义存字符串
  （`enum('Path','Regex')` → Nova `String`），SQLite 时代是存 int；
  旧库数据不迁移（见下），新库无影响。

## 1120-beta 相对 901 的上游变更（与本仓相关性）

除了上面 SQL 兼容矩阵里的方言修复，还有几处会影响本仓，已实测确认：

| 上游变更 | 对本仓的影响 |
|---|---|
| `d98c996` 统一存储与协议字段的小端字节序 | **旧库兼容性**：用 901 建的 `LocalPaths/novadb` 在新版上直接可读（实测审计清理读到 6 条旧记录）；新写入按小端 |
| `b4614db` / `b145259` / `54e5c79` SQL 数值、日期、标识符改用固定区域性 | 修掉土耳其等区域的 `i/I`、数字格式化问题；本仓 SQL 生成与解析因此区域无关 |
| `76f4795` 删表时清理行日志（rlog）文件 | 反复建删表不再残留 `.rlog` 文件；CodeFirst 回归更干净 |
| `26be89d` / `742eb2b` 规范类型名 + 列长度真实约束 | 见上文「SQL 兼容矩阵」与补丁 1；实体 `StringLength` 真正生效 |
| `11eb9b9` 新增 `net11.0` 目标框架 | 本仓仍用 `net10.0`，无影响（包同时提供 `net10.0`/`net8.0` 资产） |
| 1024 稳定版的本地化/时序引擎/NativeAOT/GetSchemaTable | 与本仓 ORM 用量无关；`NovaDataReader.GetSchemaTable` 也可用（本仓走 FreeSql 内省，不依赖） |

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

## 上游明确不支持（已知边界）

- **复合主键**：上游明确报「不支持复合主键，请改用单列代理主键」，`DavUserRole` 已用代理主键 + 唯一索引绕开。
- `DATE_ADD(col, INTERVAL n unit)`：只认 `DATEADD('UNIT', n, col)`，本仓分支已改写。
- `CURDATE()`：本仓分支用 `DATE_FORMAT(NOW(),'%Y-%m-%d')` 等价实现。
- 三参数 `TIMESTAMPDIFF`：本仓分支未使用。
- `CREATE INDEX IF NOT EXISTS`：建索引重复执行报「已存在」，本仓分支按错误文本幂等吞掉。

> 已解决（1120-beta 起）：`BIT` 布尔、`LIMIT m,n`、`库.表`、`LOCATE`、
> `CHAR_LENGTH`、`UTC_TIMESTAMP`、`CHANGE COLUMN`、`RENAME TABLE`、
> 内联索引、列级 `UNIQUE`、bool 0/1 隐转、系统视图单引号别名、
> `information_schema.Extra`。

各条补丁位置见 [`src/FreeSql.Provider.Custom/README.md`](../src/FreeSql.Provider.Custom/README.md) 的 `NovaDb` 标记。
