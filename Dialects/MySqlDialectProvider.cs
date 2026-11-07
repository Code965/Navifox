using System.Data.Common;
using Navifox.Core.Models;
using MySqlConnector;

namespace Navifox.Dialects;

public sealed partial class MySqlDialectProvider : AdoDialectProvider
{
    public override DatabaseKind Kind => DatabaseKind.MySql;

    public override async Task<DbConnection> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var tunnel = await OpenTunnelAsync(profile, cancellationToken);
        var endpoint = tunnel?.UseLocalEndpoint(profile) ?? profile;
        var builder = new MySqlConnectionStringBuilder
        {
            Server = endpoint.Host,
            Port = (uint)endpoint.EffectivePort,
            UserID = endpoint.UserName,
            Password = endpoint.Password,
            Database = endpoint.DefaultDatabase,
            SslMode = ParseSsl(endpoint.SslMode),
            // Necessario per gli script con variabili @utente e per le date 0000-00-00.
            AllowUserVariables = true,
            ConvertZeroDateTime = true
        };
        if (!string.IsNullOrWhiteSpace(endpoint.ConnectionStringOverride)) builder.ConnectionString = endpoint.ConnectionStringOverride;
        ApplyAdvancedParameters(builder, endpoint.AdvancedParameters);
        var connection = new MySqlConnection(builder.ConnectionString);
        try
        {
            await OpenAsync(connection, cancellationToken);
            await ApplyReadOnlyAsync(connection, profile, "SET SESSION TRANSACTION READ ONLY", cancellationToken);
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

    public override string QuoteIdentifier(string identifier) => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

    public override string MapDataType(string genericType) => genericType.ToLowerInvariant() switch
    {
        "boolean" => "TINYINT(1)", "integer" => "INT", "bigint" => "BIGINT", "decimal" => "DECIMAL(18,2)", "text" => "TEXT", "datetime" => "DATETIME", "binary" => "BLOB", _ => genericType
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

    // ───────────── Database, DDL, dump ─────────────

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetDatabasePropertiesAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object> { ["@database"] = database };
        var schema = await ReadStringsAsync(connection,
            "SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = @database", parameters, cancellationToken);
        var totals = await ReadStringsAsync(connection, """
            SELECT SUM(TABLE_TYPE = 'BASE TABLE'), SUM(TABLE_TYPE = 'VIEW'), COALESCE(SUM(DATA_LENGTH), 0), COALESCE(SUM(INDEX_LENGTH), 0), COALESCE(SUM(TABLE_ROWS), 0)
            FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @database
            """, parameters, cancellationToken);
        var t = totals.Count == 0 ? new[] { "0", "0", "0", "0", "0" } : totals[0];
        return
        [
            new("Set di caratteri", schema.Count == 0 ? "—" : OrDash(schema[0][0])),
            new("Collation", schema.Count == 0 ? "—" : OrDash(schema[0][1])),
            new("Tabelle", FormatNumber(t[0])),
            new("Viste", FormatNumber(t[1])),
            new("Righe (stima)", FormatNumber(t[4])),
            new("Dimensione dati", FormatSize(t[2])),
            new("Dimensione indici", FormatSize(t[3])),
        ];
    }

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetTablePropertiesAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object> { ["@schema"] = schema, ["@table"] = table };
        var rows = await ReadStringsAsync(connection, """
            SELECT TABLE_TYPE, COALESCE(TABLE_ROWS, ''), COALESCE(ENGINE, ''), COALESCE(AUTO_INCREMENT, ''), COALESCE(ROW_FORMAT, ''),
                   COALESCE(DATE_FORMAT(UPDATE_TIME, '%Y-%m-%d %H:%i:%s'), ''), COALESCE(DATE_FORMAT(CREATE_TIME, '%Y-%m-%d %H:%i:%s'), ''),
                   COALESCE(DATE_FORMAT(CHECK_TIME, '%Y-%m-%d %H:%i:%s'), ''), COALESCE(INDEX_LENGTH, 0), COALESCE(DATA_LENGTH, 0),
                   COALESCE(MAX_DATA_LENGTH, 0), COALESCE(DATA_FREE, 0), COALESCE(TABLE_COLLATION, ''), COALESCE(TABLE_COMMENT, '')
            FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            """, parameters, cancellationToken);
        if (rows.Count == 0) return [];
        var r = rows[0];
        if (r[0].Contains("VIEW", StringComparison.OrdinalIgnoreCase))
        {
            var view = await ReadStringsAsync(connection,
                "SELECT DEFINER, SECURITY_TYPE, CHECK_OPTION, IS_UPDATABLE, CHARACTER_SET_CLIENT, COLLATION_CONNECTION FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table",
                parameters, cancellationToken);
            var v = view.Count == 0 ? new string[6] : view[0];
            return
            [
                new("Definer", OrDash(v[0])), new("Sicurezza", OrDash(v[1])), new("Check option", OrDash(v[2])),
                new("Aggiornabile", OrDash(v[3])), new("Set di caratteri client", OrDash(v[4])), new("Collation connessione", OrDash(v[5])),
            ];
        }
        return
        [
            new("Righe (stima)", FormatNumber(r[1])),
            new("Engine", OrDash(r[2])),
            new("Auto Increment", FormatNumber(r[3].Length == 0 ? "0" : r[3])),
            new("Formato righe", OrDash(r[4])),
            new("Data modifica", OrDash(r[5])),
            new("Data creazione", OrDash(r[6])),
            new("Ultimo controllo", OrDash(r[7])),
            new("Dimensione indici", FormatSize(r[8])),
            new("Dimensione dati", FormatSize(r[9])),
            new("Dimensione massima dati", FormatSize(r[10])),
            new("Spazio libero", FormatSize(r[11])),
            new("Collation", OrDash(r[12])),
            new("Commento", OrDash(r[13])),
        ];
    }

