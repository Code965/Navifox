using Navifox.Core;
using Navifox.Core.Models;
using Navifox.Dialects;

namespace Navifox.App.Services;

public sealed record TableDeletionResult(bool Confirmed, IReadOnlyList<string> Deleted, string? Error = null);

public sealed class TableDeletionService(IDatabaseAccessService access, IDialogService dialogs)
{
    public async Task<TableDeletionResult> DeleteAsync(ConnectionProfile profile, string database, string schema, IReadOnlyList<string> names, bool views, CancellationToken token)
    {
        if (await SqlGuard.BlockIfReadOnlyAsync(dialogs, profile, "Eliminazione delle tabelle selezionate.")) return new(false, []);
        var selected = names.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) return new(false, []);
        var provider = access.Provider(profile);
        await using var connection = await access.OpenAsync(profile, database, token);
        var definitions = new Dictionary<string, TableDesign>(StringComparer.Ordinal);
        var existing = await provider.ListTablesAsync(connection, database, schema, token);
        if (selected.Any(n => !existing.Any(t => t.Name == n && t.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase) == views)))
            throw new InvalidOperationException("La selezione non corrisponde più agli oggetti del database. Aggiorna l'elenco prima di eliminare.");
        if (!views)
        {
            foreach (var table in existing.Where(t => !t.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase)))
                definitions[table.Name] = await provider.GetTableDesignAsync(connection, database, schema, table.Name, token);
            var external = definitions.Where(p => !selected.Contains(p.Key)).SelectMany(p => p.Value.ForeignKeys
                .Where(k => (k.ReferencedSchema.Length == 0 || k.ReferencedSchema == schema) && selected.Contains(k.ReferencedTable)).Select(k => $"{p.Key} → {k.ReferencedTable}")).ToArray();
            if (external.Length > 0) throw new InvalidOperationException("Alcune tabelle non selezionate dipendono dalla selezione. Seleziona anche le tabelle collegate oppure rimuovi prima le relazioni:\n" + string.Join("\n", external));
        }
        var ordered = new List<string>();
        var remaining = selected.ToHashSet(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var leaf = remaining.FirstOrDefault(parent => !remaining.Any(child => child != parent && definitions.TryGetValue(child, out var d) && d.ForeignKeys.Any(k => (k.ReferencedSchema.Length == 0 || k.ReferencedSchema == schema) && k.ReferencedTable == parent)));
            if (leaf is null) break;
            remaining.Remove(leaf); ordered.Add(leaf);
        }
        if (remaining.Count > 0 && profile.Kind == DatabaseKind.MySql)
            throw new InvalidOperationException("Le tabelle selezionate hanno relazioni circolari. Rimuovi prima le chiavi esterne del ciclo; i controlli delle relazioni restano attivi.");
        ordered.AddRange(remaining);
        var label = views ? "viste" : "tabelle";
        if (!await dialogs.ConfirmAsync($"Elimina {selected.Count} {label}",
            $"Eliminare definitivamente {selected.Count} {label} da «{profile.Name} / {database} / {schema}»{(profile.IsProduction ? " (PRODUZIONE)" : "")}?\n\n{string.Join("\n", ordered.Select(n => "• " + n))}\n\nVerranno eliminati struttura e dati degli oggetti elencati.", "Elimina selezionate", danger: true)) return new(false, []);
        using var activity = ActivityRecording.BeginOperation(profile, database, "Eliminazione tabelle", string.Join("\n", ordered.Select(n => schema + "." + n)));
        var deleted = new List<string>();
        var atomic = profile.Kind != DatabaseKind.MySql;
        var transaction = false;
        string Qualified(string name) => $"{provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(name)}";
        try
        {
            if (!views && profile.Kind == DatabaseKind.Sqlite) await provider.ExecuteAsync(connection, "PRAGMA foreign_keys = OFF", 0, token);
            if (atomic) { await provider.ExecuteAsync(connection, profile.Kind.BeginTransactionSql(), 0, token); transaction = true; }
            if (remaining.Count > 0 && profile.Kind == DatabaseKind.SqlServer)
                foreach (var table in remaining)
                    foreach (var key in definitions[table].ForeignKeys.Where(k => selected.Contains(k.ReferencedTable) && (k.ReferencedSchema.Length == 0 || k.ReferencedSchema == schema)))
                        await provider.ExecuteAsync(connection, $"ALTER TABLE {Qualified(table)} DROP CONSTRAINT {provider.QuoteIdentifier(key.Name)}", 0, token);
            if (profile.Kind == DatabaseKind.PostgreSql)
            {
                await provider.ExecuteAsync(connection, $"DROP {(views ? "VIEW" : "TABLE")} {string.Join(", ", ordered.Select(Qualified))}", 0, token);
                deleted.AddRange(ordered);
            }
            else foreach (var name in ordered)
            {
                await provider.ExecuteAsync(connection, $"DROP {(views ? "VIEW" : "TABLE")} {Qualified(name)}", 0, token);
                deleted.Add(name);
            }
            if (transaction) { await provider.ExecuteAsync(connection, "COMMIT", 0, token); transaction = false; }
            activity?.Complete();
            return new(true, deleted);
        }
        catch (Exception error)
        {
            if (transaction)
            {
                try { await provider.ExecuteAsync(connection, "ROLLBACK", 0, CancellationToken.None); deleted.Clear(); } catch { }
            }
            activity?.Fail(error);
            return new(true, deleted, error.Message);
        }
        finally
        {
            if (!views && profile.Kind == DatabaseKind.Sqlite)
                try { await provider.ExecuteAsync(connection, "PRAGMA foreign_keys = ON", 0, CancellationToken.None); } catch { }
        }
    }
}
