using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Navifox.Core.Models;

namespace Navifox.Dialects;

/// <summary>
/// SQLite (Microsoft.Data.Sqlite). Il campo «Host» del profilo contiene il percorso del file; il database è «main»
/// (più gli eventuali database collegati con ATTACH).
/// </summary>
public sealed class SqliteDialectProvider : AdoDialectProvider
{
    public override DatabaseKind Kind => DatabaseKind.Sqlite;

    public override async Task<DbConnection> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.Host) && string.IsNullOrWhiteSpace(profile.ConnectionStringOverride))
            throw new InvalidOperationException("Scegli il file del database SQLite.");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Environment.ExpandEnvironmentVariables(profile.Host.Trim().Trim('"')),
            Mode = profile.ReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            DefaultTimeout = 30,
        };
        if (!string.IsNullOrWhiteSpace(profile.Password)) builder.Password = profile.Password;
        if (!string.IsNullOrWhiteSpace(profile.ConnectionStringOverride)) builder.ConnectionString = profile.ConnectionStringOverride;
        ApplyAdvancedParameters(builder, profile.AdvancedParameters);
        if (profile.ReadOnly) builder.Mode = SqliteOpenMode.ReadOnly;
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await OpenAsync(connection, cancellationToken);
            ActivityRecording.Register(connection, profile);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public override string MapDataType(string genericType) => genericType.ToLowerInvariant() switch
    {
        "boolean" => "INTEGER", "integer" => "INTEGER", "bigint" => "INTEGER", "decimal" => "NUMERIC", "text" => "TEXT", "datetime" => "TEXT", "binary" => "BLOB", _ => genericType
    };

    protected override string BuildPagedSql(string schema, string table) => $"SELECT * FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} LIMIT @pageSize OFFSET @offset";
    protected override void BindPageParameters(DbCommand command, int offset, int pageSize)
    {
        AddParameter(command, "@offset", offset);
        AddParameter(command, "@pageSize", pageSize);
    }
    protected override DbCommand CreateCommand(DbConnection connection, string commandText) => NewCommand(connection, commandText);
    protected override void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static Dictionary<string, object> None => new();

    // ───────────── Navigazione ─────────────

    public override async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        (await ReadStringsAsync(connection, "SELECT name FROM pragma_database_list WHERE name <> 'temp' ORDER BY seq", None, cancellationToken)).Select(r => r[0]).ToArray();

    public override Task<IReadOnlyList<string>> ListSchemasAsync(DbConnection connection, string database, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([database]);

    private string Master(string schema) => $"{QuoteIdentifier(schema)}.sqlite_master";

    public override async Task<IReadOnlyList<DatabaseObjectInfo>> ListTablesAsync(DbConnection connection, string database, string schema, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection,
            $"SELECT name, upper(type) FROM {Master(schema)} WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite[_]%' ESCAPE '\\' ORDER BY name".Replace("[_]", "\\_"),
            None, cancellationToken);
        var sequences = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var r in await ReadStringsAsync(connection, $"SELECT name, seq FROM {QuoteIdentifier(schema)}.sqlite_sequence", None, cancellationToken))
                if (ParseLong(r[1]) is { } seq) sequences[r[0]] = seq;
        }
        catch (SqliteException) { /* nessuna tabella AUTOINCREMENT */ }
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (rows.Count(r => r[1] == "TABLE") <= 300)
            foreach (var table in rows.Where(r => r[1] == "TABLE"))
            {
                try { counts[table[0]] = await CountRowsAsync(connection, schema, table[0], null, cancellationToken); }
                catch (SqliteException) { /* tabella virtuale non leggibile: nessun conteggio */ }
            }
        return rows.Select(r => new DatabaseObjectInfo(r[0], r[1], EstimatedRows: counts.TryGetValue(r[0], out var n) ? n : null,
            AutoIncrement: sequences.TryGetValue(r[0], out var s) ? s : null)).ToArray();
    }

    public override async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
        (await ReadStringsAsync(connection, $"""
            SELECT name, type, "notnull", dflt_value, pk,
                   CASE WHEN pk = 1 AND upper(trim(type)) = 'INTEGER'
                        AND (SELECT count(*) FROM pragma_table_xinfo({FormatLiteral(table)}, {FormatLiteral(schema)}) WHERE pk > 0) = 1
                        AND NOT EXISTS (SELECT 1 FROM pragma_index_list({FormatLiteral(table)}, {FormatLiteral(schema)}) WHERE origin = 'pk')
                        THEN 1 ELSE 0 END, hidden
            FROM pragma_table_xinfo({FormatLiteral(table)}, {FormatLiteral(schema)}) ORDER BY cid
            """, None, cancellationToken))
        .Select(r => new ColumnInfo(r[0], r[1].Length == 0 ? "BLOB" : r[1], r[2] == "0", r[3].Length == 0 ? null : r[3], r[4] != "0", r[5] == "1", r[6] != "0")).ToArray();

    public override async Task<long> CountRowsAsync(DbConnection connection, string schema, string table, string? where, CancellationToken cancellationToken)
    {
        var sql = $"SELECT COUNT(*) FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)}" + (string.IsNullOrWhiteSpace(where) ? string.Empty : $" WHERE {where}");
        await using var command = CreateCommand(connection, sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    // ───────────── Proprietà ─────────────

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerPropertiesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var version = await ReadStringsAsync(connection, "SELECT sqlite_version()", None, cancellationToken);
        return [new("Versione SQLite", version.Count == 0 ? "—" : version[0][0]), new("File", OrDash(((SqliteConnection)connection).DataSource))];
    }

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetDatabasePropertiesAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        var q = QuoteIdentifier(database);
        string One(List<string[]> rows) => rows.Count == 0 ? string.Empty : rows[0][0];
        var pageSize = One(await ReadStringsAsync(connection, $"PRAGMA {q}.page_size", None, cancellationToken));
        var pages = One(await ReadStringsAsync(connection, $"PRAGMA {q}.page_count", None, cancellationToken));
        var free = One(await ReadStringsAsync(connection, $"PRAGMA {q}.freelist_count", None, cancellationToken));
        var journal = One(await ReadStringsAsync(connection, $"PRAGMA {q}.journal_mode", None, cancellationToken));
        var encoding = One(await ReadStringsAsync(connection, "PRAGMA encoding", None, cancellationToken));
        var userVersion = One(await ReadStringsAsync(connection, $"PRAGMA {q}.user_version", None, cancellationToken));
        var counts = await ReadStringsAsync(connection, $"SELECT SUM(type = 'table'), SUM(type = 'view'), SUM(type = 'index'), SUM(type = 'trigger') FROM {Master(database)} WHERE name NOT LIKE 'sqlite\\_%' ESCAPE '\\'", None, cancellationToken);
        var c = counts.Count == 0 ? new string[4] : counts[0];
        var file = await ReadStringsAsync(connection, "SELECT file FROM pragma_database_list WHERE name = @name", new Dictionary<string, object> { ["@name"] = database }, cancellationToken);
        var size = (ParseLong(pageSize) ?? 0) * (ParseLong(pages) ?? 0);
        return
        [
            new("File", OrDash(One(file))), new("Dimensione", FormatSize(size.ToString(CultureInfo.InvariantCulture))),
            new("Spazio libero", FormatSize(((ParseLong(pageSize) ?? 0) * (ParseLong(free) ?? 0)).ToString(CultureInfo.InvariantCulture))),
            new("Tabelle", FormatNumber(c[0])), new("Viste", FormatNumber(c[1])), new("Indici", FormatNumber(c[2])), new("Trigger", FormatNumber(c[3])),
            new("Codifica", OrDash(encoding)), new("Journal", OrDash(journal)), new("user_version", OrDash(userVersion)),
        ];
    }

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetTablePropertiesAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var info = await ReadStringsAsync(connection, $"SELECT type, sql FROM {Master(schema)} WHERE name = @name", new Dictionary<string, object> { ["@name"] = table }, cancellationToken);
        if (info.Count == 0) return [];
        var count = await CountRowsAsync(connection, schema, table, null, cancellationToken);
        var sql = info[0][1];
        var result = new List<KeyValuePair<string, string>>
        {
            new("Tipo", info[0][0] == "view" ? "Vista" : "Tabella"),
            new("Righe", count.ToString("N0")),
        };
        if (info[0][0] == "table")
        {
            result.Add(new("WITHOUT ROWID", Regex.IsMatch(sql, @"\bWITHOUT\s+ROWID\b", RegexOptions.IgnoreCase) ? "Sì" : "No"));
            result.Add(new("STRICT", Regex.IsMatch(sql, @"\)\s*(?:WITHOUT\s+ROWID\s*,\s*)?STRICT\b", RegexOptions.IgnoreCase) ? "Sì" : "No"));
            result.Add(new("AUTOINCREMENT", Regex.IsMatch(sql, @"\bAUTOINCREMENT\b", RegexOptions.IgnoreCase) ? "Sì" : "No"));
        }
        return result;
    }

    // ───────────── Progetta tabella e modello ─────────────

    public override IReadOnlyList<SchemaObjectKind> SchemaObjectKinds { get; } = [SchemaObjectKind.Trigger];

    private static readonly Regex TypeWithArgs = new(@"^(?<base>[^(]+?)\s*(?:\((?<a>\d+)\s*(?:,\s*(?<b>\d+))?\))?$", RegexOptions.Compiled);
    private static readonly Regex TriggerParts = new(
        @"^\s*CREATE\s+(?:TEMP(?:ORARY)?\s+)?TRIGGER\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:""(?:[^""]|"""")+""|\[[^\]]+\]|`[^`]+`|\S+)\s+(?<timing>BEFORE|AFTER|INSTEAD\s+OF)?\s*(?<events>DELETE|INSERT|UPDATE(?:\s+OF\s+.+?)?)\s+ON\s+(?:""(?:[^""]|"""")+""|\[[^\]]+\]|`[^`]+`|\S+)\s+(?<body>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public override async Task<TableDesign> GetTableDesignAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var name = FormatLiteral(table);
        var s = FormatLiteral(schema);
        var create = await ReadStringsAsync(connection, $"SELECT sql FROM {Master(schema)} WHERE type = 'table' AND name = @name", new Dictionary<string, object> { ["@name"] = table }, cancellationToken);
        var createSql = create.Count == 0 ? string.Empty : create[0][0];
        var autoIncrement = Regex.IsMatch(createSql, @"\bAUTOINCREMENT\b", RegexOptions.IgnoreCase);

        var columnRows = await ReadStringsAsync(connection, $"SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo({name}, {s}) ORDER BY cid", None, cancellationToken);
        var columns = columnRows.Select(r =>
        {
            var m = TypeWithArgs.Match(r[1].Trim());
            var type = m.Success ? m.Groups["base"].Value.Trim() : r[1];
            int? length = m.Success && m.Groups["a"].Success ? int.Parse(m.Groups["a"].Value) : null;
            int? decimals = m.Success && m.Groups["b"].Success ? int.Parse(m.Groups["b"].Value) : null;
            var generated = r[5] is "2" or "3";
            var keyOrder = int.Parse(r[4]);
            return new DesignColumn(r[0], r[0], type, length, decimals,
                NotNull: r[2] == "1",
                IsVirtual: generated,
                Expression: generated ? GeneratedExpression(createSql, r[0]) : null,
                KeyOrder: keyOrder,
                Default: r[3].Length == 0 ? null : r[3],
                AutoIncrement: autoIncrement && keyOrder == 1 && type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase),
                StoredGenerated: r[5] == "3");
        }).ToList();

        var indexes = new List<DesignIndex>();
        foreach (var index in await ReadStringsAsync(connection, $"SELECT name, \"unique\", origin, partial FROM pragma_index_list({name}, {s})", None, cancellationToken))
        {
            if (index[2] == "pk") continue;
            var parts = await ReadStringsAsync(connection, $"SELECT name, \"desc\" FROM pragma_index_xinfo({FormatLiteral(index[0])}, {s}) WHERE key = 1 ORDER BY seqno", None, cancellationToken);
            var constraint = index[2] == "u";
            var indexName = constraint && index[0].StartsWith("sqlite_autoindex_", StringComparison.Ordinal) ? $"uq_{table}_{string.Join("_", parts.Select(p => p[0]))}" : index[0];
            indexes.Add(new DesignIndex(index[0], indexName, parts.Select(p => p[1] == "1" ? p[0] + " DESC" : p[0]).ToArray(), index[1] == "1" ? "UNIQUE" : "NORMAL", IsConstraint: constraint));
        }

        var keyRows = await ReadStringsAsync(connection, $"SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete FROM pragma_foreign_key_list({name}, {s}) ORDER BY id, seq", None, cancellationToken);
        var keys = keyRows.GroupBy(r => r[0]).Select(g =>
        {
            var first = g.First();
            var keyName = $"fk_{table}_{first[2]}_{g.Key}";
            return new DesignForeignKey(keyName, keyName, g.Select(r => r[3]).ToArray(), string.Empty, first[2], g.Select(r => r[4]).ToArray(), first[6], first[5]);
        }).ToList();

        var triggers = (await ReadStringsAsync(connection, $"SELECT name, sql FROM {Master(schema)} WHERE type = 'trigger' AND tbl_name = @name ORDER BY name", new Dictionary<string, object> { ["@name"] = table }, cancellationToken))
            .Select(r =>
            {
                var m = TriggerParts.Match(r[1]);
                return m.Success
                    ? new DesignTrigger(r[0], r[0], m.Groups["timing"].Success && m.Groups["timing"].Value.Length > 0 ? Regex.Replace(m.Groups["timing"].Value.ToUpperInvariant(), @"\s+", " ") : "BEFORE",
                        m.Groups["events"].Value.Trim(), m.Groups["body"].Value.Trim().TrimEnd(';'))
                    : new DesignTrigger(r[0], r[0], "AFTER", "INSERT", r[1]);
            }).ToList();

        return new TableDesign(table, table, columns, indexes, keys, triggers, new TableDesignOptions(), null);
    }

    /// <summary>Espressione di una colonna generata (non esposta dai PRAGMA): letta dal CREATE TABLE.</summary>
    private static string? GeneratedExpression(string createSql, string column)
    {
        var m = Regex.Match(createSql, $@"(?:""{Regex.Escape(column.Replace("\"", "\"\""))}""|\b{Regex.Escape(column)}\b)[^,]*?\bAS\s*\(", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var start = m.Index + m.Length;
        var depth = 1;
        for (var i = start; i < createSql.Length; i++)
        {
            if (createSql[i] == '(') depth++;
            else if (createSql[i] == ')' && --depth == 0) return createSql[start..i].Trim();
        }
        return null;
    }

    public override async Task<SchemaModel> GetSchemaModelAsync(DbConnection connection, string database, string schema, CancellationToken cancellationToken)
    {
        var s = FormatLiteral(schema);
        var columns = await ReadStringsAsync(connection, $"""
            SELECT m.name, c.name, c.type, CASE WHEN c."notnull" = 1 THEN 'NO' ELSE 'YES' END, CASE WHEN c.pk > 0 THEN 'PRI' ELSE '' END
            FROM {Master(schema)} m JOIN pragma_table_info(m.name, {s}) c
            WHERE m.type IN ('table', 'view') AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\'
            ORDER BY m.name, c.cid
            """, None, cancellationToken);
        var keys = await ReadStringsAsync(connection, $"""
            SELECT 'fk_' || m.name || '_' || k."table" || '_' || k.id, m.name, k."from", '', k."table", k."to"
            FROM {Master(schema)} m JOIN pragma_foreign_key_list(m.name, {s}) k
            WHERE m.type = 'table'
            ORDER BY m.name, k.id, k.seq
            """, None, cancellationToken);
        var views = await ReadStringsAsync(connection, $"SELECT name FROM {Master(schema)} WHERE type = 'view'", None, cancellationToken);
        return BuildSchemaModel(columns, keys, views.Select(r => r[0]));
    }

    public override async Task<IReadOnlyList<DatabaseObjectInfo>> ListSchemaObjectsAsync(DbConnection connection, string database, string schema, SchemaObjectKind kind, CancellationToken cancellationToken)
    {
        if (kind != SchemaObjectKind.Trigger) return [];
        return (await ReadStringsAsync(connection, $"SELECT name, tbl_name, sql FROM {Master(schema)} WHERE type = 'trigger' ORDER BY name", None, cancellationToken))
            .Select(r =>
            {
                var m = TriggerParts.Match(r[2]);
                var summary = m.Success ? $"{(m.Groups["timing"].Value.Length == 0 ? "BEFORE" : m.Groups["timing"].Value.ToUpperInvariant())} {m.Groups["events"].Value.ToUpperInvariant()} su {r[1]}" : $"su {r[1]}";
                return new DatabaseObjectInfo(r[0], "TRIGGER", Detail: summary, Identity: r[1]);
            }).ToArray();
    }

    public override async Task<string> GetSchemaObjectDefinitionAsync(DbConnection connection, string database, string schema, SchemaObjectKind kind, DatabaseObjectInfo item, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, $"SELECT sql FROM {Master(schema)} WHERE name = @name AND type = @type",
            new Dictionary<string, object> { ["@name"] = item.ObjectName, ["@type"] = kind == SchemaObjectKind.View ? "view" : "trigger" }, cancellationToken);
        if (rows.Count == 0) throw new InvalidOperationException($"Definizione di «{item.Name}» non trovata.");
        var definition = rows[0][0].Trim();
        return kind == SchemaObjectKind.View ? Core.SchemaObjectSql.ExtractViewSelect(definition) : definition.TrimEnd(';') + ";";
    }

    public override Task<IReadOnlyList<string>> GetRoutineParametersAsync(DbConnection connection, string schema, DatabaseObjectInfo routine, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    // ───────────── «Server» Monitor: SQLite non ha processi né variabili di server ─────────────

    public override Task<QueryResult> GetProcessListAsync(DbConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(new QueryResult([new ColumnInfo("Id", "INTEGER", false), new ColumnInfo("User", "TEXT", true), new ColumnInfo("Host", "TEXT", true), new ColumnInfo("db", "TEXT", true),
            new ColumnInfo("Command", "TEXT", true), new ColumnInfo("Time", "INTEGER", true), new ColumnInfo("State", "TEXT", true), new ColumnInfo("Info", "TEXT", true)], [], 0, false));

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerVariablesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var pragma in new[] { "auto_vacuum", "busy_timeout", "cache_size", "encoding", "foreign_keys", "journal_mode", "locking_mode", "page_size", "synchronous", "temp_store", "user_version", "wal_autocheckpoint" })
        {
            var rows = await ReadStringsAsync(connection, $"PRAGMA {pragma}", None, cancellationToken);
            result.Add(new(pragma, rows.Count == 0 ? string.Empty : rows[0][0]));
        }
        return result;
    }

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerStatusAsync(DbConnection connection, CancellationToken cancellationToken) =>
        (await ReadStringsAsync(connection, "SELECT compile_options FROM pragma_compile_options", None, cancellationToken)).Select(r =>
        {
            var parts = r[0].Split('=', 2);
            return new KeyValuePair<string, string>(parts[0], parts.Length > 1 ? parts[1] : "sì");
        }).ToArray();

    public override async Task<ServerHealth> GetServerHealthAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var version = await ReadStringsAsync(connection, "SELECT sqlite_version()", None, cancellationToken);
        return new ServerHealth("SQLite " + (version.Count == 0 ? string.Empty : version[0][0]), null, null, null, null, null, null, null, null);
    }

    public override string BuildKillSql(long processId, bool queryOnly) => throw new NotSupportedException("SQLite non ha processi da terminare.");

    // ───────────── Database, DDL, dump ─────────────

    public override string MaintenanceDatabase => "main";
    public override bool SupportsDatabaseRename => false;
    public override bool UsesMySqlScriptSyntax => false;
    public override void ClearPools() => SqliteConnection.ClearAllPools();

    public override string BuildDropDatabaseSql(string database) =>
        database == "main" ? throw new NotSupportedException("Per eliminare un database SQLite elimina il file.") : $"DETACH DATABASE {QuoteIdentifier(database)}";

    public override async Task<TableDdl> GetDdlAsync(DbConnection connection, string database, string schema, string table, bool isView, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, $"SELECT sql FROM {Master(schema)} WHERE tbl_name = @name AND sql IS NOT NULL ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'view' THEN 0 WHEN 'index' THEN 1 ELSE 2 END, name",
            new Dictionary<string, object> { ["@name"] = table }, cancellationToken);
        return new TableDdl(string.Join("\n", rows.Select(r => r[0].Trim().TrimEnd(';') + ";")), []);
    }

    public override Task<IReadOnlyList<CharsetInfo>> ListCharsetsAsync(DbConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CharsetInfo>>([new CharsetInfo("UTF-8", "BINARY", ["BINARY", "NOCASE", "RTRIM"])]);

    public override Task<DatabaseOptions> GetDatabaseOptionsAsync(DbConnection connection, string database, CancellationToken cancellationToken) =>
        Task.FromResult(new DatabaseOptions(database, "UTF-8"));

    /// <summary>In SQLite un «nuovo database» è un altro file collegato alla connessione: ATTACH DATABASE 'file' AS nome.</summary>
    public override string BuildCreateDatabaseSql(DatabaseOptions options) =>
        $"ATTACH DATABASE {FormatLiteral(options.Name + ".db")} AS {QuoteIdentifier(options.Name)}";

    public override IReadOnlyList<string> BuildAlterDatabaseSql(DatabaseOptions current, DatabaseOptions updated) => [];

    public override string DumpHeader(string database) =>
        $"""
        -- Navifox SQL dump (SQLite)
        -- Database: {database}
        -- Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

        PRAGMA foreign_keys = OFF;
        BEGIN TRANSACTION;

        """;

    public override string DumpFooter() => "\nCOMMIT;\nPRAGMA foreign_keys = ON;\n";

    public override string FormatLiteral(object? value) => FormatCommonLiteral(value, text => "'" + text.Replace("'", "''") + "'",
        bytes => "X'" + Convert.ToHexString(bytes) + "'",
        flag => flag ? "1" : "0");
}
