using System.Data.Common;
using System.Text.RegularExpressions;
using Navifox.Core.Models;

namespace Navifox.Dialects;

/// <summary>Funzioni, procedure, trigger, sequenze e viste (PostgreSQL).</summary>
public sealed partial class PostgreSqlDialectProvider
{
    public override IReadOnlyList<SchemaObjectKind> SchemaObjectKinds { get; } = [SchemaObjectKind.Routine, SchemaObjectKind.Trigger, SchemaObjectKind.Sequence];

    private static readonly Regex TriggerSummary = new(@"\s(?<timing>BEFORE|AFTER|INSTEAD OF)\s(?<events>.+?)\sON\s", RegexOptions.Compiled);

    public override async Task<IReadOnlyList<DatabaseObjectInfo>> ListSchemaObjectsAsync(DbConnection connection, string database, string schema, SchemaObjectKind kind, CancellationToken cancellationToken)
    {
        var p = new Dictionary<string, object> { ["@s"] = schema };
        switch (kind)
        {
            case SchemaObjectKind.Routine:
                return (await ReadStringsAsync(connection, """
                    SELECT p.proname, CASE p.prokind WHEN 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END, pg_get_function_identity_arguments(p.oid),
                           COALESCE(pg_get_function_result(p.oid), ''), obj_description(p.oid, 'pg_proc'), l.lanname
                    FROM pg_proc p
                    JOIN pg_namespace n ON n.oid = p.pronamespace
                    JOIN pg_language l ON l.oid = p.prolang
                    WHERE n.nspname = @s AND p.prokind IN ('f', 'p')
                      AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = p.oid AND d.deptype = 'e')
                    ORDER BY p.proname, 3
                    """, p, cancellationToken))
                    .Select(r => new DatabaseObjectInfo($"{r[0]}({r[2]})", r[1], Comment: r[4].Length == 0 ? null : r[4],
                        Detail: r[1] == "PROCEDURE" ? $"Procedura · {r[5]}" : $"Funzione → {r[3]} · {r[5]}", Identity: r[2], BaseName: r[0]))
                    .ToArray();
            case SchemaObjectKind.Trigger:
                return (await ReadStringsAsync(connection, """
                    SELECT t.tgname, c.relname, pg_get_triggerdef(t.oid), obj_description(t.oid, 'pg_trigger')
                    FROM pg_trigger t
                    JOIN pg_class c ON c.oid = t.tgrelid
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = @s AND NOT t.tgisinternal
                    ORDER BY t.tgname, c.relname
                    """, p, cancellationToken))
                    .Select(r =>
                    {
                        var m = TriggerSummary.Match(r[2]);
                        return new DatabaseObjectInfo(r[0], "TRIGGER", Comment: r[3].Length == 0 ? null : r[3],
                            Detail: m.Success ? $"{m.Groups["timing"].Value} {m.Groups["events"].Value} su {r[1]}" : $"su {r[1]}", Identity: r[1]);
                    })
                    .ToArray();
            case SchemaObjectKind.Sequence:
                return (await ReadStringsAsync(connection, """
                    SELECT s.sequencename, COALESCE(s.last_value::text, ''), s.increment_by::text, s.data_type::text,
                           obj_description(CAST(quote_ident(s.schemaname) || '.' || quote_ident(s.sequencename) AS regclass), 'pg_class')
                    FROM pg_sequences s WHERE s.schemaname = @s ORDER BY s.sequencename
                    """, p, cancellationToken))
                    .Select(r => new DatabaseObjectInfo(r[0], "SEQUENCE", Comment: r[4].Length == 0 ? null : r[4],
                        Detail: $"{r[3]} · valore attuale {(r[1].Length == 0 ? "non ancora usata" : r[1])} · incremento {r[2]}"))
                    .ToArray();
            default:
                return [];
        }
    }

    public override async Task<string> GetSchemaObjectDefinitionAsync(DbConnection connection, string database, string schema, SchemaObjectKind kind, DatabaseObjectInfo item, CancellationToken cancellationToken)
    {
        var p = new Dictionary<string, object> { ["@s"] = schema, ["@n"] = item.ObjectName, ["@x"] = item.Identity ?? string.Empty };
        var sql = kind switch
        {
            SchemaObjectKind.Routine => """
                SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = @s AND p.proname = @n AND pg_get_function_identity_arguments(p.oid) = @x
                """,
            SchemaObjectKind.Trigger => """
                SELECT pg_get_triggerdef(t.oid, true) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @s AND t.tgname = @n AND c.relname = @x
                """,
            SchemaObjectKind.View => "SELECT pg_get_viewdef(CAST(quote_ident(@s) || '.' || quote_ident(@n) AS regclass), true)",
            SchemaObjectKind.Sequence => null,
            _ => throw new NotSupportedException($"{kind} non disponibile su PostgreSQL."),
        };
        if (sql is null)
        {
            var sequence = await GetSequenceAsync(connection, schema, item.ObjectName, cancellationToken);
            return string.Join(";\n", Core.SchemaObjectSql.SequenceSql(schema, null, sequence)) + ";";
        }
        var rows = await ReadStringsAsync(connection, sql, p, cancellationToken);
        if (rows.Count == 0 || rows[0][0].Length == 0) throw new InvalidOperationException($"Definizione di «{item.Name}» non trovata.");
        var definition = rows[0][0].Trim();
        return kind == SchemaObjectKind.View ? definition.TrimEnd(';') : definition;
    }

    public override Task<IReadOnlyList<string>> GetRoutineParametersAsync(DbConnection connection, string schema, DatabaseObjectInfo routine, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>((routine.Identity ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(part => !part.StartsWith("OUT ", StringComparison.OrdinalIgnoreCase)).ToArray());

    public override async Task<SequenceInfo> GetSequenceAsync(DbConnection connection, string schema, string name, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, """
            SELECT s.start_value::text, s.increment_by::text, s.min_value::text, s.max_value::text, s.cache_size::text, s.cycle::text,
                   COALESCE(s.last_value::text, ''), s.data_type::text,
                   (SELECT quote_ident(tn.nspname) || '.' || quote_ident(tc.relname) || '.' || quote_ident(a.attname)
                      FROM pg_depend d
                      JOIN pg_class tc ON tc.oid = d.refobjid
                      JOIN pg_namespace tn ON tn.oid = tc.relnamespace
                      JOIN pg_attribute a ON a.attrelid = d.refobjid AND a.attnum = d.refobjsubid
                     WHERE d.objid = CAST(quote_ident(s.schemaname) || '.' || quote_ident(s.sequencename) AS regclass) AND d.deptype IN ('a', 'i') LIMIT 1)
            FROM pg_sequences s WHERE s.schemaname = @s AND s.sequencename = @n
            """, new Dictionary<string, object> { ["@s"] = schema, ["@n"] = name }, cancellationToken);
        if (rows.Count == 0) throw new InvalidOperationException($"Sequenza «{name}» non trovata.");
        var r = rows[0];
        return new SequenceInfo(name, long.Parse(r[0]), long.Parse(r[1]), long.Parse(r[2]), long.Parse(r[3]), long.Parse(r[4]), r[5] == "true",
            r[6].Length == 0 ? null : long.Parse(r[6]), r[8].Length == 0 ? null : r[8], r[7]);
    }
}
