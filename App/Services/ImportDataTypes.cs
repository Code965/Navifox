using System.Globalization;
using Navifox.Core.Models;

namespace Navifox.App.Services;

public enum ImportDataType { Text, Integer, Decimal, Boolean, Date, DateTime, Time, Binary }
public sealed record ImportValueOptions(string DateOrder = "DMY", string DateDelimiter = "/", string TimeDelimiter = ":", string DecimalSymbol = ".", string DateTimeOrder = "Data Ora", string BinaryEncoding = "Base64");

public static class ImportDataTypes
{
    public static ImportDataType FromSql(string sql)
    {
        var type = sql.ToLowerInvariant().Split('(')[0].Trim();
        if (type is "bool" or "boolean" or "bit") return ImportDataType.Boolean;
        if (type.Split(' ')[0] is "int" or "integer" or "int2" or "int4" or "int8" or "tinyint" or "smallint" or "mediumint" or "bigint" or "serial" or "smallserial" or "bigserial" or "year") return ImportDataType.Integer;
        if (type is "decimal" or "numeric" or "real" or "float" or "float4" or "float8" or "double" or "double precision" or "money" or "smallmoney") return ImportDataType.Decimal;
        if (type == "date") return ImportDataType.Date;
        if (type == "time" || type.StartsWith("time without") || type.StartsWith("time with")) return ImportDataType.Time;
        if (type.Contains("date") || type.StartsWith("timestamp")) return ImportDataType.DateTime;
        if (type.Contains("binary") || type is "blob" or "bytea" or "image") return ImportDataType.Binary;
        return ImportDataType.Text;
    }

    public static string SqlType(DatabaseKind kind, ImportDataType type) => type switch
    {
        ImportDataType.Integer => "BIGINT",
        ImportDataType.Decimal => kind == DatabaseKind.PostgreSql ? "NUMERIC" : "DECIMAL(38,10)",
        ImportDataType.Boolean => kind switch { DatabaseKind.SqlServer => "BIT", DatabaseKind.Sqlite => "INTEGER", _ => "BOOLEAN" },
        ImportDataType.Date => "DATE",
        ImportDataType.DateTime => kind switch { DatabaseKind.SqlServer => "DATETIME2", DatabaseKind.MySql => "DATETIME", _ => "TIMESTAMP" },
        ImportDataType.Time => "TIME",
        ImportDataType.Binary => kind switch { DatabaseKind.PostgreSql => "BYTEA", DatabaseKind.SqlServer => "VARBINARY(MAX)", DatabaseKind.MySql => "LONGBLOB", _ => "BLOB" },
        _ => kind switch { DatabaseKind.SqlServer => "NVARCHAR(MAX)", DatabaseKind.MySql => "LONGTEXT", _ => "TEXT" }
    };

