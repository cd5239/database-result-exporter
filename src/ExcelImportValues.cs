using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlResultExporter;

internal static class ExcelImportValues
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] DateFormats =
        { "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF" };
    private static readonly string[] OffsetFormats =
        { "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz" };
    private static readonly Dictionary<string, SqlDbType> Types = new(StringComparer.Ordinal)
    {
        ["char"] = SqlDbType.NVarChar, ["varchar"] = SqlDbType.NVarChar,
        ["nchar"] = SqlDbType.NVarChar, ["nvarchar"] = SqlDbType.NVarChar,
        ["tinyint"] = SqlDbType.TinyInt, ["smallint"] = SqlDbType.SmallInt,
        ["int"] = SqlDbType.Int, ["bigint"] = SqlDbType.BigInt,
        ["decimal"] = SqlDbType.Decimal, ["numeric"] = SqlDbType.Decimal,
        ["money"] = SqlDbType.Money, ["smallmoney"] = SqlDbType.SmallMoney,
        ["float"] = SqlDbType.Float, ["real"] = SqlDbType.Real, ["bit"] = SqlDbType.Bit,
        ["date"] = SqlDbType.Date, ["datetime"] = SqlDbType.DateTime,
        ["smalldatetime"] = SqlDbType.SmallDateTime, ["datetime2"] = SqlDbType.DateTime2,
        ["datetimeoffset"] = SqlDbType.DateTimeOffset, ["time"] = SqlDbType.Time,
        ["uniqueidentifier"] = SqlDbType.UniqueIdentifier,
        ["binary"] = SqlDbType.VarBinary, ["varbinary"] = SqlDbType.VarBinary
    };

    internal static SqlDbType ParameterType(string type) => Types[type];
    internal static bool Supported(ImportColumn column) => !column.Encrypted && Types.ContainsKey(column.Type);

    internal static string Example(ImportColumn column) => ParameterType(column.Type) switch
    {
        SqlDbType.NVarChar => "例如 00123，按文本填写",
        SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney =>
            column.Scale == 0 ? "例如 123" : "例如 123." + new string('1', Math.Min(column.Scale, (byte)2)),
        SqlDbType.Bit => "0 或 1",
        SqlDbType.Date => "2026-11-05",
        SqlDbType.DateTime or SqlDbType.SmallDateTime or SqlDbType.DateTime2 => "2026-11-05 09:30:00",
        SqlDbType.DateTimeOffset => "2026-11-05T09:30:00+08:00",
        SqlDbType.Time => "09:30:00",
        SqlDbType.UniqueIdentifier => "11111111-2222-3333-4444-555555555555",
        SqlDbType.VarBinary => "十六进制，例如 0xA1B2",
        _ => "例如 123"
    };

    internal static object Parse(ImportColumn column, string value, bool numericCell, bool date1904)
    {
        var type = ParameterType(column.Type);
        if (type == SqlDbType.NVarChar)
        {
            if (numericCell) throw new FormatException("此列需要文本，请按原值重新填写，以保留前导零。");
            int bytes;
            try
            {
                bytes = column.Type.StartsWith("n", StringComparison.Ordinal)
                    ? checked(value.Length * 2)
                    : Encoding.GetEncoding(column.CodePage, EncoderFallback.ExceptionFallback,
                        DecoderFallback.ExceptionFallback).GetByteCount(value);
            }
            catch (ArgumentException) { throw new FormatException("文本包含目标列编码无法保存的字符。"); }
            if (column.Length >= 0 && bytes > column.Length)
                throw new FormatException($"文本超过列长度限制（{column.Length} 字节）。");
            return value;
        }

        string text = value.Trim();
        if (numericCell && (type is SqlDbType.BigInt or SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney)
            && (!Regex.IsMatch(text, @"^[+-]?[0-9]+(?:\.[0-9]+)?$")
                || text.TrimStart('+', '-', '0').Replace(".", "").TrimStart('0').Length > 15))
            throw new FormatException("Excel 数值可能已丢失精度。请用文本重新填写原始数字。");
        return type switch
        {
            SqlDbType.TinyInt when byte.TryParse(text, NumberStyles.Integer, Invariant, out byte tiny) => tiny,
            SqlDbType.SmallInt when short.TryParse(text, NumberStyles.Integer, Invariant, out short small) => small,
            SqlDbType.Int when int.TryParse(text, NumberStyles.Integer, Invariant, out int integer) => integer,
            SqlDbType.BigInt when long.TryParse(text, NumberStyles.Integer, Invariant, out long big) => big,
            SqlDbType.Bit when text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            SqlDbType.Bit when text == "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            SqlDbType.UniqueIdentifier when Guid.TryParseExact(text, "D", out Guid guid) => guid,
            SqlDbType.Float when double.TryParse(text, NumberStyles.Float, Invariant, out double number) && double.IsFinite(number) => number,
            SqlDbType.Real when float.TryParse(text, NumberStyles.Float, Invariant, out float single) && float.IsFinite(single) => single,
            SqlDbType.Decimal => ExactDecimal(column, text),
            SqlDbType.Money or SqlDbType.SmallMoney => Money(column, text),
            SqlDbType.Date or SqlDbType.DateTime or SqlDbType.SmallDateTime or SqlDbType.DateTime2 =>
                Date(column, text, numericCell, date1904),
            SqlDbType.DateTimeOffset => Offset(column, text),
            SqlDbType.Time => Time(column, text),
            SqlDbType.VarBinary => Binary(column, text),
            _ => throw new FormatException("值的格式或范围不符合 " + column.Description + "。")
        };
    }

    private static SqlDecimal ExactDecimal(ImportColumn column, string text)
    {
        if (!Regex.IsMatch(text, @"^[+-]?[0-9]+(?:\.[0-9]+)?$"))
            throw new FormatException("请输入普通十进制数字，不带千分位或科学计数法。");
        string[] parts = text.TrimStart('+', '-').Split('.');
        string integral = parts[0].TrimStart('0');
        string fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : "";
        if (integral.Length > column.Precision - column.Scale || fraction.Length > column.Scale)
            throw new FormatException("整数位或小数位超出列精度。");
        string normalized = (text.StartsWith('-') ? "-" : "") + (integral.Length == 0 ? "0" : integral)
            + (fraction.Length == 0 ? "" : "." + fraction);
        return SqlDecimal.ConvertToPrecScale(SqlDecimal.Parse(normalized), column.Precision, column.Scale);
    }

    private static decimal Money(ImportColumn column, string text)
    {
        decimal value = ExactDecimal(column, text).Value;
        decimal minimum = column.Type == "smallmoney" ? -214748.3648m : -922337203685477.5808m;
        decimal maximum = column.Type == "smallmoney" ? 214748.3647m : 922337203685477.5807m;
        if (value < minimum || value > maximum) throw new FormatException("金额超出列范围。");
        return value;
    }

    private static DateTime Date(ImportColumn column, string text, bool numericCell, bool date1904)
    {
        DateTime date;
        if (numericCell)
        {
            if (!int.TryParse(text, NumberStyles.None, Invariant, out int days) || days > 2958465
                || (!date1904 && (days < 1 || days == 60)))
                throw new FormatException("请按字段说明填写日期或日期时间文本。");
            date = date1904 ? new DateTime(1904, 1, 1).AddDays(days)
                : new DateTime(1899, 12, 31).AddDays(days < 60 ? days : days - 1);
        }
        else if (!DateTime.TryParseExact(text, DateFormats, Invariant, DateTimeStyles.None, out date))
            throw new FormatException("请填写 yyyy-MM-dd 或 yyyy-MM-dd HH:mm:ss[.小数]。");
        if (column.Type == "date" && date.TimeOfDay != TimeSpan.Zero)
            throw new FormatException("日期列不能包含时间。");
        if (column.Type == "datetime" && new SqlDateTime(date).Value != date)
            throw new FormatException("时间精度无法按 datetime 原样保存。");
        if (column.Type == "smalldatetime" && (date.Ticks % TimeSpan.TicksPerMinute != 0
            || date < new DateTime(1900, 1, 1) || date >= new DateTime(2079, 6, 7)))
            throw new FormatException("smalldatetime 只接受范围内的整分钟。");
        if (column.Type == "datetime2") CheckTimeScale(date.Ticks, column.Scale);
        return date;
    }

    private static DateTimeOffset Offset(ImportColumn column, string text)
    {
        if (text.EndsWith('Z')) text = text[..^1] + "+00:00";
        if (!DateTimeOffset.TryParseExact(text, OffsetFormats, Invariant, DateTimeStyles.None, out var value))
            throw new FormatException("请填写带时区的日期时间，例如 2026-11-05T09:30:00+08:00。");
        CheckTimeScale(value.Ticks, column.Scale);
        return value;
    }

    private static TimeSpan Time(ImportColumn column, string text)
    {
        if (!TimeSpan.TryParseExact(text, new[] { @"hh\:mm\:ss", @"hh\:mm\:ss\.FFFFFFF" }, Invariant, out var time))
            throw new FormatException("请填写 HH:mm:ss[.小数]。");
        CheckTimeScale(time.Ticks, column.Scale);
        return time;
    }

    private static void CheckTimeScale(long ticks, byte scale)
    {
        long quantum = 1;
        for (int digit = scale; digit < 7; digit++) quantum *= 10;
        if (ticks % quantum != 0) throw new FormatException("时间的小数秒超出列精度。");
    }

    private static byte[] Binary(ImportColumn column, string text)
    {
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("请填写以 0x 开头的十六进制文本。");
        byte[] bytes = Convert.FromHexString(text.AsSpan(2));
        if (column.Length >= 0 && bytes.Length > column.Length)
            throw new FormatException("二进制长度超出列限制。");
        if (column.Type == "binary" && bytes.Length != column.Length)
            throw new FormatException("定长 binary 必须填写准确字节数。");
        return bytes;
    }
}
