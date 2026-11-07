using System.Data.Common;
using MosaicDB.Core.Models;
using Npgsql;

namespace MosaicDB.Dialects;

public sealed class PostgreSqlDialectProvider : AdoDialectProvider
{
    public override DatabaseKind Kind => DatabaseKind.PostgreSql;

    public override async Task<DbConnection> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var tunnel = await OpenTunnelAsync(profile, cancellationToken);
        var endpoint = tunnel?.UseLocalEndpoint(profile) ?? profile;
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = endpoint.Host,
            Port = endpoint.EffectivePort,
            Username = endpoint.UserName,
            Password = endpoint.Password,
            Database = endpoint.DefaultDatabase,
            SslMode = ParseSsl(endpoint.SslMode)
        };
        if (!string.IsNullOrWhiteSpace(endpoint.ConnectionStringOverride)) builder.ConnectionString = endpoint.ConnectionStringOverride;
        ApplyAdvancedParameters(builder, endpoint.AdvancedParameters);
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await OpenAsync(connection, cancellationToken);
            tunnel?.Attach(connection);
            return connection;
        }
        catch
        {
            tunnel?.Dispose();
            await connection.DisposeAsync();
            throw;
        }
    }

    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public override string MapDataType(string genericType) => genericType.ToLowerInvariant() switch
    {
        "boolean" => "BOOLEAN", "integer" => "INTEGER", "bigint" => "BIGINT", "decimal" => "NUMERIC(18,2)", "text" => "TEXT", "datetime" => "TIMESTAMP WITH TIME ZONE", "binary" => "BYTEA", _ => genericType
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

    private static readonly string[] Encodings = ["UTF8", "LATIN1", "LATIN9", "WIN1252", "SQL_ASCII", "EUC_JP", "EUC_KR", "WIN1251", "KOI8R"];
    private static readonly System.Text.RegularExpressions.Regex NextvalPattern = new(@"nextval\('((?:[^']|'')+)'::regclass\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetDatabasePropertiesAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, """
            SELECT pg_encoding_to_char(d.encoding), d.datcollate, d.datctype, pg_get_userbyid(d.datdba),
                   pg_database_size(d.datname), COALESCE(t.spcname, ''), COALESCE(shobj_description(d.oid, 'pg_database'), ''),
                   (SELECT count(*) FROM information_schema.tables WHERE table_catalog = d.datname AND table_type = 'BASE TABLE'
                        AND table_schema NOT IN ('pg_catalog', 'information_schema')),
                   (SELECT count(*) FROM information_schema.schemata WHERE catalog_name = d.datname AND left(schema_name, 3) <> 'pg_' AND schema_name <> 'information_schema')
            FROM pg_database d LEFT JOIN pg_tablespace t ON t.oid = d.dattablespace
            WHERE d.datname = @database
            """, new Dictionary<string, object> { ["@database"] = database }, cancellationToken);
        if (rows.Count == 0) return [];
        var r = rows[0];
        return
        [
            new("Codifica", OrDash(r[0])), new("Collation", OrDash(r[1])), new("Ctype", OrDash(r[2])), new("Proprietario", OrDash(r[3])),
            new("Schemi", FormatNumber(r[8])), new("Tabelle", FormatNumber(r[7])), new("Dimensione", FormatSize(r[4])),
            new("Tablespace", OrDash(r[5])), new("Commento", OrDash(r[6])),
        ];
    }

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetTablePropertiesAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, """
            SELECT c.relkind::text, GREATEST(c.reltuples, 0)::bigint, pg_get_userbyid(c.relowner), COALESCE(t.spcname, 'pg_default'),
                   pg_total_relation_size(c.oid), pg_relation_size(c.oid), pg_indexes_size(c.oid), COALESCE(pg_total_relation_size(c.reltoastrelid), 0),
                   COALESCE(to_char(s.last_vacuum, 'YYYY-MM-DD HH24:MI:SS'), ''), COALESCE(to_char(s.last_autovacuum, 'YYYY-MM-DD HH24:MI:SS'), ''),
                   COALESCE(to_char(s.last_analyze, 'YYYY-MM-DD HH24:MI:SS'), ''), COALESCE(s.n_live_tup, 0), COALESCE(s.n_dead_tup, 0),
                   COALESCE(obj_description(c.oid, 'pg_class'), ''), c.relhasindex::text, c.relpersistence::text
            FROM pg_class c
            LEFT JOIN pg_tablespace t ON t.oid = c.reltablespace
            LEFT JOIN pg_stat_all_tables s ON s.relid = c.oid
            WHERE c.oid = CAST(@rel AS regclass)
            """, new Dictionary<string, object> { ["@rel"] = DumpName(schema, table) }, cancellationToken);
        if (rows.Count == 0) return [];
        var r = rows[0];
        if (r[0] is "v" or "m")
            return
            [
                new("Tipo", r[0] == "m" ? "Vista materializzata" : "Vista"), new("Proprietario", OrDash(r[2])),
                new("Dimensione", FormatSize(r[4])), new("Commento", OrDash(r[13])),
            ];
        return
        [
            new("Righe (stima)", FormatNumber(r[1])),
            new("Righe vive / morte", $"{FormatNumber(r[11])} / {FormatNumber(r[12])}"),
            new("Proprietario", OrDash(r[2])),
            new("Tablespace", OrDash(r[3])),
            new("Persistenza", r[15] switch { "u" => "Unlogged", "t" => "Temporanea", _ => "Permanente" }),
            new("Dimensione totale", FormatSize(r[4])),
            new("Dimensione dati", FormatSize(r[5])),
            new("Dimensione indici", FormatSize(r[6])),
            new("Dimensione TOAST", FormatSize(r[7])),
            new("Ultimo VACUUM", OrDash(r[8].Length > 0 ? r[8] : r[9])),
            new("Ultimo ANALYZE", OrDash(r[10])),
            new("Commento", OrDash(r[13])),
        ];
    }

    public override async Task<SchemaModel> GetSchemaModelAsync(DbConnection connection, string database, string schema, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object> { ["@schema"] = schema };
        var columns = await ReadStringsAsync(connection, """
            SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), (NOT a.attnotnull)::text,
                   CASE WHEN EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = c.oid AND i.indisprimary AND a.attnum = ANY(i.indkey)) THEN 'PRI' ELSE '' END
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE n.nspname = @schema AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
            ORDER BY c.relname, a.attnum
            """, parameters, cancellationToken);
        var keys = await ReadStringsAsync(connection, """
            SELECT con.conname, cl.relname, a.attname, nr.nspname, rc.relname, ra.attname
            FROM pg_constraint con
            JOIN pg_class cl ON cl.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = cl.relnamespace
            JOIN pg_class rc ON rc.oid = con.confrelid
            JOIN pg_namespace nr ON nr.oid = rc.relnamespace
            CROSS JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(attnum, refnum, ord)
            JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum
            JOIN pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = k.refnum
            WHERE con.contype = 'f' AND n.nspname = @schema
            ORDER BY con.conname, k.ord
            """, parameters, cancellationToken);
        var views = await ReadStringsAsync(connection, """
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relkind IN ('v', 'm')
            """, parameters, cancellationToken);
        return BuildSchemaModel(columns, keys, views.Select(row => row[0]));
    }

    public override Task<QueryResult> GetProcessListAsync(DbConnection connection, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, """
            SELECT pid AS "Id", usename AS "User",
                   COALESCE(host(client_addr), 'local') || COALESCE(':' || client_port::text, '') AS "Host",
                   datname AS "db", backend_type AS "Command",
                   COALESCE(EXTRACT(EPOCH FROM (now() - COALESCE(query_start, backend_start)))::bigint, 0) AS "Time",
                   COALESCE(state, '') || CASE WHEN wait_event IS NOT NULL THEN ' · ' || wait_event ELSE '' END AS "State",
                   query AS "Info"
            FROM pg_stat_activity ORDER BY pid
            """, 5000, cancellationToken);

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerVariablesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        (await ReadStringsAsync(connection, "SELECT name, setting || COALESCE(' ' || unit, '') FROM pg_settings ORDER BY name", new Dictionary<string, object>(), cancellationToken))
        .Select(row => new KeyValuePair<string, string>(row[0], row[1])).ToArray();

    public override async Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerStatusAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, """
            SELECT 'xact_commit', sum(xact_commit)::text FROM pg_stat_database UNION ALL
            SELECT 'xact_rollback', sum(xact_rollback)::text FROM pg_stat_database UNION ALL
            SELECT 'blks_read', sum(blks_read)::text FROM pg_stat_database UNION ALL
            SELECT 'blks_hit', sum(blks_hit)::text FROM pg_stat_database UNION ALL
            SELECT 'cache_hit_ratio', round(100.0 * sum(blks_hit) / NULLIF(sum(blks_hit) + sum(blks_read), 0), 2)::text || ' %' FROM pg_stat_database UNION ALL
            SELECT 'tup_returned', sum(tup_returned)::text FROM pg_stat_database UNION ALL
            SELECT 'tup_fetched', sum(tup_fetched)::text FROM pg_stat_database UNION ALL
            SELECT 'tup_inserted', sum(tup_inserted)::text FROM pg_stat_database UNION ALL
            SELECT 'tup_updated', sum(tup_updated)::text FROM pg_stat_database UNION ALL
            SELECT 'tup_deleted', sum(tup_deleted)::text FROM pg_stat_database UNION ALL
            SELECT 'conflicts', sum(conflicts)::text FROM pg_stat_database UNION ALL
            SELECT 'deadlocks', sum(deadlocks)::text FROM pg_stat_database UNION ALL
            SELECT 'temp_bytes', sum(temp_bytes)::text FROM pg_stat_database UNION ALL
            SELECT 'numbackends', sum(numbackends)::text FROM pg_stat_database UNION ALL
            SELECT 'postmaster_start_time', to_char(pg_postmaster_start_time(), 'YYYY-MM-DD HH24:MI:SS') UNION ALL
            SELECT 'database_size_total', sum(pg_database_size(datname))::text FROM pg_database
            """, new Dictionary<string, object>(), cancellationToken);
        return rows.Select(row => new KeyValuePair<string, string>(row[0], row[1])).ToArray();
    }

    public override async Task<ServerHealth> GetServerHealthAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, """
            SELECT version(),
                   EXTRACT(EPOCH FROM (now() - pg_postmaster_start_time()))::bigint,
                   (SELECT count(*) FROM pg_stat_activity WHERE backend_type = 'client backend'),
                   current_setting('max_connections'),
                   (SELECT count(*) FROM pg_stat_activity WHERE state = 'active' AND backend_type = 'client backend'),
                   (SELECT sum(xact_commit + xact_rollback) FROM pg_stat_database),
                   (SELECT count(*) FROM pg_stat_activity WHERE state = 'active' AND now() - query_start > interval '5 seconds')
            """, new Dictionary<string, object>(), cancellationToken);
        var r = rows[0];
        var uptime = ParseLong(r[1]);
        return new ServerHealth(r[0], uptime is null ? null : TimeSpan.FromSeconds(uptime.Value),
            ParseLong(r[2]), ParseLong(r[3]), ParseLong(r[4]), ParseLong(r[5]), ParseLong(r[6]), null, null);
    }

    public override string BuildKillSql(long processId, bool queryOnly) =>
        queryOnly ? $"SELECT pg_cancel_backend({processId})" : $"SELECT pg_terminate_backend({processId})";

    public override string MaintenanceDatabase => "postgres";
    public override bool SupportsDatabaseRename => true;
    public override bool UsesMySqlScriptSyntax => false;
    public override void ClearPools() => NpgsqlConnection.ClearAllPools();
    public override string DumpName(string schema, string table) => $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    public override async Task<TableDdl> GetDdlAsync(DbConnection connection, string database, string schema, string table, bool isView, CancellationToken cancellationToken)
    {
        var name = DumpName(schema, table);
        var parameters = new Dictionary<string, object> { ["@rel"] = name };
        if (isView)
        {
            var definition = await ReadStringsAsync(connection, "SELECT pg_get_viewdef(CAST(@rel AS regclass), true)", parameters, cancellationToken);
            return new TableDdl($"CREATE OR REPLACE VIEW {name} AS\n{definition[0][0].TrimEnd().TrimEnd(';')};", []);
        }

        var columns = await ReadStringsAsync(connection, """
            SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull::text, COALESCE(pg_get_expr(d.adbin, d.adrelid), ''), a.attidentity::text
            FROM pg_attribute a
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attrelid = CAST(@rel AS regclass) AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """, parameters, cancellationToken);
        var constraints = await ReadStringsAsync(connection, """
            SELECT conname, contype::text, pg_get_constraintdef(oid)
            FROM pg_constraint WHERE conrelid = CAST(@rel AS regclass) AND contype <> 'n'
            ORDER BY CASE contype WHEN 'p' THEN 0 WHEN 'u' THEN 1 WHEN 'c' THEN 2 ELSE 3 END, conname
            """, parameters, cancellationToken);
        var indexes = await ReadStringsAsync(connection, """
            SELECT pg_get_indexdef(i.indexrelid) FROM pg_index i
            WHERE i.indrelid = CAST(@rel AS regclass)
              AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = i.indexrelid)
            """, parameters, cancellationToken);

        var pre = new System.Text.StringBuilder();
        var post = new List<string>();
        var lines = new List<string>();
        foreach (var column in columns)
        {
            var line = $"  {QuoteIdentifier(column[0])} {column[1]}";
            if (column[4] == "a") line += " GENERATED ALWAYS AS IDENTITY";
            else if (column[4] == "d") line += " GENERATED BY DEFAULT AS IDENTITY";
            else if (column[3].Length > 0)
            {
                line += $" DEFAULT {column[3]}";
                var sequence = NextvalPattern.Match(column[3]);
                if (sequence.Success)
                {
                    var sequenceName = sequence.Groups[1].Value.Replace("''", "'");
                    pre.Append($"CREATE SEQUENCE IF NOT EXISTS {sequenceName};\n");
                    post.Add($"SELECT setval({FormatLiteral(sequenceName)}, COALESCE((SELECT MAX({QuoteIdentifier(column[0])}) FROM {name}), 1));");
                }
            }
            if (column[2] == "true") line += " NOT NULL";
            lines.Add(line);
        }
        foreach (var constraint in constraints)
        {
            var definition = $"CONSTRAINT {QuoteIdentifier(constraint[0])} {constraint[2]}";
            if (constraint[1] == "f") post.Add($"ALTER TABLE {name} ADD {definition};");
            else lines.Add("  " + definition);
        }
        post.AddRange(indexes.Select(index => index[0] + ";"));
        return new TableDdl($"{pre}CREATE TABLE {name} (\n{string.Join(",\n", lines)}\n);", post);
    }

    public override Task<IReadOnlyList<CharsetInfo>> ListCharsetsAsync(DbConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CharsetInfo>>(Encodings.Select(encoding => new CharsetInfo(encoding, string.Empty, [])).ToArray());

    public override async Task<DatabaseOptions> GetDatabaseOptionsAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        var rows = await ReadStringsAsync(connection, "SELECT pg_encoding_to_char(encoding) FROM pg_database WHERE datname = @database",
            new Dictionary<string, object> { ["@database"] = database }, cancellationToken);
        return new DatabaseOptions(database, rows.Count == 0 ? null : rows[0][0]);
    }

    public override string BuildCreateDatabaseSql(DatabaseOptions options)
    {
        var sql = $"CREATE DATABASE {QuoteIdentifier(options.Name)}";
        if (string.IsNullOrWhiteSpace(options.CharacterSet)) return sql;
        if (!Encodings.Contains(options.CharacterSet, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"Codifica non valida: {options.CharacterSet}");
        return $"{sql} TEMPLATE template0 ENCODING '{options.CharacterSet}'";
    }

    public override IReadOnlyList<string> BuildAlterDatabaseSql(DatabaseOptions current, DatabaseOptions updated) =>
        current.Name == updated.Name ? [] : [$"ALTER DATABASE {QuoteIdentifier(current.Name)} RENAME TO {QuoteIdentifier(updated.Name)}"];

    public override string DumpHeader(string database) =>
        $"""
        -- Navifox SQL dump (PostgreSQL)
        -- Database: {database}
        -- Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
        -- Nota: dump generato da Navifox; per backup completi (trigger, funzioni, permessi) usare pg_dump.

        SET client_encoding = 'UTF8';
        SET standard_conforming_strings = on;

        """;

    public override string DumpFooter() => string.Empty;

    public override string FormatLiteral(object? value) => value switch
    {
        DateTime { Kind: DateTimeKind.Utc } utc => FormatLiteral(new DateTimeOffset(utc)),
        Array array and not byte[] => QuoteString("{" + string.Join(",", array.Cast<object?>().Select(item => item is null ? "NULL" : "\"" + Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture)?.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "}"),
        _ => FormatCommonLiteral(value, QuoteString, bytes => $"'\\x{Convert.ToHexString(bytes)}'::bytea", flag => flag ? "TRUE" : "FALSE")
    };

    private static string QuoteString(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static SslMode ParseSsl(string sslMode) => Enum.TryParse<SslMode>(sslMode, true, out var mode) ? mode : SslMode.Prefer;
}
