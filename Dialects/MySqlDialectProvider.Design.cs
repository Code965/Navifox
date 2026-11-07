using System.Data.Common;
using System.Text.RegularExpressions;
using Navifox.Core.Models;

namespace Navifox.Dialects;

/// <summary>Progetta tabella (MySQL/MariaDB): lettura della struttura da INFORMATION_SCHEMA.</summary>
public sealed partial class MySqlDialectProvider
{
    private static readonly Regex ColumnTypePattern = new(@"^(?<base>[a-z]+(?: [a-z]+)*?)(?:\((?<args>.*)\))?(?<mods>(?:\s+(?:unsigned|signed|zerofill))*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly HashSet<string> UnquotedDefaultTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "decimal", "numeric", "float", "double", "real", "bit", "bool", "boolean",
    };

    public override async Task<TableDesign> GetTableDesignAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var p = new Dictionary<string, object> { ["@s"] = schema, ["@t"] = table };
        var version = (await ReadStringsAsync(connection, "SELECT VERSION()", new Dictionary<string, object>(), cancellationToken))[0][0];
        var mariaDb = version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);

        var tableRows = await ReadStringsAsync(connection, """
            SELECT t.ENGINE, t.TABLE_COLLATION, t.AUTO_INCREMENT, t.TABLE_COMMENT, c.CHARACTER_SET_NAME, t.ROW_FORMAT
            FROM INFORMATION_SCHEMA.TABLES t LEFT JOIN INFORMATION_SCHEMA.COLLATIONS c ON c.COLLATION_NAME = t.TABLE_COLLATION
            WHERE t.TABLE_SCHEMA = @s AND t.TABLE_NAME = @t
            """, p, cancellationToken);
        if (tableRows.Count == 0) throw new InvalidOperationException($"Tabella «{table}» non trovata in «{schema}».");
        var t = tableRows[0];
        var options = new TableDesignOptions(NullIfEmpty(t[0]), NullIfEmpty(t[4]), NullIfEmpty(t[1]), long.TryParse(t[2], out var ai) ? ai : null, NullIfEmpty(t[5]));

        var primary = (await ReadStringsAsync(connection, """
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t AND CONSTRAINT_NAME = 'PRIMARY' ORDER BY ORDINAL_POSITION
            """, p, cancellationToken)).Select(r => r[0]).ToList();

        var columnRows = await ReadStringsAsync(connection, """
            SELECT COLUMN_NAME, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, COLUMN_DEFAULT IS NULL, EXTRA, COLUMN_COMMENT,
                   GENERATION_EXPRESSION, COLLATION_NAME
            FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t ORDER BY ORDINAL_POSITION
            """, p, cancellationToken);
        var columns = new List<DesignColumn>();
        foreach (var r in columnRows)
        {
            var (type, length, decimals, unsigned, zerofill) = ParseColumnType(r[2], r[1]);
            var extra = r[6];
            var isVirtual = extra.Contains("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase) || extra.Contains("STORED GENERATED", StringComparison.OrdinalIgnoreCase)
                            || extra.Contains("PERSISTENT", StringComparison.OrdinalIgnoreCase);
            var stored = extra.Contains("STORED", StringComparison.OrdinalIgnoreCase) || extra.Contains("PERSISTENT", StringComparison.OrdinalIgnoreCase);
            var onUpdate = Regex.Match(extra, @"on update (\S+(?:\(\d*\))?)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null;
            columns.Add(new DesignColumn(
                r[0], r[0], type, length, decimals,
                NotNull: r[3] == "NO",
                IsVirtual: isVirtual,
                Expression: isVirtual ? NullIfEmpty(r[8]) : null,
                KeyOrder: primary.IndexOf(r[0]) + 1,
                Default: isVirtual ? null : DefaultExpression(r[4], r[5] == "1", extra, r[1], mariaDb),
                Comment: NullIfEmpty(r[7]),
                AutoIncrement: extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                Unsigned: unsigned,
                Zerofill: zerofill,
                OnUpdate: onUpdate,
                StoredGenerated: stored,
                Collation: NullIfEmpty(r[9])));
        }

        var indexRows = await ReadStringsAsync(connection, """
            SELECT INDEX_NAME, NON_UNIQUE, COLUMN_NAME, SUB_PART, INDEX_TYPE, INDEX_COMMENT, COLLATION
            FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t AND INDEX_NAME <> 'PRIMARY'
            ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """, p, cancellationToken);
        var indexes = indexRows.GroupBy(r => r[0]).Select(g =>
        {
            var first = g.First();
            var kind = first[4].ToUpperInvariant() switch { "FULLTEXT" => "FULLTEXT", "SPATIAL" => "SPATIAL", _ => first[1] == "0" ? "UNIQUE" : "NORMAL" };
            var method = first[4].ToUpperInvariant() is "BTREE" or "HASH" ? first[4].ToUpperInvariant() : null;
            var cols = g.Select(r => r[2] + (r[3].Length > 0 ? $"({r[3]})" : string.Empty) + (r[6] == "D" ? " DESC" : string.Empty)).ToArray();
            return new DesignIndex(g.Key, g.Key, cols, kind, method, NullIfEmpty(first[5]));
        }).ToList();

        var keyRows = await ReadStringsAsync(connection, """
            SELECT rc.CONSTRAINT_NAME, k.COLUMN_NAME, k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME, rc.DELETE_RULE, rc.UPDATE_RULE
            FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k ON k.CONSTRAINT_SCHEMA = rc.CONSTRAINT_SCHEMA AND k.CONSTRAINT_NAME = rc.CONSTRAINT_NAME AND k.TABLE_NAME = rc.TABLE_NAME
            WHERE rc.CONSTRAINT_SCHEMA = @s AND rc.TABLE_NAME = @t
            ORDER BY rc.CONSTRAINT_NAME, k.ORDINAL_POSITION
            """, p, cancellationToken);
        var foreignKeys = keyRows.GroupBy(r => r[0]).Select(g =>
        {
            var first = g.First();
            return new DesignForeignKey(g.Key, g.Key, g.Select(r => r[1]).ToArray(), first[2], first[3], g.Select(r => r[4]).ToArray(), first[5], first[6]);
        }).ToList();

        var triggers = (await ReadStringsAsync(connection, """
            SELECT TRIGGER_NAME, ACTION_TIMING, EVENT_MANIPULATION, ACTION_STATEMENT
            FROM INFORMATION_SCHEMA.TRIGGERS WHERE EVENT_OBJECT_SCHEMA = @s AND EVENT_OBJECT_TABLE = @t ORDER BY ACTION_TIMING, EVENT_MANIPULATION, ACTION_ORDER
            """, p, cancellationToken)).Select(r => new DesignTrigger(r[0], r[0], r[1], r[2], r[3])).ToList();

        return new TableDesign(table, table, columns, indexes, foreignKeys, triggers, options, NullIfEmpty(t[3]), primary.Count > 0 ? "PRIMARY" : null);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    internal static (string Type, int? Length, int? Decimals, bool Unsigned, bool Zerofill) ParseColumnType(string columnType, string dataType)
    {
        var match = ColumnTypePattern.Match(columnType.Trim());
        if (!match.Success) return (columnType, null, null, false, false);
        var baseType = match.Groups["base"].Value.ToLowerInvariant();
        var args = match.Groups["args"].Value;
        var mods = match.Groups["mods"].Value.ToLowerInvariant();
        if (baseType is "enum" or "set") return ($"{baseType}({args})", null, null, false, false);
        int? length = null, decimals = null;
        if (args.Length > 0)
        {
            var parts = args.Split(',');
            if (int.TryParse(parts[0].Trim(), out var l)) length = l;
            if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var d)) decimals = d;
        }
        return (baseType.Length > 0 ? baseType : dataType, length, decimals, mods.Contains("unsigned"), mods.Contains("zerofill"));
    }

    /// <summary>Converte COLUMN_DEFAULT in espressione SQL riutilizzabile in ALTER/CREATE.</summary>
    private static string? DefaultExpression(string value, bool isNull, string extra, string dataType, bool mariaDb)
    {
        if (mariaDb)
        {
            // MariaDB restituisce già l'espressione SQL ('testo', NULL, current_timestamp()).
            if (isNull || value.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return null;
            return value;
        }
        if (isNull) return null;
        if (extra.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(value, @"^(CURRENT_TIMESTAMP|NOW|LOCALTIME|LOCALTIMESTAMP)(\(\d*\))?$", RegexOptions.IgnoreCase) ? value.ToUpperInvariant() : $"({value})";
        if (UnquotedDefaultTypes.Contains(dataType) || value.StartsWith("b'", StringComparison.Ordinal)) return value;
        return "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
    }
}
