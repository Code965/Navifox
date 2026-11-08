using System.Data.Common;
using System.Security.Cryptography;
using Navifox.Core;
using Navifox.Core.DataGen;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public partial class DataTransferViewModel
{
    private static string SelectTransferColumns(IDialectProvider provider, string schema, string table, IReadOnlyList<string> columns) =>
        $"SELECT {string.Join(", ", columns.Select(provider.QuoteIdentifier))} FROM {provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(table)}";

    private async Task<Dictionary<string, DataMasker?>> PrepareMaskersAsync(IDialectProvider provider, DbConnection connection, string database, string schema,
        IReadOnlyList<TransferTable> tables, CancellationToken token)
    {
        var result = new Dictionary<string, DataMasker?>(StringComparer.Ordinal);
        if (!AnonymizeData || !CopyData) return result;
        var definitions = new Dictionary<string, TableDesign>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, IReadOnlyList<ColumnInfo>>(StringComparer.Ordinal);
        var excluded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            definitions[table.Name] = await provider.GetTableDesignAsync(connection, database, schema, table.Name, token);
            metadata[table.Name] = (await provider.GetColumnsAsync(connection, database, schema, table.Name, token)).Where(c => !c.IsGenerated).ToArray();
            excluded[table.Name] = [];
        }
        foreach (var (table, design) in definitions)
            foreach (var key in design.ForeignKeys)
            {
                excluded[table].UnionWith(key.Columns);
                if ((string.IsNullOrEmpty(key.ReferencedSchema) || key.ReferencedSchema == schema) && excluded.TryGetValue(key.ReferencedTable, out var parent))
                    parent.UnionWith(key.ReferencedColumns);
            }
        // Seme segreto nuovo per ogni trasferimento; condiviso tra tabelle e mai scritto nel registro.
        var salt = RandomNumberGenerator.GetBytes(32);
        foreach (var (table, columns) in metadata)
        {
            var design = definitions[table];
            var unique = design.Indexes.Where(i => i.Kind == "UNIQUE" && i.Columns.Count == 1).Select(i => i.Columns[0]);
            var masker = DataMasker.For(columns, excluded[table], design.Columns, unique, salt);
            result[table] = masker;
            AddLog($"Mascheramento {table}: " + (masker is null ? "nessun campo personale riconosciuto" : string.Join(", ", masker.MaskedColumns.Select(c => $"{c.Column} ({FakeKinds.Label(c.Kind)})"))));
        }
        return result;
    }

    private Task<bool> ConfirmMaskingAsync(IReadOnlyDictionary<string, DataMasker?> maskers)
    {
        if (!AnonymizeData || !CopyData || _dialogs is null) return Task.FromResult(true);
        var fields = string.Join("\n", maskers.Select(p => p.Key + ": " + (p.Value is null ? "nessun campo riconosciuto" : string.Join(", ", p.Value.MaskedColumns.Select(c => c.Column)))));
        return _dialogs.ConfirmAsync("Controlla i campi da mascherare",
            $"Verranno sostituiti questi campi:\n\n{fields}\n\nChiavi primarie, chiavi delle relazioni e campi non riconosciuti restano invariati. Questa opzione non garantisce l’anonimizzazione completa: rivedi i dati prima di distribuirli.\n\nLa sorgente resta invariata.", "Conferma la copia");
    }
}
