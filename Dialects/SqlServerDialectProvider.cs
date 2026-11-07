using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Navifox.Core.Models;

namespace Navifox.Dialects;

/// <summary>
/// Microsoft SQL Server / Azure SQL (Microsoft.Data.SqlClient). Con il nome utente vuoto si usa l'autenticazione di Windows;
/// le istanze con nome si indicano come «server\ISTANZA».
/// </summary>
public sealed partial class SqlServerDialectProvider : AdoDialectProvider
{
    public override DatabaseKind Kind => DatabaseKind.SqlServer;

    public override async Task<DbConnection> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var tunnel = await OpenTunnelAsync(profile, cancellationToken);
        var endpoint = tunnel?.UseLocalEndpoint(profile) ?? profile;
        var host = string.IsNullOrWhiteSpace(endpoint.Host) ? "localhost" : endpoint.Host.Trim();
        // Istanza con nome senza porta esplicita: la risolve il servizio SQL Browser (o la memoria condivisa in locale).
        var namedInstance = host.Contains('\\');
        var dataSource = namedInstance && (endpoint.Port is 0 or 1433) ? host : $"{host},{endpoint.EffectivePort}";
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = endpoint.DefaultDatabase ?? string.Empty,
            ApplicationName = "Navifox",
            ConnectTimeout = 15,
        };
        if (string.IsNullOrWhiteSpace(endpoint.UserName)) builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = endpoint.UserName;
            builder.Password = endpoint.Password;
        }
        switch (endpoint.SslMode.ToLowerInvariant())
        {
            case "disable":
                builder.Encrypt = SqlConnectionEncryptOption.Optional;
                builder.TrustServerCertificate = true;
                break;
            case "require":
                builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
                builder.TrustServerCertificate = true;
                break;
            case "verifyca" or "verifyfull" or "verify-ca" or "verify-full":
                builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
                builder.TrustServerCertificate = false;
                break;
            default:
                builder.Encrypt = SqlConnectionEncryptOption.Optional;
                builder.TrustServerCertificate = true;
                break;
        }
        if (!string.IsNullOrWhiteSpace(endpoint.ConnectionStringOverride)) builder.ConnectionString = endpoint.ConnectionStringOverride;
        ApplyAdvancedParameters(builder, endpoint.AdvancedParameters);
        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await OpenAsync(connection, cancellationToken);
            tunnel?.Attach(connection);
            ActivityRecording.Register(connection, profile);
            return connection;
        }
        catch
        {
            tunnel?.Dispose();
            await connection.DisposeAsync();
            throw;
        }
    }

    public override string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    public override string MapDataType(string genericType) => genericType.ToLowerInvariant() switch
    {
        "boolean" => "BIT", "integer" => "INT", "bigint" => "BIGINT", "decimal" => "DECIMAL(18,2)", "text" => "NVARCHAR(MAX)", "datetime" => "DATETIME2", "binary" => "VARBINARY(MAX)", _ => genericType
    };

    protected override string BuildPagedSql(string schema, string table) => $"SELECT * FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
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

    /// <summary>OFFSET/FETCH richiede un ORDER BY: senza ordinamento scelto si usa l'ordine naturale.</summary>
    public override async Task<DataPage> QueryPagedAsync(DbConnection connection, string schema, string table, int pageIndex, int pageSize, CancellationToken cancellationToken, string? where = null, string? orderBy = null)
    {
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (pageSize < 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var sql = BuildPagedSql(schema, table) + (string.IsNullOrWhiteSpace(where) ? string.Empty : $" WHERE {where}");
        if (pageSize > 0)
            sql += $" ORDER BY {(string.IsNullOrWhiteSpace(orderBy) ? "(SELECT NULL)" : orderBy)} OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY";
        else if (!string.IsNullOrWhiteSpace(orderBy)) sql += $" ORDER BY {orderBy}";
        await using var command = CreateCommand(connection, sql);
        command.CommandTimeout = 0;
        if (pageSize > 0) BindPageParameters(command, checked(pageIndex * pageSize), checked(pageSize + 1));
        return await ReadPageAsync(command, pageIndex, pageSize, cancellationToken);
    }

    // ───────────── Navigazione ─────────────

    public override async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        (await ReadStringsAsync(connection, "SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1 AND state = 0 ORDER BY CASE WHEN database_id <= 4 THEN 1 ELSE 0 END, name",
            new Dictionary<string, object>(), cancellationToken)).Select(r => r[0]).ToArray();

    public override async Task<IReadOnlyList<string>> ListSchemasAsync(DbConnection connection, string database, CancellationToken cancellationToken) =>
        (await ReadStringsAsync(connection, """
            SELECT s.name FROM sys.schemas s
            WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest') AND s.name NOT LIKE 'db[_]%'
               OR EXISTS (SELECT 1 FROM sys.objects o WHERE o.schema_id = s.schema_id AND o.is_ms_shipped = 0) AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
            ORDER BY CASE s.name WHEN 'dbo' THEN 0 ELSE 1 END, s.name
            """, new Dictionary<string, object>(), cancellationToken)).Select(r => r[0]).ToArray();

    public override async Task<IReadOnlyList<DatabaseObjectInfo>> ListTablesAsync(DbConnection connection, string database, string schema, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, """
            SELECT o.name, CASE o.type WHEN 'V' THEN 'VIEW' ELSE 'TABLE' END,
                   (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)),
                   (SELECT SUM(a.used_pages) * 8192 FROM sys.partitions p JOIN sys.allocation_units a ON a.container_id = p.partition_id WHERE p.object_id = o.object_id),
                   CONVERT(varchar(23), o.modify_date, 126),
                   CAST((SELECT ep.value FROM sys.extended_properties ep WHERE ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description') AS nvarchar(4000)),
                   (SELECT CAST(IDENT_CURRENT(QUOTENAME(SCHEMA_NAME(o.schema_id)) + '.' + QUOTENAME(o.name)) AS bigint) WHERE OBJECTPROPERTY(o.object_id, 'TableHasIdentity') = 1)
            FROM sys.objects o
            WHERE o.schema_id = SCHEMA_ID(@schema) AND o.type IN ('U', 'V') AND o.is_ms_shipped = 0
            ORDER BY o.name
            """, new Dictionary<string, object> { ["@schema"] = schema }, cancellationToken);
        return rows.Select(r => new DatabaseObjectInfo(r[0], r[1], ParseLong(r[2]), ParseLong(r[3]), null,
            DateTimeOffset.TryParse(r[4], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified) ? modified : null,
            r[5].Length == 0 ? null : r[5], ParseLong(r[6]))).ToArray();
    }

    public override async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, """
            SELECT c.name, TYPE_NAME(c.user_type_id) +
                     CASE WHEN TYPE_NAME(c.user_type_id) IN ('varchar', 'char', 'varbinary', 'binary') THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                          WHEN TYPE_NAME(c.user_type_id) IN ('nvarchar', 'nchar') THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                          WHEN TYPE_NAME(c.user_type_id) IN ('decimal', 'numeric') THEN '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                          ELSE '' END,
                   CASE WHEN c.is_nullable = 1 THEN 'YES' ELSE 'NO' END, OBJECT_DEFINITION(c.default_object_id),
                   CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                     WHERE i.object_id = c.object_id AND i.is_primary_key = 1 AND ic.column_id = c.column_id) THEN 'PRI' ELSE '' END,
                   c.is_identity, CASE WHEN c.is_computed = 1 OR TYPE_NAME(c.user_type_id) IN ('timestamp','rowversion') OR c.generated_always_type <> 0 THEN 1 ELSE 0 END
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(@object)
            ORDER BY c.column_id
            """, new Dictionary<string, object> { ["@object"] = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}" }, cancellationToken);
        return rows.Select(r => new ColumnInfo(r[0], r[1], r[2] == "YES", r[3].Length == 0 ? null : r[3], r[4] == "PRI", r[5] == "1", r[6] == "1")).ToArray();
    }

    // ───────────── Proprietà ─────────────

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerPropertiesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, """
            SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)), @@SERVERNAME, CAST(SERVERPROPERTY('Collation') AS nvarchar(128))
            """, new Dictionary<string, object>(), cancellationToken);
        var r = rows.Count == 0 ? new string[5] : rows[0];
        return
        [
            new("Versione server", OrDash(r[0])), new("Edizione", OrDash(r[1])), new("Livello", OrDash(r[2])),
            new("Nome server", OrDash(r[3])), new("Collation", OrDash(r[4])), new("Versione client", connection.ServerVersion is { Length: > 0 } v ? v : "—"),
        ];
    }

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetDatabasePropertiesAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, """
            SELECT d.collation_name, d.recovery_model_desc, d.compatibility_level, CONVERT(varchar(19), d.create_date, 120),
                   (SELECT COUNT(*) FROM sys.tables), (SELECT COUNT(*) FROM sys.views),
                   (SELECT SUM(CAST(size AS bigint)) * 8192 FROM sys.database_files WHERE type = 0),
                   (SELECT SUM(CAST(size AS bigint)) * 8192 FROM sys.database_files WHERE type = 1),
                   (SELECT SUM(p.rows) FROM sys.partitions p JOIN sys.tables t ON t.object_id = p.object_id WHERE p.index_id IN (0, 1))
            FROM sys.databases d WHERE d.name = DB_NAME()
            """, new Dictionary<string, object>(), cancellationToken);
        if (rows.Count == 0) return [];
        var r = rows[0];
        return
        [
            new("Collation", OrDash(r[0])), new("Modello di recupero", OrDash(r[1])), new("Livello di compatibilità", OrDash(r[2])),
            new("Data creazione", OrDash(r[3])), new("Tabelle", FormatNumber(r[4])), new("Viste", FormatNumber(r[5])),
            new("Righe", FormatNumber(r[8])), new("File dati", FormatSize(r[6])), new("File di log", FormatSize(r[7])),
        ];
    }

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetTablePropertiesAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, """
            SELECT o.type_desc, CONVERT(varchar(19), o.create_date, 120), CONVERT(varchar(19), o.modify_date, 120),
                   (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)),
                   (SELECT SUM(a.data_pages) * 8192 FROM sys.partitions p JOIN sys.allocation_units a ON a.container_id = p.partition_id WHERE p.object_id = o.object_id),
                   (SELECT SUM(a.used_pages - a.data_pages) * 8192 FROM sys.partitions p JOIN sys.allocation_units a ON a.container_id = p.partition_id WHERE p.object_id = o.object_id),
                   (SELECT CAST(IDENT_CURRENT(@object) AS bigint) WHERE OBJECTPROPERTY(o.object_id, 'TableHasIdentity') = 1),
                   CAST((SELECT ep.value FROM sys.extended_properties ep WHERE ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description') AS nvarchar(4000))
            FROM sys.objects o WHERE o.object_id = OBJECT_ID(@object)
            """, new Dictionary<string, object> { ["@object"] = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}" }, cancellationToken);
        if (rows.Count == 0) return [];
        var r = rows[0];
        if (r[0] == "VIEW") return [new("Tipo", "Vista"), new("Data creazione", OrDash(r[1])), new("Data modifica", OrDash(r[2])), new("Commento", OrDash(r[7]))];
        return
        [
            new("Righe", FormatNumber(r[3])), new("Identity attuale", OrDash(r[6])), new("Data creazione", OrDash(r[1])), new("Data modifica", OrDash(r[2])),
            new("Dimensione dati", FormatSize(r[4])), new("Dimensione indici", FormatSize(r[5])), new("Commento", OrDash(r[7])),
        ];
    }

    // ───────────── Modello relazionale ─────────────

    public override async Task<SchemaModel> GetSchemaModelAsync(DbConnection connection, string database, string schema, CancellationToken cancellationToken)
    {
        var p = new Dictionary<string, object> { ["@schema"] = schema };
        var columns = await ReadStringsAsync(connection, """
            SELECT t.name, c.name, TYPE_NAME(c.user_type_id), CASE WHEN c.is_nullable = 1 THEN 'YES' ELSE 'NO' END,
                   CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                     WHERE i.object_id = c.object_id AND i.is_primary_key = 1 AND ic.column_id = c.column_id) THEN 'PRI' ELSE '' END
            FROM sys.objects t JOIN sys.columns c ON c.object_id = t.object_id
            WHERE t.schema_id = SCHEMA_ID(@schema) AND t.type IN ('U', 'V') AND t.is_ms_shipped = 0
            ORDER BY t.name, c.column_id
            """, p, cancellationToken);
        var keys = await ReadStringsAsync(connection, """
            SELECT fk.name, OBJECT_NAME(fk.parent_object_id), pc.name, SCHEMA_NAME(rt.schema_id), rt.name, rc.name
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.schema_id = SCHEMA_ID(@schema)
            ORDER BY fk.name, fkc.constraint_column_id
            """, p, cancellationToken);
        var views = await ReadStringsAsync(connection, "SELECT name FROM sys.views WHERE schema_id = SCHEMA_ID(@schema)", p, cancellationToken);
        return BuildSchemaModel(columns, keys, views.Select(r => r[0]));
    }

    // ───────────── Server Monitor ─────────────

    public override Task<QueryResult> GetProcessListAsync(DbConnection connection, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, """
            SELECT s.session_id AS Id, s.login_name AS [User], COALESCE(s.host_name, '') + COALESCE(':' + s.program_name, '') AS Host,
                   DB_NAME(s.database_id) AS db, COALESCE(r.command, s.status) AS Command,
                   DATEDIFF(SECOND, COALESCE(r.start_time, s.last_request_start_time), SYSDATETIME()) AS Time,
                   COALESCE(r.wait_type, r.status, '') AS State, t.text AS Info
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE s.is_user_process = 1
            ORDER BY s.session_id
            """, 5000, cancellationToken);

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerVariablesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        (await ReadStringsAsync(connection, "SELECT name, CAST(value_in_use AS nvarchar(100)) FROM sys.configurations ORDER BY name", new Dictionary<string, object>(), cancellationToken))
        .Select(r => new KeyValuePair<string, string>(r[0], r[1])).ToArray();

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerStatusAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return (await ReadStringsAsync(connection, """
                SELECT RTRIM(counter_name) + CASE WHEN RTRIM(instance_name) = '' THEN '' ELSE ' (' + RTRIM(instance_name) + ')' END, CAST(cntr_value AS nvarchar(30))
                FROM sys.dm_os_performance_counters
                WHERE object_name LIKE '%General Statistics%' OR object_name LIKE '%SQL Statistics%' OR object_name LIKE '%Buffer Manager%'
                ORDER BY 1
                """, new Dictionary<string, object>(), cancellationToken)).Select(r => new KeyValuePair<string, string>(r[0], r[1])).ToArray();
        }
        catch (SqlException) { return []; } // serve VIEW SERVER STATE
    }

    public override async Task<ServerHealth> GetServerHealthAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var info = await ReadStringsAsync(connection, "SELECT @@VERSION, @@MAX_CONNECTIONS", new Dictionary<string, object>(), cancellationToken);
        var version = info.Count == 0 ? "—" : info[0][0].Split('\n')[0].Trim();
        long? maxConnections = info.Count == 0 ? null : ParseLong(info[0][1]);
        try
        {
            var rows = await ReadStringsAsync(connection, """
                SELECT DATEDIFF(SECOND, sqlserver_start_time, SYSDATETIME()) FROM sys.dm_os_sys_info;
                """, new Dictionary<string, object>(), cancellationToken);
            var sessions = await ReadStringsAsync(connection, """
                SELECT (SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1),
                       (SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id WHERE s.is_user_process = 1),
                       (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = 'Batch Requests/sec' AND object_name LIKE '%SQL Statistics%'),
                       (SELECT SUM(CAST(num_of_bytes_read AS bigint)) FROM sys.dm_io_virtual_file_stats(NULL, NULL)),
                       (SELECT SUM(CAST(num_of_bytes_written AS bigint)) FROM sys.dm_io_virtual_file_stats(NULL, NULL))
                """, new Dictionary<string, object>(), cancellationToken);
            var uptime = rows.Count == 0 ? null : ParseLong(rows[0][0]);
            var s = sessions.Count == 0 ? new string[5] : sessions[0];
            return new ServerHealth(version, uptime is null ? null : TimeSpan.FromSeconds(uptime.Value), ParseLong(s[0]), maxConnections, ParseLong(s[1]),
                ParseLong(s[2]), null, ParseLong(s[3]), ParseLong(s[4]));
        }
        catch (SqlException)
        {
            // Senza VIEW SERVER STATE si mostrano solo versione e connessioni massime.
            return new ServerHealth(version, null, null, maxConnections, null, null, null, null, null);
        }
    }

    public override string BuildKillSql(long processId, bool queryOnly) => $"KILL {processId}";

    // ───────────── Database, DDL, dump ─────────────

    public override string MaintenanceDatabase => "master";
    public override bool SupportsDatabaseRename => true;
    public override bool UsesMySqlScriptSyntax => false;
    public override void ClearPools() => SqlConnection.ClearAllPools();

    /// <summary>Chiude le altre connessioni al database prima di eliminarlo (altrimenti il DROP fallisce se è in uso).</summary>
    public override string BuildDropDatabaseSql(string database)
    {
        var name = QuoteIdentifier(database);
        return $"IF DB_ID({FormatLiteral(database)}) IS NOT NULL BEGIN ALTER DATABASE {name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE DROP DATABASE {name} END";
    }

    public override async Task<TableDdl> GetDdlAsync(DbConnection connection, string database, string schema, string table, bool isView, CancellationToken cancellationToken)
    {
        if (isView)
        {
            var rows = await ReadStringsAsync(connection, "SELECT OBJECT_DEFINITION(OBJECT_ID(@object))",
                new Dictionary<string, object> { ["@object"] = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}" }, cancellationToken);
            return new TableDdl((rows.Count == 0 ? string.Empty : rows[0][0].Trim()) + "\nGO", []);
        }
        var design = await GetTableDesignAsync(connection, database, schema, table, cancellationToken);
        // Le chiavi esterne si creano dopo i dati (nel dump e nel trasferimento).
        var statements = Core.TableDesignSql.Build(DatabaseKind.SqlServer, schema, null, design with { ForeignKeys = [] });
        var keys = Core.TableDesignSql.Build(DatabaseKind.SqlServer, schema, design with { ForeignKeys = [] }, design);
        return new TableDdl(string.Join(";\n", statements.Where(s => !s.StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase))) + ";"
                            + string.Concat(statements.Where(s => s.StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase)).Select(s => $"\nGO\n{s}\nGO")), keys);
    }

    public override async Task<IReadOnlyList<CharsetInfo>> ListCharsetsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var collations = await ReadStringsAsync(connection, "SELECT name FROM sys.fn_helpcollations() WHERE name NOT LIKE 'SQL[_]%' ORDER BY name", new Dictionary<string, object>(), cancellationToken);
        var server = await ReadStringsAsync(connection, "SELECT CAST(SERVERPROPERTY('Collation') AS nvarchar(128))", new Dictionary<string, object>(), cancellationToken);
        return [new CharsetInfo("Unicode", server.Count == 0 ? string.Empty : server[0][0], collations.Select(r => r[0]).ToArray())];
    }

    public override async Task<DatabaseOptions> GetDatabaseOptionsAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, "SELECT collation_name FROM sys.databases WHERE name = @database",
            new Dictionary<string, object> { ["@database"] = database }, cancellationToken);
        return new DatabaseOptions(database, "Unicode", rows.Count == 0 ? null : rows[0][0]);
    }

    public override string BuildCreateDatabaseSql(DatabaseOptions options) =>
        $"CREATE DATABASE {QuoteIdentifier(options.Name)}{(string.IsNullOrWhiteSpace(options.Collation) ? string.Empty : $" COLLATE {SafeWord(options.Collation)}")}";

    public override IReadOnlyList<string> BuildAlterDatabaseSql(DatabaseOptions current, DatabaseOptions updated) =>
        current.Collation == updated.Collation || string.IsNullOrWhiteSpace(updated.Collation)
            ? []
            : [$"ALTER DATABASE {QuoteIdentifier(current.Name)} COLLATE {SafeWord(updated.Collation)}"];

    private static string SafeWord(string value) => value.All(c => char.IsLetterOrDigit(c) || c == '_')
        ? value
        : throw new ArgumentException($"Valore non valido: {value}");

    public override string DumpHeader(string database) =>
        $"""
        -- Navifox SQL dump (SQL Server)
        -- Database: {database}
        -- Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

        SET NOCOUNT ON;

        """;

    public override string DumpFooter() => string.Empty;

    public override string DumpName(string schema, string table) => $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    /// <summary>SQL Server accetta al massimo 1000 righe per INSERT … VALUES.</summary>
    public override string BuildInsert(string schema, string table, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        if (rows.Count <= 1000) return base.BuildInsert(schema, table, columns, rows);
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < rows.Count; i += 1000) builder.Append(base.BuildInsert(schema, table, columns, rows.Skip(i).Take(1000).ToArray()));
        return builder.ToString();
    }

    public override string FormatLiteral(object? value) => value switch
    {
        string text => "N'" + text.Replace("'", "''") + "'",
        DateTime dateTime => $"'{dateTime.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.')}'",
        DateTimeOffset offset => $"'{offset.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture)}'",
        _ => FormatCommonLiteral(value, text => "N'" + text.Replace("'", "''") + "'",
            bytes => bytes.Length == 0 ? "0x" : "0x" + Convert.ToHexString(bytes),
            flag => flag ? "1" : "0"),
    };
}
