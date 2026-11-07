using MosaicDB.Core;
using MosaicDB.Core.Models;

namespace MosaicDB.App.Services;

/// <summary>Esito di una singola istruzione all'interno di un batch.</summary>
public sealed record StatementResult(string Sql, QueryResult? Result, Exception? Error, TimeSpan Elapsed);

/// <summary>Errore di uno script: indica quale istruzione è fallita.</summary>
public sealed class ScriptExecutionException(int statementNumber, string statement, Exception inner)
    : Exception($"Istruzione {statementNumber}: {inner.Message}", inner)
{
    public int StatementNumber { get; } = statementNumber;
    public string Statement { get; } = statement;
}

public sealed record ScriptProgress(int Executed, long BytesProcessed, long TotalBytes);

public interface IDatabaseAccessService
{
    IDialectProvider Provider(ConnectionProfile profile);

    /// <summary>Apre una connessione dedicata (es. per le transazioni di una scheda tabella). Va chiusa dal chiamante.</summary>
    Task<System.Data.Common.DbConnection> OpenAsync(ConnectionProfile profile, string? database, CancellationToken cancellationToken);

    Task TestConnectionAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListSchemasAsync(ConnectionProfile profile, string database, CancellationToken cancellationToken);
    Task<IReadOnlyList<DatabaseObjectInfo>> ListTablesAsync(ConnectionProfile profile, string database, string schema, CancellationToken cancellationToken);
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(ConnectionProfile profile, string database, string schema, string table, CancellationToken cancellationToken);
    Task<DataPage> QueryPagedAsync(ConnectionProfile profile, string database, string schema, string table, int pageIndex, int pageSize, CancellationToken cancellationToken, string? where = null, string? orderBy = null);
    Task<long> CountRowsAsync(ConnectionProfile profile, string database, string schema, string table, string? where, CancellationToken cancellationToken);

    /// <summary>Esegue più istruzioni sulla stessa connessione; si ferma alla prima che fallisce.</summary>
    Task<IReadOnlyList<StatementResult>> ExecuteBatchAsync(ConnectionProfile profile, string? database, string sql, int maxRows, CancellationToken cancellationToken);

    /// <summary>Esegue uno script (anche molto grande) istruzione per istruzione; lancia <see cref="ScriptExecutionException"/>.</summary>
    Task<int> ExecuteScriptFileAsync(ConnectionProfile profile, string? database, string path, IProgress<ScriptProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Istruzioni a livello server (CREATE/ALTER/DROP DATABASE) sul database di manutenzione.</summary>
    Task ExecuteOnServerAsync(ConnectionProfile profile, IEnumerable<string> statements, CancellationToken cancellationToken);

    /// <summary>Proprietà per il pannello informazioni: server (database null), database o tabella.</summary>
    Task<IReadOnlyList<KeyValuePair<string, string>>> GetPropertiesAsync(ConnectionProfile profile, string? database, string? schema, string? table, CancellationToken cancellationToken);
    Task<string> GetDdlTextAsync(ConnectionProfile profile, string database, string schema, string table, bool isView, CancellationToken cancellationToken);

    Task<SchemaModel> GetSchemaModelAsync(ConnectionProfile profile, string database, string schema, CancellationToken cancellationToken);

    Task<IReadOnlyList<CharsetInfo>> ListCharsetsAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task<DatabaseOptions> GetDatabaseOptionsAsync(ConnectionProfile profile, string database, CancellationToken cancellationToken);

    Task DumpDatabaseAsync(ConnectionProfile profile, string database, bool includeData, TextWriter writer, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Esegue una SELECT in streaming (export).</summary>
    IAsyncEnumerable<IReadOnlyList<object?>> StreamQueryAsync(ConnectionProfile profile, string database, string sql, Action<IReadOnlyList<ColumnInfo>> onColumns, CancellationToken cancellationToken);
}
