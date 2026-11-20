using FreeSql.Internal;
using FreeSql.Internal.Model;
using FreeSql.Internal.ObjectPool;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeSql.Custom.MySql
{

    class CustomMySqlCodeFirst : Internal.CommonProvider.CodeFirstProvider
    {
        public override bool IsNoneCommandParameter { get => true; set => base.IsNoneCommandParameter = true; }
        public CustomMySqlCodeFirst(IFreeSql orm, CommonUtils commonUtils, CommonExpression commonExpression) : base(orm, commonUtils, commonExpression) { }

        static object _dicCsToDbLock = new object();
        // NovaDb 1.0.2026.1120 起解析层把 BIT 映射为布尔（上游 3951087），
        // 恢复上游 MySql 方言的 bit(1) 写法；服务端规范名上报 bool，
        // 比对由 NovaDbTypeEquals 的 bool 家族负责。
        static Dictionary<string, CsToDb<DbType>> _dicCsToDb = new Dictionary<string, CsToDb<DbType>>() {
                { typeof(bool).FullName, CsToDb.New(DbType.Boolean, "bit","bit(1) NOT NULL", null, false, false) },{ typeof(bool?).FullName, CsToDb.New(DbType.Boolean, "bit","bit(1)", null, true, null) },

                { typeof(sbyte).FullName, CsToDb.New(DbType.SByte, "tinyint", "tinyint(3) NOT NULL", false, false, 0) },{ typeof(sbyte?).FullName, CsToDb.New(DbType.SByte, "tinyint", "tinyint(3)", false, true, null) },
                { typeof(short).FullName, CsToDb.New(DbType.Int16, "smallint","smallint(6) NOT NULL", false, false, 0) },{ typeof(short?).FullName, CsToDb.New(DbType.Int16, "smallint", "smallint(6)", false, true, null) },
                { typeof(int).FullName, CsToDb.New(DbType.Int32, "int", "int(11) NOT NULL", false, false, 0) },{ typeof(int?).FullName, CsToDb.New(DbType.Int32, "int", "int(11)", false, true, null) },
                { typeof(long).FullName, CsToDb.New(DbType.Int64, "bigint","bigint(20) NOT NULL", false, false, 0) },{ typeof(long?).FullName, CsToDb.New(DbType.Int64, "bigint","bigint(20)", false, true, null) },

                { typeof(byte).FullName, CsToDb.New(DbType.Byte, "tinyint","tinyint(3) unsigned NOT NULL", true, false, 0) },{ typeof(byte?).FullName, CsToDb.New(DbType.Byte, "tinyint","tinyint(3) unsigned", true, true, null) },
                { typeof(ushort).FullName, CsToDb.New(DbType.UInt16, "smallint","smallint(5) unsigned NOT NULL", true, false, 0) },{ typeof(ushort?).FullName, CsToDb.New(DbType.UInt16, "smallint", "smallint(5) unsigned", true, true, null) },
                { typeof(uint).FullName, CsToDb.New(DbType.UInt32, "int", "int(10) unsigned NOT NULL", true, false, 0) },{ typeof(uint?).FullName, CsToDb.New(DbType.UInt32, "int", "int(10) unsigned", true, true, null) },
                { typeof(ulong).FullName, CsToDb.New(DbType.UInt64, "bigint", "bigint(20) unsigned NOT NULL", true, false, 0) },{ typeof(ulong?).FullName, CsToDb.New(DbType.UInt64, "bigint", "bigint(20) unsigned", true, true, null) },

                { typeof(double).FullName, CsToDb.New(DbType.Double, "double", "double NOT NULL", false, false, 0) },{ typeof(double?).FullName, CsToDb.New(DbType.Double, "double", "double", false, true, null) },
                { typeof(float).FullName, CsToDb.New(DbType.Single, "float","float NOT NULL", false, false, 0) },{ typeof(float?).FullName, CsToDb.New(DbType.Single, "float","float", false, true, null) },
                { typeof(decimal).FullName, CsToDb.New(DbType.Decimal, "decimal", "decimal(10,2) NOT NULL", false, false, 0) },{ typeof(decimal?).FullName, CsToDb.New(DbType.Decimal, "decimal", "decimal(10,2)", false, true, null) },

                { typeof(TimeSpan).FullName, CsToDb.New(DbType.Time, "time","time NOT NULL", false, false, 0) },{ typeof(TimeSpan?).FullName, CsToDb.New(DbType.Time, "time", "time",false, true, null) },
                { typeof(DateTime).FullName, CsToDb.New(DbType.DateTime, "datetime(3)", "datetime(3) NOT NULL", false, false, new DateTime(1970,1,1)) },{ typeof(DateTime?).FullName, CsToDb.New(DbType.DateTime, "datetime(3)", "datetime(3)", false, true, null) },

                { typeof(byte[]).FullName, CsToDb.New(DbType.Binary, "varbinary", "varbinary(255)", false, null, new byte[0]) },
                { typeof(string).FullName, CsToDb.New(DbType.String, "varchar", "varchar(255)", false, null, "") },
                { typeof(char).FullName, CsToDb.New(DbType.AnsiString, "char", "char(1) NULL", false, null, '\0') },

                { typeof(Guid).FullName, CsToDb.New(DbType.Guid, "char", "char(36) NOT NULL", false, false, Guid.Empty) },{ typeof(Guid?).FullName, CsToDb.New(DbType.Guid, "char", "char(36)", false, true, null) },
            };

        public override DbInfoResult GetDbInfo(Type type)
        {
            if (_dicCsToDb.TryGetValue(type.FullName, out var trydc)) return new DbInfoResult((int)trydc.type, trydc.dbtype, trydc.dbtypeFull, trydc.isnullable, trydc.defaultValue);
            if (type.IsArray) return null;
            var enumType = type.IsEnum ? type : null;
            if (enumType == null && type.IsNullableType())
            {
                var genericTypes = type.GetGenericArguments();
                if (genericTypes.Length == 1 && genericTypes.First().IsEnum) enumType = genericTypes.First();
            }
            if (enumType != null)
            {
                var names = string.Join(",", Enum.GetNames(enumType).Select(a => _commonUtils.FormatSql("{0}", a)));
                var newItem = enumType.GetCustomAttributes(typeof(FlagsAttribute), false).Any() ?
                    CsToDb.New(DbType.String, "set", $"set({names}){(type.IsEnum ? " NOT NULL" : "")}", false, type.IsEnum ? false : true, enumType.CreateInstanceGetDefaultValue()) :
                    CsToDb.New(DbType.String, "enum", $"enum({names}){(type.IsEnum ? " NOT NULL" : "")}", false, type.IsEnum ? false : true, enumType.CreateInstanceGetDefaultValue());
                if (_dicCsToDb.ContainsKey(type.FullName) == false)
                {
                    lock (_dicCsToDbLock)
                    {
                        if (_dicCsToDb.ContainsKey(type.FullName) == false)
                            _dicCsToDb.Add(type.FullName, newItem);
                    }
                }
                return new DbInfoResult((int)newItem.type, newItem.dbtype, newItem.dbtypeFull, newItem.isnullable, newItem.defaultValue);
            }
            return null;
        }

        protected override string GetComparisonDDLStatements(params TypeSchemaAndName[] objects)
        {
            Object<DbConnection> conn = null;
            string database = null;
            
            try
            {
                conn = _orm.Ado.MasterPool.Get(TimeSpan.FromSeconds(5));
                database = conn.Value.Database;

                // NovaDb：嵌入模式连接串通常只有 Data Source（目录），Database 为空，
                // 而 information_schema 按 TABLE_SCHEMA（= 目录名）过滤，空库名永远查不到表，
                // 会导致每次同步都误判“表不存在”。用 SELECT DATABASE() 回填当前库名
                // （Nova 原生支持，返回数据目录名）。
                if (string.IsNullOrEmpty(database))
                {
                    try
                    {
                        using (var cmd = conn.Value.CreateCommand())
                        {
                            cmd.CommandText = "SELECT DATABASE()";
                            cmd.CommandType = CommandType.Text;
                            database = string.Concat(cmd.ExecuteScalar());
                        }
                    }
                    catch { }
                }

                var sb = new StringBuilder();
                foreach (var obj in objects)
                {
                    if (sb.Length > 0) sb.Append("\r\n");
                    var tb = obj.tableSchema;
                    if (tb == null) throw new Exception(CoreErrorStrings.S_Type_IsNot_Migrable(obj.tableSchema.Type.FullName));
                    if (tb.Columns.Any() == false) throw new Exception(CoreErrorStrings.S_Type_IsNot_Migrable_0Attributes(obj.tableSchema.Type.FullName));
                    var tbname = _commonUtils.SplitTableName(tb.DbName);
                    if (tbname?.Length == 1) tbname = new[] { database, tbname[0] };

                    var tboldname = _commonUtils.SplitTableName(tb.DbOldName); //旧表名
                    if (tboldname?.Length == 1) tboldname = new[] { database, tboldname[0] };
                    if (string.IsNullOrEmpty(obj.tableName) == false)
                    {
                        var tbtmpname = _commonUtils.SplitTableName(obj.tableName);
                        if (tbtmpname?.Length == 1) tbtmpname = new[] { database, tbtmpname[0] };
                        if (tbname[0] != tbtmpname[0] || tbname[1] != tbtmpname[1])
                        {
                            tbname = tbtmpname;
                            tboldname = null;
                        }
                    }

                    if (string.Compare(tbname[0], database, true) != 0 && LocalExecuteScalar(database, _commonUtils.FormatSql(" select 1 from information_schema.schemata where schema_name={0}", tbname[0])) == null) //创建数据库
                        sb.Append($"CREATE DATABASE IF NOT EXISTS ").Append(_commonUtils.QuoteSqlName(tbname[0])).Append(" default charset utf8 COLLATE utf8_general_ci;\r\n");

                    var sbalter = new StringBuilder();
                    var istmpatler = false; //创建临时表，导入数据，删除旧表，修改
                    if (LocalExecuteScalar(tbname[0], _commonUtils.FormatSql(" SELECT 1 FROM information_schema.TABLES WHERE table_schema={0} and table_name={1}", tbname)) == null)
                    { //表不存在
                        if (tboldname != null)
                        {
                            if (string.Compare(tboldname[0], tbname[0], true) != 0 && LocalExecuteScalar(database, _commonUtils.FormatSql(" select 1 from information_schema.schemata where schema_name={0}", tboldname[0])) == null ||
                                LocalExecuteScalar(tboldname[0], _commonUtils.FormatSql(" SELECT 1 FROM information_schema.TABLES WHERE table_schema={0} and table_name={1}", tboldname)) == null)
                                //数据库或表不存在
                                tboldname = null;
                        }
                        if (tboldname == null)
                        {
                            //创建表
                            var createTableName = _commonUtils.QuoteSqlName(tbname[0], tbname[1]);
                            sb.Append("CREATE TABLE IF NOT EXISTS ").Append(createTableName).Append(" ( ");
                            foreach (var tbcol in tb.ColumnsByPosition)
                            {
                                sb.Append(" \r\n  ").Append(_commonUtils.QuoteSqlName(tbcol.Attribute.Name)).Append(" ").Append(NovaPrimaryDbType(tb, tbcol));
                                if (tbcol.Attribute.IsIdentity == true && tbcol.Attribute.DbType.IndexOf("AUTO_INCREMENT", StringComparison.CurrentCultureIgnoreCase) == -1) sb.Append(" AUTO_INCREMENT");
                                if (string.IsNullOrEmpty(tbcol.Comment) == false) sb.Append(" COMMENT ").Append(_commonUtils.FormatSql("{0}", tbcol.Comment));
                                sb.Append(",");
                            }
                            if (tb.Primarys.Any())
                            {
                                sb.Append(" \r\n  PRIMARY KEY (");
                                foreach (var tbcol in tb.Primarys) sb.Append(_commonUtils.QuoteSqlName(tbcol.Attribute.Name)).Append(", ");
                                sb.Remove(sb.Length - 2, 2).Append("),");
                            }
                            //创建表的索引，感谢 @mafeng8，这样写可以支持自增不是主键的情况
                            foreach (var uk in tb.Indexes)
                            {
                                sb.Append(" \r\n  ");
                                if (uk.IsUnique) sb.Append("UNIQUE ");
                                sb.Append("INDEX ").Append(_commonUtils.QuoteSqlName(ReplaceIndexName(uk.Name, tbname[1]))).Append("(");
                                foreach (var tbcol in uk.Columns)
                                {
                                    sb.Append(_commonUtils.QuoteSqlName(tbcol.Column.Attribute.Name));
                                    if (tbcol.IsDesc) sb.Append(" DESC");
                                    sb.Append(", ");
                                }
                                sb.Remove(sb.Length - 2, 2).Append("),");
                            }
                            sb.Remove(sb.Length - 1, 1);
                            sb.Append("\r\n) Engine=InnoDB");
                            if (string.IsNullOrEmpty(tb.Comment) == false)
                                sb.Append(" Comment=").Append(_commonUtils.FormatSql("{0}", tb.Comment));
                            sb.Append(";\r\n");
                            continue;
                        }
                        //如果新表，旧表在一个数据库下，直接修改表名
                        if (string.Compare(tbname[0], tboldname[0], true) == 0)
                            sbalter.Append("ALTER TABLE ").Append(_commonUtils.QuoteSqlName(tboldname[0], tboldname[1])).Append(" RENAME TO ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append(";\r\n");
                        else
                        {
                            //如果新表，旧表不在一起，创建新表，导入数据，删除旧表
                            istmpatler = true;
                        }
                    }
                    else
                        tboldname = null; //如果新表已经存在，不走改表名逻辑

                    //对比字段，只可以修改类型、增加字段、有限的修改字段名；保证安全不删除字段
                    // NovaDb 1.0.2026.1120 起 information_schema.columns 的 EXTRA 会输出
                    // auto_increment（上游 fc2b74f），恢复上游 MySQL 的 is_identity 判断。
                    var sql = _commonUtils.FormatSql(@"
select
a.COLUMN_NAME,
a.COLUMN_TYPE,
case when a.IS_NULLABLE = 'YES' then 1 else 0 end,
case when a.EXTRA like '%auto_increment%' then 1 else 0 end,
a.COLUMN_COMMENT,
a.COLUMN_KEY
from information_schema.columns a
where a.TABLE_SCHEMA in ({0}) and a.TABLE_NAME in ({1})", tboldname ?? tbname);
                    var ds = _orm.Ado.ExecuteArray(CommandType.Text, sql);
                    var tbstruct = ds.ToDictionary(a => string.Concat(a[0]), a =>
                    {
                        var a1 = string.Concat(a[1]);
                        if (a1 == "datetime") a1 = string.Concat(a1, "(0)");
                        return new
                        {
                            column = string.Concat(a[0]),
                            sqlType = a1,
                            is_nullable = string.Concat(a[2]) == "1",
                            is_identity = string.Concat(a[3]) == "1",
                            is_unsigned = string.Concat(a[1]).EndsWith(" unsigned"),
                            comment = string.Concat(a[4]),
                            is_primary = string.Concat(a[5]) == "PRI",
                        };
                    }, StringComparer.CurrentCultureIgnoreCase);

                    if (istmpatler == false)
                    {
                        var csPrimarys = tb.Primarys.Select(a => a.Attribute.Name).ToArray();
                        var dbPrimarys = tbstruct.Where(a => a.Value.is_primary).Select(a => a.Key).ToArray();
                        if (!csPrimarys.Any() && dbPrimarys.Any()) sbalter.Append("ALTER TABLE ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append(" DROP PRIMARY KEY").Append(";\r\n");
                        if (csPrimarys.Any() && !dbPrimarys.Any()) sbalter.Append("ALTER TABLE ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append(" ADD PRIMARY KEY(").Append(string.Join(", ", tb.Primarys.Select(tbcol => _commonUtils.QuoteSqlName(tbcol.Attribute.Name)))).Append(")").Append(";\r\n");
                        else if (csPrimarys.Any() && (csPrimarys.Length != dbPrimarys.Length || csPrimarys.Except(dbPrimarys, StringComparer.CurrentCultureIgnoreCase).Any())) sbalter.Append("ALTER TABLE ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append(" DROP PRIMARY KEY, ADD PRIMARY KEY(").Append(string.Join(", ", tb.Primarys.Select(tbcol => _commonUtils.QuoteSqlName(tbcol.Attribute.Name)))).Append(")").Append(";\r\n");

                        foreach (var tbcol in tb.ColumnsByPosition)
                        {
                            if (tbstruct.TryGetValue(tbcol.Attribute.Name, out var tbstructcol) ||
                                string.IsNullOrEmpty(tbcol.Attribute.OldName) == false && tbstruct.TryGetValue(tbcol.Attribute.OldName, out tbstructcol))
                            {
                                var isCommentChanged = tbstructcol.comment != (tbcol.Comment ?? "");
                                var isDbTypeChanged = tbcol.Attribute.DbType.StartsWith(tbstructcol.sqlType, StringComparison.CurrentCultureIgnoreCase) == false;
                                // NovaDb：服务端上报归一化类型（bool→tinyint(1)、varchar(50)→varchar(255)、
                                // enum(…)→varchar(255)、datetime(3)→datetime），先按基类型家族判等；
                                // 命中则直接视为一致，跳过下面整条上游细化链——否则 tinyint/varchar
                                // 分支会把归一结果又覆盖回“变更”，导致每次同步都误报、无限 MODIFY。
                                if (isDbTypeChanged && NovaDbTypeEquals(tbcol.Attribute.DbType, tbstructcol.sqlType))
                                    isDbTypeChanged = false;
                                else if (isDbTypeChanged)
                                {
                                if (tbstructcol.sqlType == "datetime(0)" && Regex.IsMatch(tbcol.Attribute.DbType, @"datetime\s*\(", RegexOptions.IgnoreCase) == false)
                                    isDbTypeChanged = tbcol.Attribute.DbType.StartsWith("datetime", StringComparison.CurrentCultureIgnoreCase) == false;
                                else if (tbstructcol.sqlType.StartsWith("datetime", StringComparison.CurrentCultureIgnoreCase)) isDbTypeChanged = tbcol.Attribute.DbType.StartsWith("datetime", StringComparison.CurrentCultureIgnoreCase) == false ||
                                        (int.TryParse(Regex.Match(tbcol.Attribute.DbType, @"datetime\s*\((\d*)", RegexOptions.IgnoreCase).Groups[1].Value, out var trydtrd) ? trydtrd : 3) !=
                                        (int.TryParse(Regex.Match(tbstructcol.sqlType, @"datetime\s*\((\d*)", RegexOptions.IgnoreCase).Groups[1].Value, out var trydtrd2) ? trydtrd2 : 3);
                                else if (tbstructcol.sqlType.StartsWith("int", StringComparison.CurrentCultureIgnoreCase)) isDbTypeChanged = tbcol.Attribute.DbType.StartsWith("int", StringComparison.CurrentCultureIgnoreCase) == false;
                                else if (tbstructcol.sqlType.StartsWith("tinyint", StringComparison.CurrentCultureIgnoreCase)) isDbTypeChanged = tbcol.Attribute.DbType.StartsWith("tinyint", StringComparison.CurrentCultureIgnoreCase) == false;
                                else if (tbstructcol.sqlType.StartsWith("smallint", StringComparison.CurrentCultureIgnoreCase)) isDbTypeChanged = tbcol.Attribute.DbType.StartsWith("smallint", StringComparison.CurrentCultureIgnoreCase) == false;
                                else if (tbstructcol.sqlType.StartsWith("bigint", StringComparison.CurrentCultureIgnoreCase)) isDbTypeChanged = tbcol.Attribute.DbType.StartsWith("bigint", StringComparison.CurrentCultureIgnoreCase) == false;
                                }

                                if ((tbcol.Attribute.DbType.IndexOf(" unsigned", StringComparison.CurrentCultureIgnoreCase) != -1) != tbstructcol.is_unsigned ||
                                isDbTypeChanged ||
                                tbcol.Attribute.IsNullable != tbstructcol.is_nullable ||
                                tbcol.Attribute.IsIdentity != tbstructcol.is_identity ||
                                isCommentChanged)
                                {
                                    if (tbcol.Attribute.IsNullable != tbstructcol.is_nullable && tbcol.Attribute.IsNullable == false && tbcol.DbDefaultValue != "NULL" && tbcol.Attribute.IsIdentity == false)
                                        sbalter.Append("UPDATE ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append(" SET ").Append(_commonUtils.QuoteSqlName(tbstructcol.column)).Append(" = ").Append(tbcol.DbDefaultValue).Append(" WHERE ").Append(_commonUtils.QuoteSqlName(tbstructcol.column)).Append(" IS NULL;\r\n");
                                    sbalter.Append("ALTER TABLE ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append(" MODIFY ").Append(_commonUtils.QuoteSqlName(tbstructcol.column)).Append(" ").Append(tbcol.Attribute.DbType);
                                    if (string.IsNullOrEmpty(tbcol.Comment) == false) sbalter.Append(" COMMENT ").Append(_commonUtils.FormatSql("{0}", tbcol.Comment ?? ""));
                                    if (tbcol.Attribute.IsIdentity == true && tbcol.Attribute.DbType.IndexOf("AUTO_INCREMENT", StringComparison.CurrentCultureIgnoreCase) == -1) sbalter.Append(" AUTO_INCREMENT");
                                    sbalter.Append(";\r\n");
                                }
                                if (string.Compare(tbstructcol.column, tbcol.Attribute.OldName, true) == 0)
                                {
                                    //修改列名
                                    sbalter.Append("ALTER TABLE ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append(" CHANGE COLUMN ").Append(_commonUtils.QuoteSqlName(tbstructcol.column)).Append(" ").Append(_commonUtils.QuoteSqlName(tbcol.Attribute.Name)).Append(" ").Append(tbcol.Attribute.DbType);
                                    if (string.IsNullOrEmpty(tbcol.Comment) == false) sbalter.Append(" COMMENT ").Append(_commonUtils.FormatSql("{0}", tbcol.Comment ?? ""));
                                    if (tbcol.Attribute.IsIdentity == true && tbcol.Attribute.DbType.IndexOf("AUTO_INCREMENT", StringComparison.CurrentCultureIgnoreCase) == -1) sbalter.Append(" AUTO_INCREMENT");
                                    sbalter.Append(";\r\n");
                                }
                                continue;
                            }
                            //添加列
                            sbalter.Append("ALTER TABLE ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append(" ADD ").Append(_commonUtils.QuoteSqlName(tbcol.Attribute.Name)).Append(" ").Append(tbcol.Attribute.DbType);
                            if (tbcol.Attribute.IsNullable == false && tbcol.DbDefaultValue != "NULL" && tbcol.Attribute.IsIdentity == false) sbalter.Append(" DEFAULT ").Append(tbcol.DbDefaultValue);
                            if (string.IsNullOrEmpty(tbcol.Comment) == false) sbalter.Append(" COMMENT ").Append(_commonUtils.FormatSql("{0}", tbcol.Comment ?? ""));
                            if (tbcol.Attribute.IsIdentity == true && tbcol.Attribute.DbType.IndexOf("AUTO_INCREMENT", StringComparison.CurrentCultureIgnoreCase) == -1) sbalter.Append(" AUTO_INCREMENT");
                            sbalter.Append(";\r\n");
                        }
                        // NovaDb：同上，系统视图查询不用单引号别名（C# 侧按位置取值）。
                        var dsuksql = _commonUtils.FormatSql(@"
select
a.COLUMN_NAME,
a.INDEX_NAME,
case when a.COLLATION = 'D' then 1 else 0 end,
case when a.NON_UNIQUE = 0 then 1 else 0 end
from information_schema.statistics a
where a.TABLE_SCHEMA IN ({0}) and a.TABLE_NAME IN ({1}) and a.INDEX_NAME <> 'PRIMARY'", tboldname ?? tbname);
                        var dsuk = _orm.Ado.ExecuteArray(CommandType.Text, dsuksql).Select(a => new[] { string.Concat(a[0]), string.Concat(a[1]), string.Concat(a[2]), string.Concat(a[3]) });
                        foreach (var uk in tb.Indexes)
                        {
                            if (string.IsNullOrEmpty(uk.Name) || uk.Columns.Any() == false) continue;
                            var ukname = ReplaceIndexName(uk.Name, tbname[1]);
                            var dsukfind1 = dsuk.Where(a => string.Compare(a[1], ukname, true) == 0).ToArray();
                            if (dsukfind1.Any() == false || dsukfind1.Length != uk.Columns.Length || dsukfind1.Where(a => (a[3] == "1") == uk.IsUnique && uk.Columns.Where(b => string.Compare(b.Column.Attribute.Name, a[0], true) == 0 && (a[2] == "1") == b.IsDesc).Any()).Count() != uk.Columns.Length)
                            {
                                if (dsukfind1.Any()) sbalter.Append("DROP INDEX ").Append(_commonUtils.QuoteSqlName(ukname)).Append(" ON ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append(";\r\n");
                                sbalter.Append("CREATE ");
                                if (uk.IsUnique) sbalter.Append("UNIQUE ");
                                sbalter.Append("INDEX ").Append(_commonUtils.QuoteSqlName(ukname)).Append(" ON ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append("(");
                                foreach (var tbcol in uk.Columns)
                                {
                                    sbalter.Append(_commonUtils.QuoteSqlName(tbcol.Column.Attribute.Name));
                                    if (tbcol.IsDesc) sbalter.Append(" DESC");
                                    sbalter.Append(", ");
                                }
                                sbalter.Remove(sbalter.Length - 2, 2).Append(");\r\n");
                            }
                        }
                    }
                    if (istmpatler == false)
                    {
                        var dbcomment = string.Concat(_orm.Ado.ExecuteScalar(CommandType.Text, _commonUtils.FormatSql(@" select table_comment from information_schema.tables where table_schema = {0} and table_name = {1}", tbname[0], tbname[1])));
                        if (dbcomment != (tb.Comment ?? ""))
                            sb.Append("ALTER TABLE ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append(" COMMENT ").Append(" ").Append(_commonUtils.FormatSql("{0}", tb.Comment ?? "")).Append(";\r\n");

                        sb.Insert(0, sbalter);
                        continue;
                    }

                    //创建临时表，数据导进临时表，然后删除原表，将临时表改名为原表名
                    var newtablename = _commonUtils.QuoteSqlName(tbname[0], tbname[1]);
                    var tablename = tboldname == null ? newtablename : _commonUtils.QuoteSqlName(tboldname[0], tboldname[1]);
                    var tmptablename = _commonUtils.QuoteSqlName(tbname[0], $"FreeSqlTmp_{tbname[1]}");
                    //创建临时表
                    sb.Append("CREATE TABLE IF NOT EXISTS ").Append(tmptablename).Append(" ( ");
                    foreach (var tbcol in tb.ColumnsByPosition)
                    {
                        sb.Append(" \r\n  ").Append(_commonUtils.QuoteSqlName(tbcol.Attribute.Name)).Append(" ").Append(NovaPrimaryDbType(tb, tbcol));
                        if (tbcol.Attribute.IsIdentity == true && tbcol.Attribute.DbType.IndexOf("AUTO_INCREMENT", StringComparison.CurrentCultureIgnoreCase) == -1) sb.Append(" AUTO_INCREMENT");
                        if (string.IsNullOrEmpty(tbcol.Comment) == false) sb.Append(" COMMENT ").Append(_commonUtils.FormatSql("{0}", tbcol.Comment));
                        sb.Append(",");
                    }
                    if (tb.Primarys.Any())
                    {
                        sb.Append(" \r\n  PRIMARY KEY (");
                        foreach (var tbcol in tb.Primarys) sb.Append(_commonUtils.QuoteSqlName(tbcol.Attribute.Name)).Append(", ");
                        sb.Remove(sb.Length - 2, 2).Append("),");
                    }
                    sb.Remove(sb.Length - 1, 1);
                    sb.Append("\r\n) Engine=InnoDB");
                    if (string.IsNullOrEmpty(tb.Comment) == false)
                        sb.Append(" Comment=").Append(_commonUtils.FormatSql("{0}", tb.Comment));
                    sb.Append(";\r\n");

                    sb.Append("INSERT INTO ").Append(tmptablename).Append(" (");
                    foreach (var tbcol in tb.ColumnsByPosition)
                        sb.Append(_commonUtils.QuoteSqlName(tbcol.Attribute.Name)).Append(", ");
                    sb.Remove(sb.Length - 2, 2).Append(")\r\nSELECT ");
                    foreach (var tbcol in tb.ColumnsByPosition)
                    {
                        var insertvalue = "NULL";
                        if (tbstruct.TryGetValue(tbcol.Attribute.Name, out var tbstructcol) ||
                            string.IsNullOrEmpty(tbcol.Attribute.OldName) == false && tbstruct.TryGetValue(tbcol.Attribute.OldName, out tbstructcol))
                        {
                            insertvalue = _commonUtils.QuoteSqlName(tbstructcol.column);
                            if (tbcol.Attribute.DbType.StartsWith(tbstructcol.sqlType, StringComparison.CurrentCultureIgnoreCase) == false)
                            {
                                //insertvalue = $"cast({insertvalue} as {tbcol.Attribute.DbType.Split(' ').First()})";
                            }
                            if (tbcol.Attribute.IsNullable != tbstructcol.is_nullable)
                                insertvalue = $"ifnull({insertvalue},{tbcol.DbDefaultValue})";
                        }
                        else if (tbcol.Attribute.IsNullable == false)
                            if (tbcol.DbDefaultValue != "NULL" && tbcol.Attribute.IsIdentity == false)
                                insertvalue = tbcol.DbDefaultValue;
                        sb.Append(insertvalue).Append(", ");
                    }
                    sb.Remove(sb.Length - 2, 2).Append(" FROM ").Append(tablename).Append(";\r\n");
                    sb.Append("DROP TABLE ").Append(tablename).Append(";\r\n");
                    sb.Append("ALTER TABLE ").Append(tmptablename).Append(" RENAME TO ").Append(_commonUtils.QuoteSqlName(tbname[0], tbname[1])).Append(";\r\n");
                    //创建表的索引
                    foreach (var uk in tb.Indexes)
                    {
                        sb.Append("CREATE ");
                        if (uk.IsUnique) sb.Append("UNIQUE ");
                        sb.Append("INDEX ").Append(_commonUtils.QuoteSqlName(ReplaceIndexName(uk.Name, tbname[1]))).Append(" ON ").Append(newtablename).Append("(");
                        foreach (var tbcol in uk.Columns)
                        {
                            sb.Append(_commonUtils.QuoteSqlName(tbcol.Column.Attribute.Name));
                            if (tbcol.IsDesc) sb.Append(" DESC");
                            sb.Append(", ");
                        }
                        sb.Remove(sb.Length - 2, 2).Append(");\r\n");
                    }
                }
                return sb.Length == 0 ? null : sb.ToString();
            }
            finally
            {
                try
                {
                    if (string.IsNullOrEmpty(database) == false)
                        conn.Value.ChangeDatabase(database);
                    _orm.Ado.MasterPool.Return(conn);
                }
                catch
                {
                    _orm.Ado.MasterPool.Return(conn, true);
                }
            }

            object LocalExecuteScalar(string db, string sql)
            {
                if (string.Compare(database, db) != 0) conn.Value.ChangeDatabase(db);
                try
                {
                    using (var cmd = conn.Value.CreateCommand())
                    {
                        cmd.CommandText = sql;
                        cmd.CommandType = CommandType.Text;
                        var before = new Aop.CommandBeforeEventArgs(cmd);
                        this._orm?.Aop.CommandBeforeHandler?.Invoke(this._orm, before);
                        Exception afterException = null;
                        try
                        {
                            return cmd.ExecuteScalar();
                        }
                        catch (Exception ex)
                        {
                            afterException = ex;
                            throw;
                        }
                        finally
                        {
                            this._orm?.Aop.CommandAfterHandler?.Invoke(this._orm, new Aop.CommandAfterEventArgs(before, afterException, null));
                        }
                    }
                }
                finally
                {
                    if (string.Compare(database, db) != 0) conn.Value.ChangeDatabase(database);
                }
            }
        }

        public override int ExecuteDDLStatements(string ddl)
        {
            if (string.IsNullOrEmpty(ddl)) return 0;
            var scripts = ddl.Split(new string[] { ";\r\n" }, StringSplitOptions.None).Where(a => string.IsNullOrEmpty(a.Trim()) == false).ToArray();

            if (scripts.Any() == false) return 0;

            var affrows = 0;
            foreach (var script in scripts)
            {
                // NovaDb 1.0.2026.1120 起 CREATE TABLE 支持内联 INDEX/UNIQUE INDEX
                // （上游 740a9c1），不再需要剥离后转独立 CREATE INDEX。
                // 仍保留“已存在即幂等”：独立 CREATE INDEX 没有 IF NOT EXISTS 写法
                // （上游未支持），双实例同时首次启动会撞“索引已存在”。
                try { affrows += base.ExecuteDDLStatements(script); }
                catch (Exception ex) when (IsNovaAlreadyExists(ex)) { }
            }
            return affrows;
        }

        /// <summary>
        /// NovaDb：主键列建表时强制 NOT NULL（与 MySQL 语义一致：主键隐含非空）。
        /// Nova 建表不会隐式收紧可空性，若主键列建成 NULLABLE，information_schema
        /// 会如实上报 IS_NULLABLE=YES，与实体侧 IsNullable=false 比对不上，
        /// 导致每次同步都误报变更（UPDATE+MODIFY 空跑）。
        /// </summary>
        static string NovaPrimaryDbType(FreeSql.Internal.Model.TableInfo tb, FreeSql.Internal.Model.ColumnInfo tbcol)
        {
            var dbType = tbcol.Attribute.DbType;
            // 仅当实体侧本身非空（值类型 PK 如 Guid）才补 NOT NULL；
            // 可空的主键列（如 string PK 未标 Required）保持原样，否则同步永远收敛不了。
            if (!tbcol.Attribute.IsNullable &&
                tb.Primarys.Any(p => string.Equals(p.Attribute.Name, tbcol.Attribute.Name, StringComparison.OrdinalIgnoreCase)) &&
                dbType.IndexOf("NOT NULL", StringComparison.OrdinalIgnoreCase) == -1)
                return dbType + " NOT NULL";
            return dbType;
        }

        /// <summary>NovaDb“已存在”类错误的精确识别（FreeSql 包了两层 AggregateException）。</summary>
        static bool IsNovaAlreadyExists(Exception ex)
        {
            var msg = ex.ToString();
            return msg.Contains("already exists");
        }

        /// <summary>
        /// NovaDb 类型判等：服务端 information_schema 上报的类型名与实体侧 DbType 按
        /// “基类型家族 + 长度”判等。
        /// <para>
        /// Nova 1.0.2026.1120 起（上游 26be89d/742eb2b）改为输出**规范类型名**
        /// 与**真实长度**：字符串列上报 <c>string(n)</c>（旧版是 <c>varchar(255)</c>），
        /// bool 上报 <c>bool</c>（旧版 <c>tinyint(1)</c>），且列长度成为真实约束。
        /// 因此这里对字符串家族比较长度：同长视为一致（避免每次同步空跑 MODIFY），
        /// 长度变化视为真实变更（让改长/改型能正确迁移）。
        /// </para>
        /// </summary>
        static bool NovaDbTypeEquals(string expectedDbType, string actualColumnType)
        {
            static (string Base, int? Length) Parse(string t)
            {
                t = t.Trim().ToLowerInvariant();
                int? length = null;
                var open = t.IndexOf('(');
                if (open >= 0)
                {
                    var close = t.IndexOf(')', open);
                    if (close > open && int.TryParse(t.Substring(open + 1, close - open - 1), out var n))
                        length = n;
                    t = t.Substring(0, open);
                }
                var space = t.IndexOf(' ');
                if (space >= 0) t = t.Substring(0, space);
                return (t, length);
            }

            var (e, eLength) = Parse(expectedDbType);
            var (a, aLength) = Parse(actualColumnType);
            if (e == a) return true;

            // 字符串家族：Nova 内部统一 String，上报 string(n)；
            // 实体侧可能是 varchar/char/text/enum/set（MySQL 方言生成）。
            static bool IsString(string s) =>
                s is "varchar" or "char" or "string" or "text" or "longtext" or "mediumtext" or "enum" or "set";
            if (IsString(e) && IsString(a))
            {
                // 双方都有明确长度时按长度比；任一方无长度（text/enum/未声明）视为不限，按家族判等。
                if (eLength.HasValue && aLength.HasValue) return eLength == aLength;
                return true;
            }

            // bool 家族：Nova 原生 BOOL，规范名 bool；旧版上报 tinyint(1)。
            if ((e is "bool" or "boolean" or "bit") && (a is "bool" or "boolean" or "tinyint")) return true;
            return false;
        }
    }
}