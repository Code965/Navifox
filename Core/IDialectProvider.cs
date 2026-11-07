using System.Data.Common;
using MosaicDB.Core.Models;

namespace MosaicDB.Core;

public interface IDialectProvider
{
    DatabaseKind Kind { get; }
    Task<DbConnection> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListSchemasAsync(DbConnection connection, string database, CancellationToken cancellationToken);
    Task<IReadOnlyList<DatabaseObjectInfo>> ListTablesAsync(DbConnection connection, string database, string schema, CancellationToken cancellationToken);
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken);
    /// <summary>Pagina di una tabella; <paramref name="where"/> e <paramref name="orderBy"/> sono frammenti SQL già quotati.</summary>
    Task<DataPage> QueryPagedAsync(DbConnection connection, string schema, string table, int pageIndex, int pageSize, CancellationToken cancellationToken, string? where = null, string? orderBy = null);
    Task<long> CountRowsAsync(DbConnection connection, string schema, string table, string? where, CancellationToken cancellationToken);

    /// <summary>Esegue una singola istruzione; legge al massimo <paramref name="maxRows"/> righe del primo result set.</summary>
    Task<QueryResult> ExecuteAsync(DbConnection connection, string sql, int maxRows, CancellationToken cancellationToken);

    /// <summary>Legge tutte le righe di una tabella in streaming (per dump/export).</summary>
    IAsyncEnumerable<IReadOnlyList<object?>> StreamRowsAsync(DbConnection connection, string schema, string table, CancellationToken cancellationToken);

    /// <summary>Esegue una SELECT in streaming; <paramref name="onColumns"/> riceve le colonne prima della prima riga.</summary>
    IAsyncEnumerable<IReadOnlyList<object?>> StreamQueryAsync(DbConnection connection, string sql, Action<IReadOnlyList<ColumnInfo>> onColumns, CancellationToken cancellationToken);

    Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerPropertiesAsync(DbConnection connection, CancellationToken cancellationToken);
    Task<IReadOnlyList<KeyValuePair<string, string>>> GetDatabasePropertiesAsync(DbConnection connection, string database, CancellationToken cancellationToken);
    Task<IReadOnlyList<KeyValuePair<string, string>>> GetTablePropertiesAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken);

    Task<TableDdl> GetDdlAsync(DbConnection connection, string database, string schema, string table, bool isView, CancellationToken cancellationToken);

    /// <summary>Tabelle, colonne e chiavi esterne di uno schema (modello relazionale).</summary>
    Task<SchemaModel> GetSchemaModelAsync(DbConnection connection, string database, string schema, CancellationToken cancellationToken);

    // ───── Server Monitor ─────
    /// <summary>Processi/sessioni con colonne Id, User, Host, db, Command, Time, State, Info.</summary>
    Task<QueryResult> GetProcessListAsync(DbConnection connection, CancellationToken cancellationToken);
    Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerVariablesAsync(DbConnection connection, CancellationToken cancellationToken);
    Task<IReadOnlyList<KeyValuePair<string, string>>> GetServerStatusAsync(DbConnection connection, CancellationToken cancellationToken);
    Task<ServerHealth> GetServerHealthAsync(DbConnection connection, CancellationToken cancellationToken);
    /// <summary>Termina la sessione (o solo la query in corso se <paramref name="queryOnly"/>).</summary>
    string BuildKillSql(long processId, bool queryOnly);

    Task<IReadOnlyList<CharsetInfo>> ListCharsetsAsync(DbConnection connection, CancellationToken cancellationToken);
    Task<DatabaseOptions> GetDatabaseOptionsAsync(DbConnection connection, string database, CancellationToken cancellationToken);
    string BuildCreateDatabaseSql(DatabaseOptions options);
    /// <summary>Istruzioni per passare da <paramref name="current"/> a <paramref name="updated"/>; vuoto se non cambia nulla.</summary>
    IReadOnlyList<string> BuildAlterDatabaseSql(DatabaseOptions current, DatabaseOptions updated);
    string BuildDropDatabaseSql(string database);
    /// <summary>Database a cui collegarsi per operazioni a livello server (creazione/eliminazione/rinomina).</summary>
    string MaintenanceDatabase { get; }
    /// <summary>Libera le connessioni in pool (necessario prima di DROP/RENAME DATABASE).</summary>
    void ClearPools();
    bool SupportsDatabaseRename { get; }
    /// <summary>Sintassi dello script (DELIMITER, commenti #, escape con backslash).</summary>
    bool UsesMySqlScriptSyntax { get; }

    /// <summary>Intestazione e chiusura di un dump SQL.</summary>
    string DumpHeader(string database);
    string DumpFooter();
    string BuildInsert(string schema, string table, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows);
    string FormatLiteral(object? value);
    string QuoteIdentifier(string identifier);
    string MapDataType(string genericType);
}
