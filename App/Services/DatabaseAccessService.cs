using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.Services;

public sealed class DatabaseAccessService(IEnumerable<IDialectProvider> providers) : IDatabaseAccessService
{
    private readonly IReadOnlyDictionary<DatabaseKind, IDialectProvider> _providers = providers.ToDictionary(provider => provider.Kind);

    public IDialectProvider Provider(ConnectionProfile profile) => _providers.TryGetValue(profile.Kind, out var provider)
        ? provider
        : throw new NotSupportedException($"Il dialect {profile.Kind} non è disponibile.");

    private Task<System.Data.Common.DbConnection> ConnectAsync(ConnectionProfile profile, string? database, CancellationToken cancellationToken) =>
        Provider(profile).ConnectAsync(database is null ? profile : profile with { DefaultDatabase = database }, cancellationToken);

    public Task<System.Data.Common.DbConnection> OpenAsync(ConnectionProfile profile, string? database, CancellationToken cancellationToken) =>
        ConnectAsync(profile, database, cancellationToken);

    public async Task TestConnectionAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, null, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var provider = Provider(profile);
        await using var connection = await ConnectAsync(profile, string.Empty, cancellationToken);
        return await provider.ListDatabasesAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListSchemasAsync(ConnectionProfile profile, string database, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        return await Provider(profile).ListSchemasAsync(connection, database, cancellationToken);
    }

    public async Task<IReadOnlyList<DatabaseObjectInfo>> ListTablesAsync(ConnectionProfile profile, string database, string schema, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        return await Provider(profile).ListTablesAsync(connection, database, schema, cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(ConnectionProfile profile, string database, string schema, string table, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        return await Provider(profile).GetColumnsAsync(connection, database, schema, table, cancellationToken);
    }

    public async Task<DataPage> QueryPagedAsync(ConnectionProfile profile, string database, string schema, string table, int pageIndex, int pageSize, CancellationToken cancellationToken, string? where = null, string? orderBy = null)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        return await Provider(profile).QueryPagedAsync(connection, schema, table, pageIndex, pageSize, cancellationToken, where, orderBy);
    }

    public async Task<long> CountRowsAsync(ConnectionProfile profile, string database, string schema, string table, string? where, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        return await Provider(profile).CountRowsAsync(connection, schema, table, where, cancellationToken);
    }

    public async Task<IReadOnlyList<StatementResult>> ExecuteBatchAsync(ConnectionProfile profile, string? database, string sql, int maxRows, CancellationToken cancellationToken)
    {
        var provider = Provider(profile);
        var results = new List<StatementResult>();
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        foreach (var statement in SqlScriptSplitter.Split(sql, provider.UsesMySqlScriptSyntax))
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var result = await provider.ExecuteAsync(connection, statement, maxRows, cancellationToken);
                results.Add(new StatementResult(statement, result, null, timer.Elapsed));
                if (AppSettings.Current.RecordHistory) SqlHistory.Record(profile.Name, database, statement, timer.Elapsed, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                results.Add(new StatementResult(statement, null, exception, timer.Elapsed));
                if (AppSettings.Current.RecordHistory) SqlHistory.Record(profile.Name, database, statement, timer.Elapsed, exception.Message);
                break;
            }
        }
        return results;
    }

    public async Task<int> ExecuteScriptFileAsync(ConnectionProfile profile, string? database, string path, IProgress<ScriptProgress>? progress, CancellationToken cancellationToken)
    {
        var provider = Provider(profile);
        var totalBytes = new FileInfo(path).Length;
        // Lo script viene letto per intero: lo splitter lavora su stringa (adatto a file fino a qualche centinaio di MB).
        var script = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        var executed = 0;
        long processedChars = 0;
        var lastReport = Stopwatch.StartNew();
        foreach (var statement in SqlScriptSplitter.Split(script, provider.UsesMySqlScriptSyntax))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await provider.ExecuteAsync(connection, statement, 0, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { throw new ScriptExecutionException(executed + 1, statement, exception); }
            executed++;
            processedChars += statement.Length;
            if (lastReport.ElapsedMilliseconds > 200)
            {
                progress?.Report(new ScriptProgress(executed, Math.Min(totalBytes, processedChars), totalBytes));
                lastReport.Restart();
            }
        }
        progress?.Report(new ScriptProgress(executed, totalBytes, totalBytes));
        if (AppSettings.Current.RecordHistory) SqlHistory.Record(profile.Name, database, $"-- Esecuzione file {Path.GetFileName(path)}: {executed} istruzioni", lastReport.Elapsed, null);
        return executed;
    }

