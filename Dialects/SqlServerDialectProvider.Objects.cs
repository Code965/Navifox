using System.Data.Common;
using System.Text.RegularExpressions;
using Navifox.Core.Models;

namespace Navifox.Dialects;

/// <summary>Progetta tabella, procedure, funzioni, trigger e viste (SQL Server).</summary>
public sealed partial class SqlServerDialectProvider
{
    public override IReadOnlyList<SchemaObjectKind> SchemaObjectKinds { get; } = [SchemaObjectKind.Routine, SchemaObjectKind.Trigger];

    private static readonly Regex TriggerHeader = new(
        @"^\s*CREATE\s+(?:OR\s+ALTER\s+)?TRIGGER\s+.+?\s+ON\s+.+?\s+(?:WITH\s+[\w\s,]+?\s+)?(?<timing>FOR|AFTER|INSTEAD\s+OF)\s+(?<events>[\w\s,]+?)\s+(?:NOT\s+FOR\s+REPLICATION\s+)?AS\b(?<body>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private string ObjectName(string schema, string name) => $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";

    public override async Task<TableDesign> GetTableDesignAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var p = new Dictionary<string, object> { ["@object"] = ObjectName(schema, table) };
        var primary = await ReadStringsAsync(connection, """
            SELECT kc.name, c.name FROM sys.key_constraints kc
            JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE kc.parent_object_id = OBJECT_ID(@object) AND kc.type = 'PK' ORDER BY ic.key_ordinal
            """, p, cancellationToken);
        var keyColumns = primary.Select(r => r[1]).ToList();

        var columnRows = await ReadStringsAsync(connection, """
            SELECT c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed,
                   cc.definition, CAST(cc.is_persisted AS int), OBJECT_DEFINITION(c.default_object_id), c.collation_name,
                   CAST((SELECT ep.value FROM sys.extended_properties ep WHERE ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description') AS nvarchar(4000))
            FROM sys.columns c LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@object) ORDER BY c.column_id
            """, p, cancellationToken);
        var columns = columnRows.Select(r =>
        {
            var type = r[1];
            var maxLength = int.Parse(r[2]);
            int? length = null, decimals = null;
            switch (type)
            {
                case "varchar" or "char" or "varbinary" or "binary":
                    if (maxLength == -1) type += "(max)"; else length = maxLength;
                    break;
                case "nvarchar" or "nchar":
                    if (maxLength == -1) type += "(max)"; else length = maxLength / 2;
                    break;
                case "decimal" or "numeric":
                    length = int.Parse(r[3]);
                    decimals = int.Parse(r[4]);
                    break;
                case "datetime2" or "time" or "datetimeoffset" when r[4] != "7":
                    length = int.Parse(r[4]);
                    break;
            }
            var computed = r[7] == "True";
            return new DesignColumn(r[0], r[0], type, length, decimals,
                NotNull: r[5] == "False",
                IsVirtual: computed,
                Expression: computed ? Unwrap(r[8]) : null,
                KeyOrder: keyColumns.IndexOf(r[0]) + 1,
                Default: computed ? null : r[10].Length == 0 ? null : Unwrap(r[10]),
                Comment: r[12].Length == 0 ? null : r[12],
                AutoIncrement: r[6] == "True",
                StoredGenerated: r[9] == "1",
                Collation: null);
        }).ToList();

        var indexRows = await ReadStringsAsync(connection, """
            SELECT i.name, CAST(i.is_unique AS int), i.type_desc, CAST(i.is_unique_constraint AS int), c.name, CAST(ic.is_descending_key AS int)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@object) AND i.is_primary_key = 0 AND i.type > 0 AND i.is_hypothetical = 0
            ORDER BY i.name, ic.key_ordinal
            """, p, cancellationToken);
        var indexes = indexRows.GroupBy(r => r[0]).Select(g =>
        {
            var first = g.First();
            return new DesignIndex(g.Key, g.Key, g.Select(r => r[5] == "1" ? r[4] + " DESC" : r[4]).ToArray(),
                first[1] == "1" ? "UNIQUE" : "NORMAL", first[2] == "CLUSTERED" ? "CLUSTERED" : null, IsConstraint: first[3] == "1");
        }).ToList();

        var keyRows = await ReadStringsAsync(connection, """
            SELECT fk.name, pc.name, SCHEMA_NAME(rt.schema_id), rt.name, rc.name, fk.delete_referential_action_desc, fk.update_referential_action_desc
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = OBJECT_ID(@object)
            ORDER BY fk.name, fkc.constraint_column_id
            """, p, cancellationToken);
        var keys = keyRows.GroupBy(r => r[0]).Select(g =>
        {
            var first = g.First();
            return new DesignForeignKey(g.Key, g.Key, g.Select(r => r[1]).ToArray(), first[2], first[3], g.Select(r => r[4]).ToArray(),
                first[5].Replace('_', ' '), first[6].Replace('_', ' '));
        }).ToList();

        var triggerRows = await ReadStringsAsync(connection,
            "SELECT t.name, OBJECT_DEFINITION(t.object_id) FROM sys.triggers t WHERE t.parent_id = OBJECT_ID(@object) ORDER BY t.name", p, cancellationToken);
        var triggers = triggerRows.Select(r =>
        {
            var m = TriggerHeader.Match(r[1]);
            if (!m.Success) return new DesignTrigger(r[0], r[0], "AFTER", "INSERT", r[1]);
            var timing = Regex.Replace(m.Groups["timing"].Value.ToUpperInvariant(), @"\s+", " ");
            return new DesignTrigger(r[0], r[0], timing == "FOR" ? "AFTER" : timing,
                string.Join(", ", m.Groups["events"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(e => e.ToUpperInvariant())),
                m.Groups["body"].Value.Trim());
        }).ToList();

        var comment = await ReadStringsAsync(connection,
            "SELECT CAST(value AS nvarchar(4000)) FROM sys.extended_properties WHERE major_id = OBJECT_ID(@object) AND minor_id = 0 AND name = 'MS_Description'", p, cancellationToken);
        return new TableDesign(table, table, columns, indexes, keys, triggers, new TableDesignOptions(), comment.Count == 0 ? null : comment[0][0],
            primary.Count == 0 ? null : primary[0][0]);
    }

    /// <summary>SQL Server memorizza default ed espressioni tra parentesi, spesso doppie: «((0))» → «0».</summary>
    private static string Unwrap(string expression)
    {
        var value = expression.Trim();
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')' && Balanced(value[1..^1])) value = value[1..^1].Trim();
        return value;
    }

