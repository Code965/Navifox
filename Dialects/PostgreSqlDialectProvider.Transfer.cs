using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Navifox.Core.Models;

namespace Navifox.Dialects;

public sealed partial class PostgreSqlDialectProvider
{
    private const string TransferIdentifier = "(?:\"(?:[^\"]|\"\")+\"|[\\p{L}_][\\p{L}\\p{N}_$]*)";
    private static readonly Regex TransferRelationName = new($"^(?:(?<schema>{TransferIdentifier})\\s*\\.\\s*)?(?<name>{TransferIdentifier})$", RegexOptions.Compiled);

    public override async Task<TableDdl> GetTransferDdlAsync(DbConnection connection, string database, string sourceSchema, string targetSchema, string table, CancellationToken cancellationToken)
    {
        // Il catalogo restituisce nomi qualificati anche per relazioni visibili normalmente in public.
        // Si modifica solo questa sessione dedicata al trasferimento e si ripristina il percorso originale.
        var path = await ReadStringsAsync(connection, "SHOW search_path", new Dictionary<string, object>(), cancellationToken);
        try
        {
            await using (var command = CreateCommand(connection, "SET search_path TO pg_catalog"))
                await command.ExecuteNonQueryAsync(cancellationToken);
            return await ReadTableDdlAsync(connection, database, sourceSchema, table, false, targetSchema, cancellationToken);
        }
        finally
        {
            if (connection.State == ConnectionState.Open && path.Count > 0)
            {
                await using var restore = CreateCommand(connection, $"SELECT set_config('search_path', {FormatLiteral(path[0][0])}, false)");
                restore.CommandTimeout = 5;
                await restore.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    private string TransferRelation(string relation, string sourceSchema, string targetSchema)
    {
        var match = TransferRelationName.Match(relation);
        if (!match.Success) throw new InvalidOperationException($"Nome di relazione non valido: {relation}");
        var schema = match.Groups["schema"].Value;
        var actualSchema = schema.StartsWith('"') ? schema[1..^1].Replace("\"\"", "\"") : schema.ToLowerInvariant();
        if (schema.Length > 0 && actualSchema != sourceSchema) return relation;
        return $"{QuoteIdentifier(targetSchema)}.{match.Groups["name"].Value}";
    }

    private string TransferReference(string sql, string keyword, string sourceSchema, string targetSchema)
    {
        // Solo il nome della relazione: espressioni, tipi, funzioni e stringhe restano intatti.
        var prefix = keyword == "ON"
            ? $"CREATE\\s+(?:UNIQUE\\s+)?INDEX\\s+{TransferIdentifier}\\s+ON\\s+(?:ONLY\\s+)?"
            : $"FOREIGN\\s+KEY\\s*\\(\\s*{TransferIdentifier}(?:\\s*,\\s*{TransferIdentifier})*\\s*\\)\\s+REFERENCES\\s+(?:ONLY\\s+)?";
        var pattern = $"^(?<prefix>{prefix})(?<relation>{TransferIdentifier}(?:\\s*\\.\\s*{TransferIdentifier})?)";
        return Regex.Replace(sql, pattern,
            match => match.Groups["prefix"].Value + TransferRelation(match.Groups["relation"].Value, sourceSchema, targetSchema),
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }
}