    public static object? ConvertValue(object? value, ImportDataType type, ImportValueOptions options, bool emptyAsNull)
    {
        if (value is null or DBNull) return null;
        var text = value switch
        {
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "1" : "0",
            _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
        if (emptyAsNull && text.Length == 0) return null;
        if (type == ImportDataType.Text) return text;
        var trimmed = text.Trim();
        if (type == ImportDataType.Binary)
            return value is byte[] bytes ? bytes : options.BinaryEncoding == "Hex" ? System.Convert.FromHexString(trimmed.StartsWith("0x") ? trimmed[2..] : trimmed) : System.Convert.FromBase64String(trimmed);
        if (type == ImportDataType.Boolean)
            return trimmed.ToLowerInvariant() switch { "1" or "true" or "vero" or "sì" or "si" or "yes" => true, "0" or "false" or "falso" or "no" => false, _ => throw new FormatException("Valore booleano non valido.") };
        if (type is ImportDataType.Integer or ImportDataType.Decimal)
        {
            var format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            format.NumberDecimalSeparator = options.DecimalSymbol;
            format.NumberGroupSeparator = options.DecimalSymbol == "," ? "." : ",";
            decimal number;
            if (value is string)
            {
                var mantissa = trimmed.Split('e', 'E')[0].TrimStart('+', '-');
                var parts = mantissa.Split(options.DecimalSymbol);
                var groups = parts[0].Split(format.NumberGroupSeparator);
                if (parts.Length > 2 || parts.Skip(1).Any(p => p.Contains(format.NumberGroupSeparator, StringComparison.Ordinal)) || groups.Length > 1 && (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(g => g.Length != 3)))
                    throw new FormatException("Separatore decimale o raggruppamento delle migliaia non valido.");
                number = decimal.Parse(trimmed, NumberStyles.Number | NumberStyles.AllowExponent, format);
            }
            else number = decimal.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (type == ImportDataType.Decimal) return number;
            if (decimal.Truncate(number) != number) throw new FormatException("Il valore non è un intero.");
            return checked((long)number);
        }
        if (type == ImportDataType.Time)
        {
            if (value is DateTime nativeTime) return nativeTime.TimeOfDay.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);
            if (!TimeSpan.TryParseExact(trimmed.Replace(options.TimeDelimiter, ":"), [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss", @"hh\:mm\:ss\.FFFFFFF"], CultureInfo.InvariantCulture, out var time)) throw new FormatException("Ora non valida.");
            return time.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);
        }
        if (value is DateTime native) return type == ImportDataType.Date ? native.Date : native;
        var dateFormat = options.DateOrder switch { "MDY" => $"M'{options.DateDelimiter}'d'{options.DateDelimiter}'yyyy", "YMD" => $"yyyy'{options.DateDelimiter}'M'{options.DateDelimiter}'d", _ => $"d'{options.DateDelimiter}'M'{options.DateDelimiter}'yyyy" };
        var timeFormat = $"H'{options.TimeDelimiter}'mm'{options.TimeDelimiter}'ss";
        var full = options.DateTimeOrder == "Ora Data" ? timeFormat + " " + dateFormat : dateFormat + " " + timeFormat;
        string[] formats = [dateFormat, full, full.Replace("ss", "ss.FFFFFFF"), "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];
        if (!DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) throw new FormatException("Data non valida per il formato scelto.");
        return type == ImportDataType.Date ? parsed.Date : parsed;
    }

    public static ImportDataType Infer(IEnumerable<object?> values, ImportValueOptions options)
    {
        var sample = values.Where(v => v is not null && !string.IsNullOrWhiteSpace(System.Convert.ToString(v, CultureInfo.InvariantCulture))).ToArray();
        if (sample.Length == 0) return ImportDataType.Text;
        if (sample.All(v => v is bool)) return ImportDataType.Boolean;
        if (sample.Any(v => v is string s && s.Length > 1 && s[0] == '0' && s.All(char.IsDigit))) return ImportDataType.Text;
        foreach (var type in new[] { ImportDataType.Integer, ImportDataType.Decimal, ImportDataType.Boolean, ImportDataType.DateTime })
        {
            try
            {
                var converted = sample.Select(v => ConvertValue(v, type, options, true)).ToArray();
                if (type == ImportDataType.DateTime && converted.Cast<DateTime>().All(d => d.TimeOfDay == TimeSpan.Zero)) return ImportDataType.Date;
                return type;
            }
            catch (Exception error) when (error is FormatException or OverflowException) { }
        }
        return ImportDataType.Text;
    }

    public static void ValidatePrecision(object? value, string sqlType)
    {
        if (value is not decimal number) return;
        var match = System.Text.RegularExpressions.Regex.Match(sqlType, @"^(?:decimal|numeric)\s*\(\s*(\d+)\s*,\s*(\d+)\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return;
        var scale = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (scale < 28 && decimal.Round(number, scale) != number) throw new FormatException("Il valore ha più cifre decimali di quelle consentite dalla colonna. Scegli un tipo con precisione adeguata.");
        var integer = decimal.Truncate(decimal.Abs(number));
        if (integer > 0 && integer.ToString("0", CultureInfo.InvariantCulture).Length > int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - scale)
            throw new FormatException("La parte intera supera la precisione della colonna.");
    }
}
