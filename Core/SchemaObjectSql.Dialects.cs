using Navifox.Core.Models;

namespace Navifox.Core;

/// <summary>Varianti per dialetto (SQL Server, SQLite) di <see cref="SchemaObjectSql"/>; MySQL e PostgreSQL usano le versioni originali.</summary>
public static partial class SchemaObjectSql
{
    private static string QMs(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Qualified(DatabaseKind kind, string schema, string name) => kind switch
    {
        DatabaseKind.MySql => QMy(name),
        DatabaseKind.SqlServer => $"{QMs(schema)}.{QMs(name)}",
        DatabaseKind.Sqlite => QPg(name),
        _ => $"{QPg(schema)}.{QPg(name)}",
    };

    public static string DropSql(DatabaseKind kind, string schema, SchemaObjectKind objectKind, DatabaseObjectInfo item) => kind switch
    {
        DatabaseKind.SqlServer => objectKind switch
        {
            SchemaObjectKind.Routine => $"DROP {RoutineType(item)} IF EXISTS {Qualified(kind, schema, item.ObjectName)}",
            SchemaObjectKind.Trigger => $"DROP TRIGGER IF EXISTS {Qualified(kind, schema, item.ObjectName)}",
            SchemaObjectKind.Sequence => $"DROP SEQUENCE IF EXISTS {Qualified(kind, schema, item.ObjectName)}",
            _ => $"DROP VIEW IF EXISTS {Qualified(kind, schema, item.ObjectName)}",
        },
        DatabaseKind.Sqlite => objectKind == SchemaObjectKind.Trigger
            ? $"DROP TRIGGER IF EXISTS {QPg(item.ObjectName)}"
            : $"DROP VIEW IF EXISTS {QPg(item.ObjectName)}",
        _ => DropSql(kind == DatabaseKind.MySql, schema, objectKind, item),
    };

    /// <summary>SQL Server e SQLite hanno DDL transazionale: la versione precedente si elimina e si ricrea in un'unica transazione.</summary>
    public static SchemaObjectSavePlan BuildSave(DatabaseKind kind, string schema, SchemaObjectKind objectKind, DatabaseObjectInfo? original, string? originalDefinition, string definition)
    {
        if (kind is DatabaseKind.MySql or DatabaseKind.PostgreSql) return BuildSave(kind == DatabaseKind.MySql, schema, objectKind, original, originalDefinition, definition);
        if (objectKind == SchemaObjectKind.View) throw new ArgumentException("Per le viste usare BuildViewSave.");
        var create = definition.Trim().TrimEnd(';').Trim();
        // SQLite: il corpo di un trigger termina con «END;» e il punto e virgola interno va conservato.
        if (kind == DatabaseKind.Sqlite && create.EndsWith("END", StringComparison.OrdinalIgnoreCase)) create += ";";
        if (original is null) return new SchemaObjectSavePlan([create]);
        return new SchemaObjectSavePlan([DropSql(kind, schema, objectKind, original), create], Atomic: true);
    }

    public static SchemaObjectSavePlan BuildViewSave(DatabaseKind kind, string schema, string? originalName, string name, string select)
    {
        if (kind is DatabaseKind.MySql or DatabaseKind.PostgreSql) return BuildViewSave(kind == DatabaseKind.MySql, schema, originalName, name, select);
        var body = select.Trim().TrimEnd(';').Trim();
        var target = Qualified(kind, schema, name);
        var create = $"CREATE VIEW {target} AS\n{body}";
        if (originalName is null) return new SchemaObjectSavePlan([create]);
        var recreate = new SchemaObjectSavePlan([$"DROP VIEW {Qualified(kind, schema, originalName)}", create], Atomic: true);
        // SQL Server: CREATE OR ALTER mantiene i permessi della vista.
        return kind == DatabaseKind.SqlServer && originalName == name
            ? new SchemaObjectSavePlan([$"CREATE OR ALTER VIEW {target} AS\n{body}"], Fallback: recreate)
            : recreate;
    }

    public static string Template(DatabaseKind kind, string schema, SchemaObjectKind objectKind, string? table = null, string routineType = "FUNCTION")
    {
        if (kind is DatabaseKind.MySql or DatabaseKind.PostgreSql) return Template(kind == DatabaseKind.MySql, schema, objectKind, table, routineType);
        var target = table ?? "nome_tabella";
        return (kind, objectKind, routineType) switch
        {
            (DatabaseKind.SqlServer, SchemaObjectKind.Routine, "PROCEDURE") => $"""
                CREATE PROCEDURE {QMs(schema)}.[nuova_procedura]
                  @id INT
                AS
                BEGIN
                  SET NOCOUNT ON;
                  SELECT @id AS id;
                END
                """,
            (DatabaseKind.SqlServer, SchemaObjectKind.Routine, _) => $"""
                CREATE FUNCTION {QMs(schema)}.[nuova_funzione] (@valore INT)
                RETURNS INT
                AS
                BEGIN
                  RETURN @valore * 2;
                END
                """,
            (DatabaseKind.SqlServer, SchemaObjectKind.Trigger, _) => $"""
                CREATE TRIGGER {QMs(schema)}.[tr_{target}_ai] ON {Qualified(kind, schema, target)}
                AFTER INSERT
                AS
                BEGIN
                  SET NOCOUNT ON;
                  -- es. UPDATE t SET creato = SYSDATETIME() FROM {Qualified(kind, schema, target)} t JOIN inserted i ON i.id = t.id;
                END
                """,
            (DatabaseKind.Sqlite, SchemaObjectKind.Trigger, _) => $"""
                CREATE TRIGGER "tr_{target}_ai" AFTER INSERT ON {QPg(target)}
                FOR EACH ROW
                BEGIN
                  -- es. UPDATE {QPg(target)} SET creato = CURRENT_TIMESTAMP WHERE rowid = NEW.rowid;
                  SELECT 1;
                END;
                """,
            _ => "SELECT *\nFROM " + Qualified(kind, schema, target),
        };
    }

    public static string RunTemplate(DatabaseKind kind, string schema, DatabaseObjectInfo routine, IReadOnlyList<string> parameters)
    {
        if (kind != DatabaseKind.SqlServer) return RunTemplate(kind == DatabaseKind.MySql, schema, routine, parameters);
        var name = Qualified(kind, schema, routine.ObjectName);
        if (RoutineType(routine) == "PROCEDURE")
            return parameters.Count == 0 ? $"EXEC {name};" : $"EXEC {name} {string.Join(", ", parameters.Select(p => $"{p.Split(' ')[0]} = NULL"))};";
        var args = string.Join(", ", parameters.Select(p => $"/* {p} */ NULL"));
        return routine.Kind.Contains("TABLE", StringComparison.OrdinalIgnoreCase) ? $"SELECT * FROM {name}({args});" : $"SELECT {name}({args});";
    }
}
