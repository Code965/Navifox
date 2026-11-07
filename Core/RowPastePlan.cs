using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Navifox.Core.Models;

namespace Navifox.Core;

public sealed record RowColumnMapping(int Source, int Target, string Name);
public sealed record RowPastePlan(IReadOnlyList<RowColumnMapping> Mappings, IReadOnlyList<IReadOnlyDictionary<int, object?>> Rows,
    IReadOnlyList<string> IgnoredColumns, IReadOnlyList<string> GeneratedColumns, IReadOnlyList<string> RequiredColumns)
{
    public static RowPastePlan Create(GridRowsClipboard source, IReadOnlyList<ColumnInfo> target, DatabaseKind? dialect = null)
    {
        source.Validate();
        var mappings = new List<RowColumnMapping>();
        var used = new HashSet<int>();
        var generated = new List<string>();
        for (var t = 0; t < target.Count; t++)
        {
            if (target[t].IsAutoIncrement || target[t].IsGenerated) { generated.Add(target[t].Name); continue; }
            var s = Array.FindIndex(source.Columns, n => n == target[t].Name);
            if (s >= 0) { mappings.Add(new(s, t, target[t].Name)); used.Add(s); }
        }
        for (var t = 0; t < target.Count; t++)
        {
            if (target[t].IsAutoIncrement || target[t].IsGenerated || mappings.Any(m => m.Target == t)) continue;
            var candidates = Enumerable.Range(0, source.Columns.Length).Where(s => !used.Contains(s) && string.Equals(source.Columns[s], target[t].Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var targetMatches = target.Count(c => string.Equals(c.Name, target[t].Name, StringComparison.OrdinalIgnoreCase));
            if (candidates.Length == 1 && targetMatches == 1) { mappings.Add(new(candidates[0], t, target[t].Name)); used.Add(candidates[0]); }
        }
        if (mappings.Count == 0) throw new InvalidOperationException("Nessuna colonna modificabile corrisponde alla tabella di destinazione. Usa Importa per scegliere un abbinamento diverso.");
        var rows = new List<IReadOnlyDictionary<int, object?>>();
        for (var r = 0; r < source.Rows.Length; r++)
        {
            var values = new Dictionary<int, object?>();
            foreach (var mapping in mappings)
            {
                try { values[mapping.Target] = ConvertValue(source.Rows[r][mapping.Source].ToValue(), target[mapping.Target], dialect); }
                catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
                { throw new FormatException($"Riga {r + 1}, colonna «{mapping.Name}»: valore non compatibile con {target[mapping.Target].DataType}. {error.Message}", error); }
            }
            rows.Add(values);
        }
        var ignored = source.Columns.Where((name, index) => !used.Contains(index) && !target.Any(c => (c.IsAutoIncrement || c.IsGenerated) && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))).ToArray();
        var required = target.Select((c, i) => (c, i)).Where(p => !p.c.IsAutoIncrement && !p.c.IsGenerated && !p.c.IsNullable && p.c.DefaultValue is null &&
            (!mappings.Any(m => m.Target == p.i) || rows.Any(row => row[p.i] is null))).Select(p => p.c.Name).ToArray();
        return new(mappings.OrderBy(m => m.Target).ToArray(), rows, ignored, generated, required);
    }

    private static object? ConvertValue(object? value, ColumnInfo column, DatabaseKind? dialect)
    {
        if (value is null) return null;
        var type = column.DataType.Split('(', ' ')[0].ToLowerInvariant();
        if (type is "bool" or "boolean" or "bit")
            return Convert.ToString(value, CultureInfo.InvariantCulture)?.ToLowerInvariant() switch
            { "1" or "true" or "t" => true, "0" or "false" or "f" => false, _ => throw new FormatException("Usa true/false oppure 1/0.") };
        if (type is "int" or "integer" or "bigint" or "smallint" or "tinyint" or "mediumint" or "int2" or "int4" or "int8")
        {
            var textNumber = value is bool flag ? flag ? "1" : "0" : Convert.ToString(value, CultureInfo.InvariantCulture);
            var number = BigInteger.Parse(textNumber!, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (dialect is not null)
            {
                var bits = dialect == DatabaseKind.Sqlite ? 64 : type switch { "tinyint" => 8, "smallint" or "int2" => 16, "mediumint" => 24, "bigint" or "int8" => 64, _ => 32 };
                var unsigned = dialect == DatabaseKind.MySql && column.DataType.Contains("unsigned", StringComparison.OrdinalIgnoreCase) || dialect == DatabaseKind.SqlServer && type == "tinyint";
                var min = unsigned ? BigInteger.Zero : -(BigInteger.One << (bits - 1));
                var max = (BigInteger.One << (unsigned ? bits : bits - 1)) - 1;
                if (number < min || number > max) throw new FormatException("Il numero supera l’intervallo della colonna di destinazione.");
            }
            return number >= long.MinValue && number <= long.MaxValue ? (object)(long)number : number;
        }
        if (value is byte[] && type is not ("blob" or "tinyblob" or "mediumblob" or "longblob" or "binary" or "varbinary" or "bytea" or "image"))
            throw new FormatException("Il valore è binario e richiede una colonna binaria.");
        if (value is not string text) return value;
        if (type is "blob" or "tinyblob" or "mediumblob" or "longblob" or "binary" or "varbinary" or "bytea" or "image")
            return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.FromHexString(text[2..]) : throw new FormatException("Per i dati binari usa il formato 0x seguito da cifre esadecimali.");
        if (type is "decimal" or "numeric" or "money" or "smallmoney" or "number")
        {
            var number = text.Trim();
            if (CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator == "," && !number.Contains('.')) number = number.Replace(',', '.');
            // Mantiene tutte le cifre anche per DECIMAL con precisione superiore a quella di System.Decimal.
            if (Regex.IsMatch(number, @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$", RegexOptions.CultureInvariant)) return number;
            throw new FormatException("Numero decimale non valido.");
        }
        if (type is "float" or "real" or "double")
        {
            var number = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            return double.IsFinite(number) ? number : throw new FormatException("Numero non finito.");
        }
        if (type is "date" or "datetime" or "datetime2" or "smalldatetime" or "timestamp" or "timestamptz")
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw new FormatException("Data non valida.");
        if (type is "uuid" or "uniqueidentifier") return Guid.Parse(text);
        return text;
    }
}
