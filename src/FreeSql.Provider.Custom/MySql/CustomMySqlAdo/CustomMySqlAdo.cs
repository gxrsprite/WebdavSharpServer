using FreeSql.Internal;
using FreeSql.Internal.Model;
using FreeSql.Internal.ObjectPool;
using System;
using System.Collections;
using System.Data.Common;
using System.Text;
using System.Threading;

namespace FreeSql.Custom.MySql
{
    public class CustomMySqlAdo : FreeSql.Internal.CommonProvider.AdoProvider
    {
        DbProviderFactory Factory => FreeSqlCustomAdapterGlobalExtensions.GetDbProviderFactory(_util._orm);

        public CustomMySqlAdo() : base(DataType.CustomMySql, null, null) { }
        public CustomMySqlAdo(CommonUtils util, string masterConnectionString, string[] slaveConnectionStrings, Func<DbConnection> connectionFactory) : base(DataType.CustomMySql, masterConnectionString, slaveConnectionStrings)
        {
            base._util = util;
            if (connectionFactory != null)
            {
                var pool = new FreeSql.Internal.CommonProvider.DbConnectionPool(DataType.CustomMySql, connectionFactory);
                ConnectionString = pool.TestConnection?.ConnectionString;
                MasterPool = pool;
                return;
            }
            throw new Exception(CoreErrorStrings.S_CustomAdapter_OnlySuppport_UseConnectionFactory);
        }
        public override object AddslashesProcessParam(object param, Type mapType, ColumnInfo mapColumn)
        {
            if (param == null) return "NULL";
            if (mapType != null && mapType != param.GetType() && (param is IEnumerable == false))
                param = Utils.GetDataReaderValue(mapType, param);

            // NovaDb：字符串字面量只有 '' 转义，没有反斜杠转义（词法见 NovaDb SqlLexer.ReadString）。
            // 若沿用 MySQL 的 "\\" → "\\\\" 加倍，Windows 路径（C:\data\x）入库会被写成双反斜杠。
            // 所以本分支只做 '' 转义，不碰反斜杠。
            // NovaDb：bool 内联为 TRUE/FALSE 关键字（Nova 词法有 True/False 关键字）。
            // 不能沿用 MySQL 的 1/0：Nova 的 CompareValues 不做 bool↔数字强制转换，
            // `bool列 = 1` 恒为 false（WHERE 直接查不出行，登录都过不了）。
            if (param is bool || param is bool?)
                return (bool)param ? "TRUE" : "FALSE";
            else if (param is string)
                return string.Concat("'", param.ToString().Replace("'", "''"), "'");
            else if (param is char)
                return string.Concat("'", param.ToString().Replace("'", "''").Replace('\0', ' '), "'");
            else if (param is Enum)
                return AddslashesTypeHandler(param.GetType(), param) ?? string.Concat("'", param.ToString().Replace("'", "''").Replace(", ", ","), "'"); //((Enum)val).ToInt64();
            else if (decimal.TryParse(string.Concat(param), out var trydec))
                return param;

            else if (param is DateTime)
                return AddslashesTypeHandler(typeof(DateTime), param) ?? string.Concat("'", ((DateTime)param).ToString("yyyy-MM-dd HH:mm:ss.fff"), "'");
            else if (param is DateTime?)
                return AddslashesTypeHandler(typeof(DateTime?), param) ?? string.Concat("'", ((DateTime)param).ToString("yyyy-MM-dd HH:mm:ss.fff"), "'");

            else if (param is TimeSpan || param is TimeSpan?)
            {
                var ts = (TimeSpan)param;
                return $"'{Math.Floor(ts.TotalHours)}:{ts.Minutes}:{ts.Seconds}'";
            }
            else if (param is byte[])
                return $"0x{CommonUtils.BytesSqlRaw(param as byte[])}";
            else if (param is IEnumerable)
                return AddslashesIEnumerable(param, mapType, mapColumn);

            return string.Concat("'", param.ToString().Replace("'", "''"), "'");
        }

        public override DbCommand CreateCommand()
        {
            return Factory.CreateCommand();
        }

        public override void ReturnConnection(IObjectPool<DbConnection> pool, Object<DbConnection> conn, Exception ex)
        {
            pool.Return(conn);
        }

        public override DbParameter[] GetDbParamtersByObject(string sql, object obj) => _util.GetDbParamtersByObject(sql, obj);
    }
}
