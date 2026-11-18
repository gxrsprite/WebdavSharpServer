// NovaDb 分支：仅保留 MySql 方言。Oracle / SqlServer / PostgreSQL 方言文件未引入，
// 对应 Format 方法一并移除（否则引用缺失类型导致编译失败）。
public static partial class FreeSqlCustomGlobalExtensions
{

    /// <summary>
    /// 特殊处理类似 string.Format 的使用方法，防止注入，以及 IS NULL 转换
    /// </summary>
    /// <param name="that"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    internal static string FormatCustomMySql(this string that, params object[] args) => _customMySqlAdo.Addslashes(that, args);
    static FreeSql.Custom.MySql.CustomMySqlAdo _customMySqlAdo = new FreeSql.Custom.MySql.CustomMySqlAdo();
}
