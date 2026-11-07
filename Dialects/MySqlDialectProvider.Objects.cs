using System.Data.Common;
using System.Globalization;
using MosaicDB.Core.Models;

namespace MosaicDB.Dialects;

/// <summary>Funzioni, procedure, trigger, eventi e viste (MySQL/MariaDB).</summary>
public sealed partial class MySqlDialectProvider
{
    public override IReadOnlyList<SchemaObjectKind> SchemaObjectKinds { get; } = [SchemaObjectKind.Routine, SchemaObjectKind.Trigger, SchemaObjectKind.Event];

    internal static DateTimeOffset? ParseDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? new DateTimeOffset(date) : null;

    public override async Task<IReadOnlyList<DatabaseObjectInfo>> ListSchemaObjectsAsync(DbConnection connection, string database, string schema, SchemaObjectKind kind, CancellationToken cancellationToken)
    {
        var p = new Dictionary<string, object> { ["@s"] = schema };
        switch (kind)
        {
            case SchemaObjectKind.Routine:
                return (await ReadStringsAsync(connection, """
                    SELECT ROUTINE_NAME, ROUTINE_TYPE, DTD_IDENTIFIER, LAST_ALTERED, ROUTINE_COMMENT
                    FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_SCHEMA = @s ORDER BY ROUTINE_NAME
                    """, p, cancellationToken))
                    .Select(r => new DatabaseObjectInfo(r[0], r[1], ModifiedAt: ParseDate(r[3]), Comment: NullIfEmpty(r[4]),
                        Detail: r[1] == "FUNCTION" ? $"Funzione → {r[2]}" : "Procedura"))
                    .ToArray();
            case SchemaObjectKind.Trigger:
                return (await ReadStringsAsync(connection, """
                    SELECT TRIGGER_NAME, EVENT_OBJECT_TABLE, ACTION_TIMING, EVENT_MANIPULATION, CREATED
                    FROM INFORMATION_SCHEMA.TRIGGERS WHERE TRIGGER_SCHEMA = @s ORDER BY TRIGGER_NAME
                    """, p, cancellationToken))
                    .Select(r => new DatabaseObjectInfo(r[0], "TRIGGER", ModifiedAt: ParseDate(r[4]), Detail: $"{r[2]} {r[3]} su {r[1]}", Identity: r[1]))
                    .ToArray();
            case SchemaObjectKind.Event:
                return (await ReadStringsAsync(connection, """
                    SELECT EVENT_NAME, STATUS, EVENT_TYPE, INTERVAL_VALUE, INTERVAL_FIELD, EXECUTE_AT, LAST_ALTERED, EVENT_COMMENT, LAST_EXECUTED
                    FROM INFORMATION_SCHEMA.EVENTS WHERE EVENT_SCHEMA = @s ORDER BY EVENT_NAME
                    """, p, cancellationToken))
                    .Select(r => new DatabaseObjectInfo(r[0], "EVENT", ModifiedAt: ParseDate(r[6]), Comment: NullIfEmpty(r[7]),
                        Detail: (r[2] == "RECURRING" ? $"Ogni {r[3]} {r[4]}" : $"Una volta: {r[5]}") + $" · {r[1]}" + (r[8].Length > 0 ? $" · ultima esecuzione {r[8]}" : string.Empty),
                        Engine: r[1]))
                    .ToArray();
            default:
                return [];
        }
    }

    public override async Task<string> GetSchemaObjectDefinitionAsync(DbConnection connection, string database, string schema, SchemaObjectKind kind, DatabaseObjectInfo item, CancellationToken cancellationToken)
    {
        var name = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(item.ObjectName)}";
        var (sql, column) = kind switch
        {
            SchemaObjectKind.Routine => ($"SHOW CREATE {(item.Kind.Contains("PROC", StringComparison.OrdinalIgnoreCase) ? "PROCEDURE" : "FUNCTION")} {name}", 2),
            SchemaObjectKind.Trigger => ($"SHOW CREATE TRIGGER {name}", 2),
            SchemaObjectKind.Event => ($"SHOW CREATE EVENT {name}", 3),
            SchemaObjectKind.View => ($"SHOW CREATE VIEW {name}", 1),
            _ => throw new NotSupportedException($"{kind} non disponibile su MySQL."),
        };
        var rows = await ReadStringsAsync(connection, sql, new Dictionary<string, object>(), cancellationToken);
        var definition = rows.Count == 0 || rows[0].Length <= column ? string.Empty : rows[0][column];
        if (definition.Length == 0)
            throw new InvalidOperationException($"Il server non ha restituito la definizione di «{item.ObjectName}»: serve il privilegio SHOW VIEW / SELECT su mysql.proc o essere il proprietario (DEFINER).");
        return kind == SchemaObjectKind.View ? Core.SchemaObjectSql.ExtractViewSelect(definition) : definition;
    }

    public override async Task<IReadOnlyList<string>> GetRoutineParametersAsync(DbConnection connection, string schema, DatabaseObjectInfo routine, CancellationToken cancellationToken) =>
        (await ReadStringsAsync(connection, """
            SELECT PARAMETER_MODE, PARAMETER_NAME, DTD_IDENTIFIER FROM INFORMATION_SCHEMA.PARAMETERS
            WHERE SPECIFIC_SCHEMA = @s AND SPECIFIC_NAME = @n AND ORDINAL_POSITION > 0 ORDER BY ORDINAL_POSITION
            """, new Dictionary<string, object> { ["@s"] = schema, ["@n"] = routine.ObjectName }, cancellationToken))
        .Select(r => $"{(r[0].Length > 0 && r[0] != "IN" ? r[0] + " " : string.Empty)}{r[1]} {r[2]}").ToArray();
}
