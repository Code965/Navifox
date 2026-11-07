using System.Data;
using System.Data.Common;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.Dialects;

public abstract class AdoDialectProvider : IDialectProvider
{
    public abstract DatabaseKind Kind { get; }
    public abstract Task<DbConnection> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    public abstract string QuoteIdentifier(string identifier);
    public abstract string MapDataType(string genericType);
    protected abstract string BuildPagedSql(string schema, string table);
    protected abstract void BindPageParameters(DbCommand command, int offset, int pageSize);
    protected abstract DbCommand CreateCommand(DbConnection connection, string commandText);
    protected abstract void AddParameter(DbCommand command, string name, object value);

    public virtual async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var sql = Kind == DatabaseKind.MySql
            ? "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA ORDER BY SCHEMA_NAME"
            : "SELECT datname FROM pg_database WHERE datistemplate = FALSE ORDER BY datname";
        await using var command = CreateCommand(connection, sql);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    public virtual async Task<IReadOnlyList<string>> ListSchemasAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        var sql = Kind == DatabaseKind.MySql
            ? "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = @database ORDER BY SCHEMA_NAME"
            : "SELECT schema_name FROM information_schema.schemata WHERE catalog_name = @database AND schema_name <> 'information_schema' AND left(schema_name, 3) <> 'pg_' ORDER BY schema_name";
        await using var command = CreateCommand(connection, sql);
        AddParameter(command, "@database", database);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    public virtual async Task<IReadOnlyList<DatabaseObjectInfo>> ListTablesAsync(DbConnection connection, string database, string schema, CancellationToken cancellationToken)
    {
        var sql = Kind == DatabaseKind.MySql
            ? "SELECT TABLE_NAME, TABLE_TYPE, TABLE_ROWS, DATA_LENGTH, ENGINE, COALESCE(UPDATE_TIME, CREATE_TIME), TABLE_COMMENT, AUTO_INCREMENT FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @schema ORDER BY TABLE_NAME"
            : """
              SELECT t.table_name, CASE t.table_type WHEN 'BASE TABLE' THEN 'TABLE' ELSE t.table_type END,
                     CASE WHEN t.table_type <> 'BASE TABLE' THEN NULL WHEN c.reltuples >= 0 THEN c.reltuples::bigint ELSE s.n_live_tup END,
                     CASE WHEN t.table_type = 'BASE TABLE' THEN pg_total_relation_size(c.oid) END,
                     NULL::text, NULL::timestamp, obj_description(c.oid, 'pg_class'), NULL::bigint
              FROM information_schema.tables t
              LEFT JOIN pg_namespace n ON n.nspname = t.table_schema
              LEFT JOIN pg_class c ON c.relnamespace = n.oid AND c.relname = t.table_name
              LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
              WHERE t.table_catalog = @database AND t.table_schema = @schema
              ORDER BY t.table_name
              """;
        await using var command = CreateCommand(connection, sql);
        AddParameter(command, "@schema", schema);
        if (Kind == DatabaseKind.PostgreSql) AddParameter(command, "@database", database);
        var result = new List<DatabaseObjectInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DatabaseObjectInfo(
                reader.GetString(0),
                reader.IsDBNull(1) ? "TABLE" : reader.GetString(1),
                ReadInt64(reader, 2),
                ReadInt64(reader, 3),
                ReadString(reader, 4),
                ReadDateTime(reader, 5),
                ReadString(reader, 6),
                ReadInt64(reader, 7)));
        }
        return result;
    }

    public virtual async Task<long> CountRowsAsync(DbConnection connection, string schema, string table, string? where, CancellationToken cancellationToken)
    {
        var sql = $"SELECT COUNT(*) FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)}" + (string.IsNullOrWhiteSpace(where) ? string.Empty : $" WHERE {where}");
        await using var command = CreateCommand(connection, sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public virtual async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var sql = Kind == DatabaseKind.MySql
            ? "SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, COLUMN_KEY, EXTRA LIKE '%auto_increment%', COALESCE(GENERATION_EXPRESSION, '') <> '' FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION"
            : """
              SELECT c.column_name, c.data_type, c.is_nullable, c.column_default,
                     CASE WHEN EXISTS (
                         SELECT 1 FROM information_schema.table_constraints tc
                         JOIN information_schema.key_column_usage k
                           ON k.constraint_name = tc.constraint_name AND k.table_schema = tc.table_schema AND k.table_name = tc.table_name
                         WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = c.table_schema
                           AND tc.table_name = c.table_name AND k.column_name = c.column_name) THEN 'PRI' ELSE '' END,
                     (c.is_identity = 'YES' OR COALESCE(c.column_default LIKE 'nextval(%', FALSE)), c.is_generated <> 'NEVER'
              FROM information_schema.columns c
              WHERE c.table_catalog = @database AND c.table_schema = @schema AND c.table_name = @table
              ORDER BY c.ordinal_position
              """;
        await using var command = CreateCommand(connection, sql);
        AddParameter(command, "@schema", schema);
        AddParameter(command, "@table", table);
        if (Kind == DatabaseKind.PostgreSql) AddParameter(command, "@database", database);
        var result = new List<ColumnInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ColumnInfo(
                reader.GetString(0),
                reader.GetString(1),
                string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase),
                ReadString(reader, 3),
                string.Equals(ReadString(reader, 4), "PRI", StringComparison.OrdinalIgnoreCase),
                Convert.ToBoolean(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToBoolean(reader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return result;
    }

    /// <summary>Pagina di una tabella; <paramref name="pageSize"/> = 0 legge tutte le righe (nessun limite).</summary>
    public virtual async Task<DataPage> QueryPagedAsync(DbConnection connection, string schema, string table, int pageIndex, int pageSize, CancellationToken cancellationToken, string? where = null, string? orderBy = null)
    {
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (pageSize < 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var unlimited = pageSize == 0;
        var offset = unlimited ? 0 : checked((long)pageIndex * pageSize);
        var sql = BuildPagedSql(schema, table);
        var limit = sql.LastIndexOf(" LIMIT ", StringComparison.Ordinal);
        var clauses = (string.IsNullOrWhiteSpace(where) ? string.Empty : $" WHERE {where}")
                      + (string.IsNullOrWhiteSpace(orderBy) ? string.Empty : $" ORDER BY {orderBy}");
        sql = limit < 0 ? sql + clauses : unlimited ? sql[..limit] + clauses : sql[..limit] + clauses + sql[limit..];
        await using var command = CreateCommand(connection, sql);
        command.CommandTimeout = 0;
        if (!unlimited) BindPageParameters(command, checked((int)offset), checked(pageSize + 1));
        return await ReadPageAsync(command, pageIndex, pageSize, cancellationToken);
    }

    /// <summary>Legge una pagina: il comando chiede una riga in più per sapere se ce ne sono altre.</summary>
    protected static async Task<DataPage> ReadPageAsync(DbCommand command, int pageIndex, int pageSize, CancellationToken cancellationToken)
    {
        var unlimited = pageSize == 0;
        var rows = new List<IReadOnlyList<object?>>(unlimited ? 1024 : Math.Min(pageSize, 4096));
        var columns = new List<ColumnInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        for (var index = 0; index < reader.FieldCount; index++) columns.Add(new ColumnInfo(reader.GetName(index), reader.GetDataTypeName(index), true));
        var hasMore = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!unlimited && rows.Count == pageSize)
            {
                hasMore = true;
                break;
            }
            rows.Add(ReadRow(reader));
        }
        return new DataPage(columns, rows, pageIndex, pageSize, hasMore);
    }

    // ───────────── Modello relazionale e Server Monitor ─────────────

    public abstract IReadOnlyList<SchemaObjectKind> SchemaObjectKinds { get; }
    public abstract Task<IReadOnlyList<DatabaseObjectInfo>> ListSchemaObjectsAsync(DbConnection connection, string database, string schema, SchemaObjectKind kind, CancellationToken cancellationToken);
    public abstract Task<string> GetSchemaObjectDefinitionAsync(DbConnection connection, string database, string schema, SchemaObjectKind kind, DatabaseObjectInfo item, CancellationToken cancellationToken);
    public abstract Task<IReadOnlyList<string>> GetRoutineParametersAsync(DbConnection connection, string schema, DatabaseObjectInfo routine, CancellationToken cancellationToken);
    public virtual Task<SequenceInfo> GetSequenceAsync(DbConnection connection, string schema, string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Le sequenze non sono disponibili per questo database.");
    public abstract Task<TableDesign> GetTableDesignAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken);
    public abstract Task<SchemaModel> GetSchemaModelAsync(DbConnection connection, string database, string schema, CancellationToken cancellationToken);
    public abstract Task<QueryResult> GetProcessListAsync(DbConnection connection, CancellationToken cancellationToken);
    public abstract Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerVariablesAsync(DbConnection connection, CancellationToken cancellationToken);
    public abstract Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerStatusAsync(DbConnection connection, CancellationToken cancellationToken);
    public abstract Task<ServerHealth> GetServerHealthAsync(DbConnection connection, CancellationToken cancellationToken);
    public abstract string BuildKillSql(long processId, bool queryOnly);

    /// <summary>Costruisce il modello da righe (tabella, colonna, tipo, nullable, PRI) e (vincolo, tabella, colonna, schema rif., tabella rif., colonna rif.).</summary>
    protected static SchemaModel BuildSchemaModel(IEnumerable<string[]> columnRows, IEnumerable<string[]> keyRows, IEnumerable<string> views)
    {
        var tables = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);
        foreach (var row in columnRows)
        {
            if (!tables.TryGetValue(row[0], out var list)) tables[row[0]] = list = [];
            list.Add(new ColumnInfo(row[1], row[2], row[3] is "YES" or "true", null, row[4] == "PRI"));
        }
        var keys = keyRows.GroupBy(row => (Name: row[0], Table: row[1]))
            .Select(group => new ForeignKeyInfo(group.Key.Name, group.Key.Table,
                group.Select(row => row[2]).ToArray(), group.First()[3], group.First()[4], group.Select(row => row[5]).ToArray()))
            .ToArray();
        return new SchemaModel(tables.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ColumnInfo>)pair.Value, StringComparer.Ordinal), keys,
            new HashSet<string>(views, StringComparer.Ordinal));
    }

    protected static long? ParseLong(string? value) =>
        long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    // ───────────── Proprietà per il pannello informazioni ─────────────

    public abstract Task<IReadOnlyList<KeyValuePair<string, string>>> GetDatabasePropertiesAsync(DbConnection connection, string database, CancellationToken cancellationToken);
    public abstract Task<IReadOnlyList<KeyValuePair<string, string>>> GetTablePropertiesAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken);

    public virtual async Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerPropertiesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var version = await ReadStringsAsync(connection, "SELECT version()", new Dictionary<string, object>(), cancellationToken);
        return [new("Versione server", version.Count == 0 ? "—" : version[0][0]), new("Versione client", connection.ServerVersion is { Length: > 0 } v ? v : "—")];
    }

    /// <summary>Dimensione leggibile seguita dal valore esatto, es. «48,00 KB (49.152)».</summary>
    protected static string FormatSize(string raw)
    {
        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var bytes)) return raw.Length == 0 ? "—" : raw;
        var readable = bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):N2} MB",
            >= 1L << 10 => $"{bytes / 1024d:N2} KB",
            _ => $"{bytes:N0} byte"
        };
        return $"{readable} ({bytes:N0})";
    }

    protected static string OrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    protected static string FormatNumber(string raw) =>
        long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number.ToString("N0") : OrDash(raw);

    public async Task<QueryResult> ExecuteAsync(DbConnection connection, string sql, int maxRows, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, sql);
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<ColumnInfo>();
        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;
        // Il primo result set con colonne viene mostrato; gli altri vengono consumati.
        do
        {
            if (reader.FieldCount == 0 || columns.Count > 0)
            {
                while (await reader.ReadAsync(cancellationToken)) { }
                continue;
            }
            for (var index = 0; index < reader.FieldCount; index++) columns.Add(new ColumnInfo(reader.GetName(index), reader.GetDataTypeName(index), true));
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }
                rows.Add(ReadRow(reader));
            }
            if (truncated) break;
        } while (await reader.NextResultAsync(cancellationToken));
        var affected = reader.RecordsAffected;
        return new QueryResult(columns, rows, affected, truncated);
    }

    public async IAsyncEnumerable<IReadOnlyList<object?>> StreamRowsAsync(DbConnection connection, string schema, string table,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, $"SELECT * FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)}");
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) yield return ReadRow(reader);
    }

    public async IAsyncEnumerable<IReadOnlyList<object?>> StreamQueryAsync(DbConnection connection, string sql, Action<IReadOnlyList<ColumnInfo>> onColumns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, sql);
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<ColumnInfo>();
        for (var index = 0; index < reader.FieldCount; index++) columns.Add(new ColumnInfo(reader.GetName(index), reader.GetDataTypeName(index), true));
        onColumns(columns);
        while (await reader.ReadAsync(cancellationToken)) yield return ReadRow(reader);
    }

    public virtual string BuildInsert(string schema, string table, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("INSERT INTO ").Append(DumpName(schema, table)).Append(" (")
            .AppendJoin(", ", columns.Select(QuoteIdentifier)).Append(") VALUES\n");
        for (var r = 0; r < rows.Count; r++)
        {
            builder.Append("  (").AppendJoin(", ", rows[r].Select(FormatLiteral)).Append(')');
            builder.Append(r == rows.Count - 1 ? ";\n" : ",\n");
        }
        return builder.ToString();
    }

    /// <summary>Nome usato negli script di dump (MySQL: solo tabella, così il dump è importabile in un altro database).</summary>
    public virtual string DumpName(string schema, string table) => QuoteIdentifier(table);

    public abstract Task<TableDdl> GetDdlAsync(DbConnection connection, string database, string schema, string table, bool isView, CancellationToken cancellationToken);
    public abstract Task<IReadOnlyList<CharsetInfo>> ListCharsetsAsync(DbConnection connection, CancellationToken cancellationToken);
    public abstract Task<DatabaseOptions> GetDatabaseOptionsAsync(DbConnection connection, string database, CancellationToken cancellationToken);
    public abstract string BuildCreateDatabaseSql(DatabaseOptions options);
    public abstract IReadOnlyList<string> BuildAlterDatabaseSql(DatabaseOptions current, DatabaseOptions updated);
    public virtual string BuildDropDatabaseSql(string database) => $"DROP DATABASE {QuoteIdentifier(database)}";
    public abstract string MaintenanceDatabase { get; }
    public abstract void ClearPools();
    public abstract bool SupportsDatabaseRename { get; }
    public abstract bool UsesMySqlScriptSyntax { get; }
    public virtual IEnumerable<string> SplitScript(string script) => SqlScriptSplitter.Split(script, Kind);
    public abstract string DumpHeader(string database);
    public abstract string DumpFooter();
    public abstract string FormatLiteral(object? value);

    protected static string FormatCommonLiteral(object? value, Func<string, string> quoteString, Func<byte[], string> formatBinary, Func<bool, string> formatBool)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return value switch
        {
            null or DBNull => "NULL",
            bool flag => formatBool(flag),
            byte[] bytes => formatBinary(bytes),
            sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(value, invariant)!,
            float number => number.ToString("R", invariant),
            double number => number.ToString("R", invariant),
            decimal number => number.ToString(invariant),
            DateTime dateTime => quoteString(dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", invariant).TrimEnd('.')),
            DateTimeOffset offset => quoteString(offset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", invariant)),
            DateOnly date => quoteString(date.ToString("yyyy-MM-dd", invariant)),
            TimeOnly time => quoteString(time.ToString("HH:mm:ss.FFFFFF", invariant).TrimEnd('.')),
            TimeSpan span => quoteString(span.ToString("c", invariant)),
            Guid guid => quoteString(guid.ToString()),
            _ => quoteString(Convert.ToString(value, invariant) ?? string.Empty)
        };
    }

    protected static IReadOnlyList<object?> ReadRow(DbDataReader reader)
    {
        var values = new object?[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            try { values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index); }
            catch (Exception)
            {
                // Valori non rappresentabili in .NET (es. date MySQL 0000-00-00): si usa il testo grezzo.
                try { values[index] = reader.GetString(index); }
                catch (Exception) { values[index] = null; }
            }
        }
        return values;
    }

    protected async Task<List<string[]>> ReadStringsAsync(DbConnection connection, string sql, IReadOnlyDictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, sql);
        foreach (var (name, value) in parameters) AddParameter(command, name, value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++) row[index] = reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            result.Add(row);
        }
        return result;
    }

    protected static async Task<DbConnection> OpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Connessione in sola lettura: rende la sessione read-only lato server (oltre ai blocchi dell'interfaccia).</summary>
    protected static async Task ApplyReadOnlyAsync(DbConnection connection, ConnectionProfile profile, string sql, CancellationToken cancellationToken)
    {
        if (!profile.ReadOnly) return;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected static void ApplyAdvancedParameters(DbConnectionStringBuilder builder, string parameters)
    {
        foreach (var segment in parameters.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0) throw new FormatException($"Parametro avanzato non valido: {segment}");
            builder[segment[..separator].Trim()] = segment[(separator + 1)..].Trim();
        }
    }

    protected static async Task<SshTunnelLease?> OpenTunnelAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (!profile.UseSshTunnel) return null;
        if (!string.IsNullOrWhiteSpace(profile.ConnectionStringOverride))
            throw new InvalidOperationException("La stringa di connessione personalizzata non può essere combinata con il tunnel SSH.");
        return await SshTunnelLease.OpenAsync(profile, cancellationToken);
    }

    protected static DbCommand NewCommand(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return ActivityRecording.Wrap(command);
    }

    private static long? ReadInt64(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    private static string? ReadString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    private static DateTimeOffset? ReadDateTime(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc));
}