    public async Task ExecuteOnServerAsync(ConnectionProfile profile, IEnumerable<string> statements, CancellationToken cancellationToken)
    {
        var provider = Provider(profile);
        provider.ClearPools();
        // Mai collegati al database da eliminare/rinominare: si usa quello di manutenzione.
        await using (var connection = await ConnectAsync(profile, provider.MaintenanceDatabase, cancellationToken))
        {
            foreach (var statement in statements)
            {
                await provider.ExecuteAsync(connection, statement, 0, cancellationToken);
                if (AppSettings.Current.RecordHistory) SqlHistory.Record(profile.Name, null, statement, TimeSpan.Zero, null);
            }
        }
        provider.ClearPools();
    }

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> GetPropertiesAsync(ConnectionProfile profile, string? database, string? schema, string? table, CancellationToken cancellationToken)
    {
        var provider = Provider(profile);
        await using var connection = await ConnectAsync(profile, database ?? string.Empty, cancellationToken);
        if (database is null) return await provider.GetServerPropertiesAsync(connection, cancellationToken);
        if (table is null || schema is null) return await provider.GetDatabasePropertiesAsync(connection, database, cancellationToken);
        return await provider.GetTablePropertiesAsync(connection, database, schema, table, cancellationToken);
    }

    public async Task<string> GetDdlTextAsync(ConnectionProfile profile, string database, string schema, string table, bool isView, CancellationToken cancellationToken)
    {
        var provider = Provider(profile);
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        var ddl = await provider.GetDdlAsync(connection, database, schema, table, isView, cancellationToken);
        return ddl.PostData.Count == 0 ? ddl.Create : ddl.Create + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, ddl.PostData);
    }

    public async Task<TableDesign> GetTableDesignAsync(ConnectionProfile profile, string database, string schema, string table, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        return await Provider(profile).GetTableDesignAsync(connection, database, schema, table, cancellationToken);
    }

    public async Task ExecuteStatementsAsync(ConnectionProfile profile, string? database, IReadOnlyList<string> statements, CancellationToken cancellationToken, bool atomic = false)
    {
        var provider = Provider(profile);
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        if (!atomic)
        {
            await RunStatementsAsync(provider, connection, profile, database, statements, cancellationToken);
            return;
        }
        // DDL transazionale (PostgreSQL): o tutto o niente.
        await provider.ExecuteAsync(connection, "BEGIN", 0, cancellationToken);
        try
        {
            await RunStatementsAsync(provider, connection, profile, database, statements, cancellationToken);
            await provider.ExecuteAsync(connection, "COMMIT", 0, cancellationToken);
        }
        catch
        {
            try { await provider.ExecuteAsync(connection, "ROLLBACK", 0, CancellationToken.None); } catch { /* connessione non più utilizzabile */ }
            throw;
        }
    }

    public async Task ExecuteSavePlanAsync(ConnectionProfile profile, string? database, SchemaObjectSavePlan plan, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteStatementsAsync(profile, database, plan.Statements, cancellationToken, plan.Atomic);
        }
        catch (Exception first) when (first is not OperationCanceledException)
        {
            if (plan.Fallback is { } fallback)
            {
                await ExecuteSavePlanAsync(profile, database, fallback, cancellationToken);
                return;
            }
            if (plan.Restore is { Count: > 0 } restore)
            {
                string outcome;
                try
                {
                    await ExecuteStatementsAsync(profile, database, restore, CancellationToken.None);
                    outcome = "La versione precedente è stata ripristinata.";
                }
                catch (Exception restoreError)
                {
                    outcome = $"ATTENZIONE: non è stato possibile ripristinare la versione precedente ({restoreError.Message}). Ricrea l'oggetto dalla definizione originale.";
                }
                throw new SchemaObjectSaveException(first, outcome);
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<DatabaseObjectInfo>> ListSchemaObjectsAsync(ConnectionProfile profile, string database, string schema, SchemaObjectKind kind, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        return await Provider(profile).ListSchemaObjectsAsync(connection, database, schema, kind, cancellationToken);
    }

    public async Task<string> GetSchemaObjectDefinitionAsync(ConnectionProfile profile, string database, string schema, SchemaObjectKind kind, DatabaseObjectInfo item, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        return await Provider(profile).GetSchemaObjectDefinitionAsync(connection, database, schema, kind, item, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetRoutineParametersAsync(ConnectionProfile profile, string database, string schema, DatabaseObjectInfo routine, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        return await Provider(profile).GetRoutineParametersAsync(connection, schema, routine, cancellationToken);
    }

    public async Task<SequenceInfo> GetSequenceAsync(ConnectionProfile profile, string database, string schema, string name, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        return await Provider(profile).GetSequenceAsync(connection, schema, name, cancellationToken);
    }

    private static async Task RunStatementsAsync(IDialectProvider provider, System.Data.Common.DbConnection connection, ConnectionProfile profile, string? database, IReadOnlyList<string> statements, CancellationToken cancellationToken)
    {
        foreach (var statement in statements)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                await provider.ExecuteAsync(connection, statement, 0, cancellationToken);
                if (AppSettings.Current.RecordHistory) SqlHistory.Record(profile.Name, database, statement, timer.Elapsed, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (AppSettings.Current.RecordHistory) SqlHistory.Record(profile.Name, database, statement, timer.Elapsed, exception.Message);
                throw new ScriptExecutionException(statements.ToList().IndexOf(statement) + 1, statement, exception);
            }
        }
    }

    public async Task<SchemaModel> GetSchemaModelAsync(ConnectionProfile profile, string database, string schema, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        return await Provider(profile).GetSchemaModelAsync(connection, database, schema, cancellationToken);
    }

    public async Task<IReadOnlyList<CharsetInfo>> ListCharsetsAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, string.Empty, cancellationToken);
        return await Provider(profile).ListCharsetsAsync(connection, cancellationToken);
    }

    public async Task<DatabaseOptions> GetDatabaseOptionsAsync(ConnectionProfile profile, string database, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, string.Empty, cancellationToken);
        return await Provider(profile).GetDatabaseOptionsAsync(connection, database, cancellationToken);
    }

    public async Task DumpDatabaseAsync(ConnectionProfile profile, string database, bool includeData, TextWriter writer, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        const int batchSize = 200;
        var provider = Provider(profile);
        var isMySql = provider.UsesMySqlScriptSyntax;
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        await writer.WriteAsync(provider.DumpHeader(database));
        var schemas = isMySql ? [database] : await provider.ListSchemasAsync(connection, database, cancellationToken);
        var postData = new List<string>();
        foreach (var schema in schemas)
        {
            if (!isMySql && schema != "public") await writer.WriteAsync($"\nCREATE SCHEMA IF NOT EXISTS {provider.QuoteIdentifier(schema)};\n");
            var objects = await provider.ListTablesAsync(connection, database, schema, cancellationToken);
            var tables = objects.Where(item => !item.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase)).ToArray();
            var views = objects.Where(item => item.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase)).ToArray();
            var cascade = isMySql ? string.Empty : " CASCADE";
            foreach (var table in tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Struttura {table.Name}…");
                var name = DumpName(provider, schema, table.Name);
                var ddl = await provider.GetDdlAsync(connection, database, schema, table.Name, false, cancellationToken);
                await writer.WriteAsync($"\n-- ----------------------------\n-- Tabella {name}\n-- ----------------------------\nDROP TABLE IF EXISTS {name}{cascade};\n{ddl.Create}\n");
                postData.AddRange(ddl.PostData);
                if (!includeData) continue;

                var columns = (await provider.GetColumnsAsync(connection, database, schema, table.Name, cancellationToken)).Select(column => column.Name).ToArray();
                var batch = new List<IReadOnlyList<object?>>(batchSize);
                long count = 0;
                await foreach (var row in provider.StreamRowsAsync(connection, schema, table.Name, cancellationToken))
                {
                    batch.Add(row.Count == columns.Length ? row : row.Take(columns.Length).ToArray());
                    if (batch.Count < batchSize) continue;
                    await writer.WriteAsync(provider.BuildInsert(schema, table.Name, columns, batch));
                    count += batch.Count;
                    batch.Clear();
                    if (count % 10_000 == 0) progress?.Report($"Dati {table.Name}: {count:N0} righe…");
                }
                if (batch.Count > 0) await writer.WriteAsync(provider.BuildInsert(schema, table.Name, columns, batch));
            }
            foreach (var view in views)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Vista {view.Name}…");
                var name = DumpName(provider, schema, view.Name);
                var ddl = await provider.GetDdlAsync(connection, database, schema, view.Name, true, cancellationToken);
                await writer.WriteAsync($"\n-- ----------------------------\n-- Vista {name}\n-- ----------------------------\nDROP VIEW IF EXISTS {name}{cascade};\n{ddl.Create}\n");
            }
        }
        if (postData.Count > 0) await writer.WriteAsync("\n-- Vincoli, indici e sequenze\n" + string.Join("\n", postData) + "\n");
        await writer.WriteAsync(provider.DumpFooter());
        await writer.FlushAsync(cancellationToken);
    }

    public async IAsyncEnumerable<IReadOnlyList<object?>> StreamQueryAsync(ConnectionProfile profile, string database, string sql, Action<IReadOnlyList<ColumnInfo>> onColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(profile, database, cancellationToken);
        await foreach (var row in Provider(profile).StreamQueryAsync(connection, sql, onColumns, cancellationToken)) yield return row;
    }

    private static string DumpName(IDialectProvider provider, string schema, string table) =>
        provider.UsesMySqlScriptSyntax ? provider.QuoteIdentifier(table) : $"{provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(table)}";
}
