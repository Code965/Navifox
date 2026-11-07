using System.Text;
using System.Text.RegularExpressions;
using Navifox.Core.Models;

namespace Navifox.Core;

/// <summary>SQL per creare, modificare ed eliminare funzioni, procedure, trigger, eventi, viste e sequenze.</summary>
public static partial class SchemaObjectSql
{
    private static string QMy(string identifier) => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    private static string QPg(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string Q(bool mysql, string identifier) => mysql ? QMy(identifier) : QPg(identifier);
    private static string Qualified(bool mysql, string schema, string name) => mysql ? QMy(name) : $"{QPg(schema)}.{QPg(name)}";

    public static string DropSql(bool mysql, string schema, SchemaObjectKind kind, DatabaseObjectInfo item) => kind switch
    {
        SchemaObjectKind.Routine when mysql => $"DROP {RoutineType(item)} IF EXISTS {QMy(item.ObjectName)}",
        SchemaObjectKind.Routine => $"DROP {RoutineType(item)} IF EXISTS {Qualified(false, schema, item.ObjectName)}({item.Identity})",
        SchemaObjectKind.Trigger when mysql => $"DROP TRIGGER IF EXISTS {QMy(item.ObjectName)}",
        SchemaObjectKind.Trigger => $"DROP TRIGGER IF EXISTS {QPg(item.ObjectName)} ON {Qualified(false, schema, item.Identity ?? string.Empty)}",
        SchemaObjectKind.Event => $"DROP EVENT IF EXISTS {QMy(item.ObjectName)}",
        SchemaObjectKind.Sequence => $"DROP SEQUENCE IF EXISTS {Qualified(false, schema, item.ObjectName)}",
        _ => $"DROP VIEW IF EXISTS {Qualified(mysql, schema, item.ObjectName)}",
    };

    private static string RoutineType(DatabaseObjectInfo item) => item.Kind.Contains("PROC", StringComparison.OrdinalIgnoreCase) ? "PROCEDURE" : "FUNCTION";

    /// <summary>
    /// Salvataggio di una definizione completa (CREATE FUNCTION/PROCEDURE/TRIGGER/EVENT).
    /// MySQL non ha DDL transazionale: si elimina e si ricrea, e se la nuova versione non viene accettata si ripristina quella precedente.
    /// PostgreSQL: funzioni con CREATE OR REPLACE; trigger eliminati e ricreati in un'unica transazione.
    /// </summary>
    public static SchemaObjectSavePlan BuildSave(bool mysql, string schema, SchemaObjectKind kind, DatabaseObjectInfo? original, string? originalDefinition, string definition)
    {
        var create = definition.Trim().TrimEnd(';').Trim();
        if (kind == SchemaObjectKind.View) throw new ArgumentException("Per le viste usare BuildViewSave.");
        if (original is null) return new SchemaObjectSavePlan([create]);
        if (mysql)
            return new SchemaObjectSavePlan([DropSql(true, schema, kind, original), create],
                Restore: string.IsNullOrWhiteSpace(originalDefinition) ? null : [originalDefinition.Trim().TrimEnd(';')]);
        return kind == SchemaObjectKind.Routine
            ? new SchemaObjectSavePlan([create], Fallback: new SchemaObjectSavePlan([DropSql(false, schema, kind, original), create], Atomic: true))
            : new SchemaObjectSavePlan([DropSql(false, schema, kind, original), create], Atomic: true);
    }

    /// <summary>Salvataggio di una vista a partire dalla sola SELECT.</summary>
    public static SchemaObjectSavePlan BuildViewSave(bool mysql, string schema, string? originalName, string name, string select)
    {
        var body = select.Trim().TrimEnd(';').Trim();
        var target = Qualified(mysql, schema, name);
        var replace = $"CREATE OR REPLACE VIEW {target} AS\n{body}";
        if (originalName is null) return new SchemaObjectSavePlan([$"CREATE VIEW {target} AS\n{body}"]);
        var renamed = originalName != name;
        if (mysql)
            return new SchemaObjectSavePlan(renamed ? [replace, $"DROP VIEW IF EXISTS {QMy(originalName)}"] : [replace]);
        var recreate = new SchemaObjectSavePlan([$"DROP VIEW {Qualified(false, schema, originalName)}", $"CREATE VIEW {target} AS\n{body}"], Atomic: true);
        // CREATE OR REPLACE non permette di togliere o rinominare colonne: in quel caso si ricrea la vista.
        return renamed ? recreate : new SchemaObjectSavePlan([replace], Fallback: recreate);
    }

    /// <summary>Definizione iniziale di un oggetto nuovo.</summary>
    public static string Template(bool mysql, string schema, SchemaObjectKind kind, string? table = null, string routineType = "FUNCTION")
    {
        var target = table ?? "nome_tabella";
        return (kind, mysql, routineType) switch
        {
            (SchemaObjectKind.Routine, true, "PROCEDURE") => """
                CREATE PROCEDURE `nuova_procedura`(IN p_id INT)
                BEGIN
                  SELECT p_id;
                END
                """,
            (SchemaObjectKind.Routine, true, _) => """
                CREATE FUNCTION `nuova_funzione`(p_valore INT) RETURNS INT
                DETERMINISTIC
                BEGIN
                  RETURN p_valore * 2;
                END
                """,
            (SchemaObjectKind.Routine, false, "PROCEDURE") => $"""
                CREATE OR REPLACE PROCEDURE {QPg(schema)}.nuova_procedura(p_id integer)
                LANGUAGE plpgsql
                AS $$
                BEGIN
                  RAISE NOTICE 'id %', p_id;
                END;
                $$
                """,
            (SchemaObjectKind.Routine, false, _) => $"""
                CREATE OR REPLACE FUNCTION {QPg(schema)}.nuova_funzione(p_valore integer)
                RETURNS integer
                LANGUAGE plpgsql
                AS $$
                BEGIN
                  RETURN p_valore * 2;
                END;
                $$
                """,
            (SchemaObjectKind.Trigger, true, _) => $"""
                CREATE TRIGGER `tr_{target}_bi` BEFORE INSERT ON {QMy(target)}
                FOR EACH ROW
                BEGIN
                  -- es. SET NEW.created_at = NOW();
                END
                """,
            (SchemaObjectKind.Trigger, false, _) => $"""
                CREATE TRIGGER tr_{target}_bi BEFORE INSERT ON {Qualified(false, schema, target)}
                FOR EACH ROW EXECUTE FUNCTION {QPg(schema)}.nome_funzione()
                """,
            (SchemaObjectKind.Event, _, _) => """
                CREATE EVENT `nuovo_evento`
                ON SCHEDULE EVERY 1 DAY STARTS CURRENT_TIMESTAMP
                ON COMPLETION PRESERVE ENABLE
                COMMENT 'Pulizia giornaliera'
                DO
                  DELETE FROM log WHERE creato < NOW() - INTERVAL 30 DAY
                """,
            _ => "SELECT *\nFROM " + (mysql ? QMy(target) : Qualified(false, schema, target)),
        };
    }

    /// <summary>Istruzione per provare una funzione o procedura (aperta in una nuova query).</summary>
    public static string RunTemplate(bool mysql, string schema, DatabaseObjectInfo routine, IReadOnlyList<string> parameters)
    {
        var name = Qualified(mysql, schema, routine.ObjectName);
        var args = string.Join(", ", parameters.Select(p => $"/* {p} */ NULL"));
        return RoutineType(routine) == "PROCEDURE" ? $"CALL {name}({args});" : $"SELECT {name}({args});";
    }

    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>CREATE/ALTER SEQUENCE (PostgreSQL) dal modulo Progetta sequenza.</summary>
    public static IReadOnlyList<string> SequenceSql(string schema, SequenceInfo? original, SequenceInfo target)
    {
        var statements = new List<string>();
        var name = Qualified(false, schema, target.Name);
        string Bounds(SequenceInfo s) =>
            $" INCREMENT BY {s.Increment} {(s.Min is { } min ? $"MINVALUE {min}" : "NO MINVALUE")} {(s.Max is { } max ? $"MAXVALUE {max}" : "NO MAXVALUE")} START WITH {s.Start} CACHE {Math.Max(1, s.Cache)} {(s.Cycle ? "CYCLE" : "NO CYCLE")}";
        string Owned(string? owner) => string.IsNullOrWhiteSpace(owner) ? "NONE" : string.Join(".", owner.Split('.').Select(part => Identifier.IsMatch(part) ? part : QPg(part)));
        if (original is null)
        {
            var sql = new StringBuilder($"CREATE SEQUENCE {name} AS {target.DataType}{Bounds(target)}");
            statements.Add(sql.ToString());
            if (!string.IsNullOrWhiteSpace(target.OwnedBy)) statements.Add($"ALTER SEQUENCE {name} OWNED BY {Owned(target.OwnedBy)}");
            if (target.Current is { } value) statements.Add($"SELECT setval('{name.Replace("'", "''")}', {value}, true)");
            return statements;
        }
        var current = Qualified(false, schema, original.Name);
        if (original.Name != target.Name)
        {
            statements.Add($"ALTER SEQUENCE {current} RENAME TO {QPg(target.Name)}");
            current = name;
        }
        var bounds = original with { Name = target.Name, Current = null, OwnedBy = null } != target with { Current = null, OwnedBy = null };
        if (bounds) statements.Add($"ALTER SEQUENCE {current} AS {target.DataType}{Bounds(target)}");
        if ((original.OwnedBy ?? string.Empty) != (target.OwnedBy ?? string.Empty)) statements.Add($"ALTER SEQUENCE {current} OWNED BY {Owned(target.OwnedBy)}");
        if (target.Current is { } next && next != original.Current) statements.Add($"SELECT setval('{current.Replace("'", "''")}', {next}, true)");
        return statements;
    }

    /// <summary>MySQL: SELECT di una vista da «SHOW CREATE VIEW» (tutto ciò che segue «VIEW nome AS»).</summary>
    public static string ExtractViewSelect(string createView)
    {
        var m = Regex.Match(createView, @"\bVIEW\s+(?:`(?:[^`]|``)+`|""(?:[^""]|"""")+""|\S+?)(?:\.(?:`(?:[^`]|``)+`|\S+?))?\s+(?:\([^)]*\)\s+)?AS\s+(?<body>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups["body"].Value.Trim() : createView;
    }

    /// <summary>Nome dell'oggetto definito da un CREATE (per riconoscere una rinomina o il nome di un oggetto nuovo).</summary>
    public static string? DefinedName(string definition)
    {
        var m = Regex.Match(definition, @"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:DEFINER\s*=\s*\S+\s+)?(?:CONSTRAINT\s+)?(?:FUNCTION|PROCEDURE|TRIGGER|EVENT)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?:`[^`]+`|""[^""]+""|[A-Za-z_][\w$]*)\.)?(?<name>`[^`]+`|""[^""]+""|[A-Za-z_][\w$]*)",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["name"].Value.Trim('`', '"') : null;
    }
}