    private static bool Balanced(string text)
    {
        var depth = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '\'') quoted = !quoted;
            if (quoted) continue;
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    public override async Task<IReadOnlyList<DatabaseObjectInfo>> ListSchemaObjectsAsync(DbConnection connection, string database, string schema, SchemaObjectKind kind, CancellationToken cancellationToken)
    {
        var p = new Dictionary<string, object> { ["@schema"] = schema };
        switch (kind)
        {
            case SchemaObjectKind.Routine:
                return (await ReadStringsAsync(connection, """
                    SELECT o.name, o.type, CONVERT(varchar(19), o.modify_date, 120),
                           CAST((SELECT ep.value FROM sys.extended_properties ep WHERE ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description') AS nvarchar(4000)),
                           STUFF((SELECT ', ' + pr.name + ' ' + TYPE_NAME(pr.user_type_id) + CASE WHEN pr.is_output = 1 THEN ' OUTPUT' ELSE '' END
                                  FROM sys.parameters pr WHERE pr.object_id = o.object_id AND pr.parameter_id > 0 ORDER BY pr.parameter_id FOR XML PATH('')), 1, 2, '')
                    FROM sys.objects o
                    WHERE o.schema_id = SCHEMA_ID(@schema) AND o.type IN ('P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0
                    ORDER BY o.name
                    """, p, cancellationToken))
                    .Select(r =>
                    {
                        var procedure = r[1].Trim() == "P";
                        var tableValued = r[1].Trim() is "IF" or "TF";
                        return new DatabaseObjectInfo(r[0], procedure ? "PROCEDURE" : tableValued ? "FUNCTION TABLE" : "FUNCTION", Comment: r[3].Length == 0 ? null : r[3],
                            Detail: $"{(procedure ? "Procedura" : tableValued ? "Funzione tabellare" : "Funzione scalare")}{(r[4].Length == 0 ? string.Empty : $" ({r[4]})")} · modificata {r[2]}",
                            Identity: r[4]);
                    })
                    .ToArray();
            case SchemaObjectKind.Trigger:
                return (await ReadStringsAsync(connection, """
                    SELECT t.name, OBJECT_NAME(t.parent_id), CAST(t.is_instead_of_trigger AS int), CAST(t.is_disabled AS int),
                           STUFF((SELECT ', ' + te.type_desc FROM sys.trigger_events te WHERE te.object_id = t.object_id FOR XML PATH('')), 1, 2, '')
                    FROM sys.triggers t JOIN sys.objects o ON o.object_id = t.object_id
                    WHERE t.parent_class = 1 AND o.schema_id = SCHEMA_ID(@schema)
                    ORDER BY t.name
                    """, p, cancellationToken))
                    .Select(r => new DatabaseObjectInfo(r[0], "TRIGGER",
                        Detail: $"{(r[2] == "1" ? "INSTEAD OF" : "AFTER")} {r[4]} su {r[1]}{(r[3] == "1" ? " · disattivato" : string.Empty)}", Identity: r[1]))
                    .ToArray();
            default:
                return [];
        }
    }

    public override async Task<string> GetSchemaObjectDefinitionAsync(DbConnection connection, string database, string schema, SchemaObjectKind kind, DatabaseObjectInfo item, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, "SELECT OBJECT_DEFINITION(OBJECT_ID(@object))",
            new Dictionary<string, object> { ["@object"] = ObjectName(schema, item.ObjectName) }, cancellationToken);
        if (rows.Count == 0 || rows[0][0].Length == 0) throw new InvalidOperationException($"Definizione di «{item.Name}» non trovata (oggetto cifrato o permessi insufficienti).");
        var definition = rows[0][0].Trim();
        return kind == SchemaObjectKind.View ? Core.SchemaObjectSql.ExtractViewSelect(definition) : definition;
    }

    public override Task<IReadOnlyList<string>> GetRoutineParametersAsync(DbConnection connection, string schema, DatabaseObjectInfo routine, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>((routine.Identity ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.EndsWith(" OUTPUT", StringComparison.OrdinalIgnoreCase)).ToArray());
}
