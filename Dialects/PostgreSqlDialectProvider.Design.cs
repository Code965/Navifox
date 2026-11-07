using System.Data.Common;
using System.Text.RegularExpressions;
using MosaicDB.Core.Models;

namespace MosaicDB.Dialects;

/// <summary>Progetta tabella (PostgreSQL): lettura della struttura da pg_catalog.</summary>
public sealed partial class PostgreSqlDialectProvider
{
    private static readonly Regex TriggerDefinition = new(
        @"^CREATE (?:CONSTRAINT )?TRIGGER (?<name>""(?:[^""]|"""")+""|\S+) (?<timing>BEFORE|AFTER|INSTEAD OF) (?<events>.+?) ON (?<table>(?:""(?:[^""]|"""")+""|[^\s.""]+)(?:\.(?:""(?:[^""]|"""")+""|[^\s.""]+))?) (?<body>.*)$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FormattedType = new(@"^(?<base>[^(\[]+?)(?:\((?<args>[^)]*)\))?(?<suffix> with(?:out)? time zone)?(?<array>(?:\[\])*)$", RegexOptions.Compiled);

    public override async Task<TableDesign> GetTableDesignAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var p = new Dictionary<string, object> { ["@rel"] = DumpName(schema, table) };

        var primaryRows = await ReadStringsAsync(connection, """
            SELECT c.conname, a.attname
            FROM pg_constraint c CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(att, ord)
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.att
            WHERE c.conrelid = CAST(@rel AS regclass) AND c.contype = 'p' ORDER BY k.ord
            """, p, cancellationToken);
        var primary = primaryRows.Select(r => r[1]).ToList();

        var columnRows = await ReadStringsAsync(connection, """
            SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull::text, pg_get_expr(d.adbin, d.adrelid), (d.adbin IS NULL)::text,
                   a.attidentity::text, a.attgenerated::text, col_description(a.attrelid, a.attnum)
            FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attrelid = CAST(@rel AS regclass) AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """, p, cancellationToken);
        var columns = columnRows.Select(r =>
        {
            var (type, length, decimals) = ParseFormattedType(r[1]);
            var generated = r[6] == "s";
            var identity = r[5] is "a" or "d";
            return new DesignColumn(r[0], r[0], type, length, decimals,
                NotNull: r[2] == "true",
                IsVirtual: generated,
                Expression: generated ? r[3] : null,
                KeyOrder: primary.IndexOf(r[0]) + 1,
                Default: generated || identity || r[4] == "true" ? null : r[3],
                Comment: r[7].Length == 0 ? null : r[7],
                AutoIncrement: identity,
                StoredGenerated: generated);
        }).ToList();

        var indexRows = await ReadStringsAsync(connection, """
            SELECT ic.relname, i.indisunique::text, am.amname, pg_get_indexdef(i.indexrelid, k.n, true), obj_description(i.indexrelid, 'pg_class'),
                   EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = i.indexrelid AND c.contype = 'u')::text,
                   ((i.indoption[k.n - 1] & 1) = 1)::text
            FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_am am ON am.oid = ic.relam
            CROSS JOIN LATERAL generate_series(1, i.indnkeyatts) AS k(n)
            WHERE i.indrelid = CAST(@rel AS regclass) AND NOT i.indisprimary
              AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = i.indexrelid AND c.contype NOT IN ('u'))
            ORDER BY ic.relname, k.n
            """, p, cancellationToken);
        var indexes = indexRows.GroupBy(r => r[0]).Select(g =>
        {
            var first = g.First();
            return new DesignIndex(g.Key, g.Key, g.Select(r => r[3] + (r[6] == "true" ? " DESC" : string.Empty)).ToArray(), first[1] == "true" ? "UNIQUE" : "NORMAL", first[2],
                first[4].Length == 0 ? null : first[4], IsConstraint: first[5] == "true");
        }).ToList();

        var keyRows = await ReadStringsAsync(connection, """
            SELECT c.conname, a.attname, fn.nspname, fc.relname, fa.attname, c.confdeltype::text, c.confupdtype::text
            FROM pg_constraint c
            CROSS JOIN LATERAL unnest(c.conkey, c.confkey) WITH ORDINALITY AS k(att, fatt, ord)
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.att
            JOIN pg_class fc ON fc.oid = c.confrelid
            JOIN pg_namespace fn ON fn.oid = fc.relnamespace
            JOIN pg_attribute fa ON fa.attrelid = c.confrelid AND fa.attnum = k.fatt
            WHERE c.conrelid = CAST(@rel AS regclass) AND c.contype = 'f'
            ORDER BY c.conname, k.ord
            """, p, cancellationToken);
        var foreignKeys = keyRows.GroupBy(r => r[0]).Select(g =>
        {
            var first = g.First();
            return new DesignForeignKey(g.Key, g.Key, g.Select(r => r[1]).ToArray(), first[2], first[3], g.Select(r => r[4]).ToArray(), Rule(first[5]), Rule(first[6]));
        }).ToList();

        var triggerRows = await ReadStringsAsync(connection,
            "SELECT tgname, pg_get_triggerdef(oid, true) FROM pg_trigger WHERE tgrelid = CAST(@rel AS regclass) AND NOT tgisinternal ORDER BY tgname", p, cancellationToken);
        var triggers = triggerRows.Select(r =>
        {
            var m = TriggerDefinition.Match(r[1]);
            return m.Success
                ? new DesignTrigger(r[0], r[0], m.Groups["timing"].Value, m.Groups["events"].Value, m.Groups["body"].Value)
                : new DesignTrigger(r[0], r[0], "AFTER", "INSERT", r[1]);
        }).ToList();

        var comment = (await ReadStringsAsync(connection, "SELECT obj_description(CAST(@rel AS regclass), 'pg_class')", p, cancellationToken))[0][0];
        return new TableDesign(table, table, columns, indexes, foreignKeys, triggers, new TableDesignOptions(), comment.Length == 0 ? null : comment,
            primaryRows.Count > 0 ? primaryRows[0][0] : null);
    }

    private static string Rule(string code) => code switch
    {
        "r" => "RESTRICT",
        "c" => "CASCADE",
        "n" => "SET NULL",
        "d" => "SET DEFAULT",
        _ => "NO ACTION",
    };

    /// <summary>«character varying(255)» → (varchar, 255) · «numeric(10,2)» → (numeric, 10, 2) · «timestamp(3) with time zone» → (timestamptz, 3).</summary>
    internal static (string Type, int? Length, int? Decimals) ParseFormattedType(string formatted)
    {
        var m = FormattedType.Match(formatted.Trim());
        if (!m.Success) return (formatted, null, null);
        var name = (m.Groups["base"].Value.Trim() + m.Groups["suffix"].Value).Trim();
        name = name switch
        {
            "character varying" => "varchar",
            "character" => "char",
            "timestamp without time zone" => "timestamp",
            "timestamp with time zone" => "timestamptz",
            "time without time zone" => "time",
            "time with time zone" => "timetz",
            "bit varying" => "varbit",
            _ => name
        };
        int? length = null, decimals = null;
        var args = m.Groups["args"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (args.Length > 0 && int.TryParse(args[0], out var l)) length = l;
        if (args.Length > 1 && int.TryParse(args[1], out var d)) decimals = d;
        return (name + m.Groups["array"].Value, length, decimals);
    }
}