    public override async Task<SchemaModel> GetSchemaModelAsync(DbConnection connection, string database, string schema, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object> { ["@schema"] = schema };
        var columns = await ReadStringsAsync(connection,
            "SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @schema ORDER BY TABLE_NAME, ORDINAL_POSITION",
            parameters, cancellationToken);
        var keys = await ReadStringsAsync(connection, """
            SELECT CONSTRAINT_NAME, TABLE_NAME, COLUMN_NAME, REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @schema AND REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY CONSTRAINT_NAME, ORDINAL_POSITION
            """, parameters, cancellationToken);
        var views = await ReadStringsAsync(connection, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_SCHEMA = @schema", parameters, cancellationToken);
        return BuildSchemaModel(columns, keys, views.Select(row => row[0]));
    }

    public override Task<QueryResult> GetProcessListAsync(DbConnection connection, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, "SELECT ID AS Id, USER AS User, HOST AS Host, DB AS db, COMMAND AS Command, TIME AS Time, STATE AS State, INFO AS Info FROM INFORMATION_SCHEMA.PROCESSLIST ORDER BY ID", 5000, cancellationToken);

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerVariablesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        (await ReadStringsAsync(connection, "SHOW GLOBAL VARIABLES", new Dictionary<string, object>(), cancellationToken)).Select(row => new KeyValuePair<string, string>(row[0], row[1])).ToArray();

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerStatusAsync(DbConnection connection, CancellationToken cancellationToken) =>
        (await ReadStringsAsync(connection, "SHOW GLOBAL STATUS", new Dictionary<string, object>(), cancellationToken)).Select(row => new KeyValuePair<string, string>(row[0], row[1])).ToArray();

    public override async Task<ServerHealth> GetServerHealthAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var status = (await GetServerStatusAsync(connection, cancellationToken)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var variables = await ReadStringsAsync(connection, "SELECT @@version, @@max_connections", new Dictionary<string, object>(), cancellationToken);
        string? Get(string key) => status.TryGetValue(key, out var value) ? value : null;
        var uptime = ParseLong(Get("Uptime"));
        return new ServerHealth(
            variables.Count == 0 ? "—" : variables[0][0],
            uptime is null ? null : TimeSpan.FromSeconds(uptime.Value),
            ParseLong(Get("Threads_connected")),
            variables.Count == 0 ? null : ParseLong(variables[0][1]),
            ParseLong(Get("Threads_running")),
            ParseLong(Get("Questions")),
            ParseLong(Get("Slow_queries")),
            ParseLong(Get("Bytes_received")),
            ParseLong(Get("Bytes_sent")));
    }

    public override string BuildKillSql(long processId, bool queryOnly) => queryOnly ? $"KILL QUERY {processId}" : $"KILL {processId}";

    public override string MaintenanceDatabase => string.Empty;
    public override bool SupportsDatabaseRename => false;
    public override bool UsesMySqlScriptSyntax => true;
    public override void ClearPools() => MySqlConnection.ClearAllPools();

    public override async Task<TableDdl> GetDdlAsync(DbConnection connection, string database, string schema, string table, bool isView, CancellationToken cancellationToken)
    {
        var sql = $"SHOW CREATE {(isView ? "VIEW" : "TABLE")} {QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var rows = await ReadStringsAsync(connection, sql, new Dictionary<string, object>(), cancellationToken);
        var ddl = rows.Count == 0 ? string.Empty : rows[0][1];
        return new TableDdl(ddl + ";", []);
    }

    public override async Task<IReadOnlyList<CharsetInfo>> ListCharsetsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var charsets = await ReadStringsAsync(connection, "SELECT CHARACTER_SET_NAME, DEFAULT_COLLATE_NAME FROM INFORMATION_SCHEMA.CHARACTER_SETS ORDER BY CHARACTER_SET_NAME", new Dictionary<string, object>(), cancellationToken);
        var collations = await ReadStringsAsync(connection, "SELECT CHARACTER_SET_NAME, COLLATION_NAME FROM INFORMATION_SCHEMA.COLLATIONS ORDER BY COLLATION_NAME", new Dictionary<string, object>(), cancellationToken);
        var byCharset = collations.GroupBy(row => row[0]).ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(row => row[1]).ToArray());
        return charsets.Select(row => new CharsetInfo(row[0], row[1], byCharset.GetValueOrDefault(row[0], []))).ToArray();
    }

    public override async Task<DatabaseOptions> GetDatabaseOptionsAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection,
            "SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = @database",
            new Dictionary<string, object> { ["@database"] = database }, cancellationToken);
        return rows.Count == 0 ? new DatabaseOptions(database) : new DatabaseOptions(database, rows[0][0], rows[0][1]);
    }

    public override string BuildCreateDatabaseSql(DatabaseOptions options) =>
        $"CREATE DATABASE {QuoteIdentifier(options.Name)}{CharsetClause(options)}";

    public override IReadOnlyList<string> BuildAlterDatabaseSql(DatabaseOptions current, DatabaseOptions updated)
    {
        if (current.CharacterSet == updated.CharacterSet && current.Collation == updated.Collation) return [];
        return [$"ALTER DATABASE {QuoteIdentifier(current.Name)}{CharsetClause(updated)}"];
    }

    private static string CharsetClause(DatabaseOptions options) =>
        (string.IsNullOrWhiteSpace(options.CharacterSet) ? string.Empty : $" CHARACTER SET {SafeWord(options.CharacterSet)}")
        + (string.IsNullOrWhiteSpace(options.Collation) ? string.Empty : $" COLLATE {SafeWord(options.Collation)}");

    /// <summary>Charset e collation sono identificatori semplici: si rifiuta tutto il resto.</summary>
    private static string SafeWord(string value) => value.All(c => char.IsLetterOrDigit(c) || c == '_')
        ? value
        : throw new ArgumentException($"Valore non valido: {value}");

    public override string DumpHeader(string database) =>
        $"""
        -- Navifox SQL dump
        -- Database: {database}
        -- Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

        SET NAMES utf8mb4;
        SET FOREIGN_KEY_CHECKS = 0;
        SET SQL_MODE = 'NO_AUTO_VALUE_ON_ZERO';

        """;

    public override string DumpFooter() => "\nSET FOREIGN_KEY_CHECKS = 1;\n";

    public override string FormatLiteral(object? value) => FormatCommonLiteral(value, QuoteString,
        bytes => bytes.Length == 0 ? "''" : "0x" + Convert.ToHexString(bytes),
        flag => flag ? "1" : "0");

    private static string QuoteString(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\'' => "\\'", '\\' => "\\\\", '\0' => "\\0", '\n' => "\\n", '\r' => "\\r", '\u001a' => "\\Z",
                _ => c.ToString()
            });
        }
        return builder.Append('\'').ToString();
    }

    private static MySqlSslMode ParseSsl(string sslMode) => sslMode.Equals("Disable", StringComparison.OrdinalIgnoreCase)
        ? MySqlSslMode.Disabled
        : Enum.TryParse<MySqlSslMode>(sslMode, true, out var mode) ? mode : MySqlSslMode.Preferred;
}
