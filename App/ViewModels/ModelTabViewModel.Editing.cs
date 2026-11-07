using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>
/// Modifica del modello ER: nuove relazioni trascinando da una colonna a un'altra tabella, eliminazione delle relazioni,
/// nuova tabella / progetta tabella, script SQL dell'intero modello e posizioni delle tabelle salvate.
/// </summary>
public partial class ModelTabViewModel
{
    /// <summary>Servizi forniti da chi apre la scheda (finestra principale).</summary>
    public IDialogService? Dialogs { get; init; }
    /// <summary>Apre «Progetta tabella» (null = nuova tabella).</summary>
    public Action<string?>? DesignTable { get; init; }
    /// <summary>Apre una nuova query con lo script indicato.</summary>
    public Action<string>? OpenScript { get; init; }

    /// <summary>Strumento «Nuova relazione»: trascinando da una colonna a un'altra tabella si crea la chiave esterna.</summary>
    [ObservableProperty] private bool _isLinkMode;

    public bool CanEdit => !IsLoading;

    // ───────────── Posizioni salvate ─────────────

    private string LayoutKey => $"{_profile.Id}|{Database}|{Schema}";

    /// <summary>Applica le posizioni salvate; false se mancano tabelle (serve la disposizione automatica).</summary>
    public bool ApplySavedLayout(Func<DiagramTable, (double Width, double Height)> measure)
    {
        if (!AppSettings.Current.ModelLayouts.TryGetValue(LayoutKey, out var saved) || string.IsNullOrWhiteSpace(saved)) return false;
        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        foreach (var entry in saved.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('\t');
            if (parts.Length == 3 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                positions[parts[0]] = (x, y);
        }
        var known = Tables.Count(t => positions.ContainsKey(t.Name));
        if (known == 0) return false;
        // Tabelle nuove: sotto le altre.
        var bottom = Tables.Where(t => positions.ContainsKey(t.Name)).Select(t => positions[t.Name].Y + measure(t).Height).DefaultIfEmpty(0).Max() + 60;
        var left = 40d;
        foreach (var table in Tables)
        {
            table.Width = measure(table).Width;
            if (positions.TryGetValue(table.Name, out var p)) (table.X, table.Y) = p;
            else
            {
                (table.X, table.Y) = (left, bottom);
                left += table.Width + 60;
            }
        }
        return true;
    }

    public void SaveLayout()
    {
        if (Tables.Count == 0) return;
        AppSettings.Current.ModelLayouts[LayoutKey] = string.Join("\n", Tables.Select(t => $"{t.Name}\t{t.X.ToString("0.#", CultureInfo.InvariantCulture)}\t{t.Y.ToString("0.#", CultureInfo.InvariantCulture)}"));
        AppSettings.Current.Save();
    }

    public void ForgetLayout()
    {
        if (AppSettings.Current.ModelLayouts.Remove(LayoutKey)) AppSettings.Current.Save();
    }

    // ───────────── Tabelle ─────────────

    [RelayCommand]
    private void NewTable() => DesignTable?.Invoke(null);

    [RelayCommand]
    private void DesignSelected()
    {
        if (SelectedTable is { IsView: false } table) DesignTable?.Invoke(table.Name);
    }

    public void Design(DiagramTable table) => DesignTable?.Invoke(table.Name);

    // ───────────── Relazioni ─────────────

    /// <summary>Colonna di destinazione proposta: quella indicata, altrimenti la chiave primaria (a una colonna) della tabella.</summary>
    public static string? TargetColumn(DiagramTable to, string? column)
    {
        if (column is not null) return column;
        var keys = to.AllColumns.Where(c => c.IsPrimaryKey).ToArray();
        return keys.Length == 1 ? keys[0].Name : null;
    }

    public async Task CreateRelationAsync(DiagramTable from, string fromColumn, DiagramTable to, string? toColumn)
    {
        if (Dialogs is null) return;
        if (from.IsView || to.IsView)
        {
            StatusMessage = "Le relazioni si creano solo tra tabelle (non con le viste).";
            return;
        }
        var target = TargetColumn(to, toColumn);
        if (target is null)
        {
            await Dialogs.ShowMessageAsync("Nuova relazione", $"«{to.Name}» non ha una chiave primaria a una sola colonna: trascina la relazione direttamente sulla colonna di destinazione.", isError: true);
            return;
        }
        var name = UniqueKeyName($"fk_{from.Name}_{to.Name}");
        var choice = await Dialogs.PromptAsync("Nuova relazione",
            $"{from.Name}.{fromColumn}  →  {to.Name}.{target}\n\nNome della chiave esterna:", name, "Avanti");
        if (string.IsNullOrWhiteSpace(choice)) return;
        await ChangeKeysAsync(from.Name, design => design with
        {
            ForeignKeys = [.. design.ForeignKeys, new DesignForeignKey(null, choice.Trim(), [fromColumn], Schema, to.Name, [target], "NO ACTION", "NO ACTION")],
        }, $"Creare la relazione «{choice.Trim()}»");
    }

    public async Task DeleteRelationAsync(DiagramRelation relation)
    {
        await ChangeKeysAsync(relation.From.Name, design => design with
        {
            ForeignKeys = design.ForeignKeys.Where(k => k.Name != relation.Name && k.OriginalName != relation.Name).ToArray(),
        }, $"Eliminare la relazione «{relation.Name}» ({relation.From.Name} → {relation.To.Name})");
    }

    private string UniqueKeyName(string baseName)
    {
        var used = Relations.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = baseName;
        for (var i = 2; used.Contains(name); i++) name = $"{baseName}_{i}";
        return name;
    }

    /// <summary>Legge la struttura, applica la modifica, mostra l'SQL e lo esegue dopo la conferma.</summary>
    private async Task ChangeKeysAsync(string table, Func<TableDesign, TableDesign> change, string question)
    {
        if (Dialogs is null) return;
        try
        {
            var design = await _databaseAccess.GetTableDesignAsync(_profile, Database, Schema, table, CancellationToken.None);
            var statements = TableDesignSql.Build(_profile.Kind, Schema, design, change(design));
            if (statements.Count == 0) return;
            var preview = string.Join(";\n\n", statements) + ";";
            if (!await Dialogs.ConfirmAsync("Modifica del modello", $"{question}?\n\nVerranno eseguite queste istruzioni:\n\n{preview}", "Esegui")) return;
            if (!await SqlGuard.ConfirmProductionAsync(Dialogs, _profile, $"La struttura della tabella «{table}» verrà modificata.")) return;
            IsLoading = true;
            await _databaseAccess.ExecuteStatementsAsync(_profile, Database, statements, CancellationToken.None,
                atomic: _profile.Kind is DatabaseKind.SqlServer or DatabaseKind.Sqlite or DatabaseKind.PostgreSql);
            SaveLayout();
            await LoadAsync();
            StatusMessage = $"Modello aggiornato · {statements.Count} istruzioni eseguite su «{table}».";
        }
        catch (Exception exception)
        {
            IsLoading = false;
            await Dialogs.ShowMessageAsync("Modifica non riuscita", exception.Message, isError: true);
        }
    }

    // ───────────── Forward engineering ─────────────

    /// <summary>Script SQL completo del modello: tabelle in ordine di dipendenza, poi chiavi esterne, poi viste.</summary>
    [RelayCommand]
    private async Task GenerateSqlAsync()
    {
        if (OpenScript is null || Tables.Count == 0) return;
        IsLoading = true;
        StatusMessage = "Generazione dello script…";
        try
        {
            var provider = _databaseAccess.Provider(_profile);
            await using var connection = await _databaseAccess.OpenAsync(_profile, Database, CancellationToken.None);
            var separator = _profile.Kind == DatabaseKind.SqlServer ? "\nGO\n" : "\n";
            var script = new StringBuilder($"-- Modello {Database}{(Schema == Database ? string.Empty : "." + Schema)} · generato da Navifox il {DateTime.Now:yyyy-MM-dd HH:mm}\n");
            var post = new List<string>();
            foreach (var table in DependencyOrder(Tables.Where(t => !t.IsView)))
            {
                var ddl = await provider.GetDdlAsync(connection, Database, Schema, table.Name, false, CancellationToken.None);
                script.Append($"\n-- Tabella {table.Name}\n").Append(ddl.Create.Trim()).Append(separator);
                post.AddRange(ddl.PostData);
            }
            if (post.Count > 0) script.Append("\n-- Chiavi esterne\n").Append(string.Join(separator == "\n" ? ";\n" : separator, post.Select(p => p.TrimEnd(';')))).Append(separator == "\n" ? ";\n" : separator);
            foreach (var view in Tables.Where(t => t.IsView))
            {
                var ddl = await provider.GetDdlAsync(connection, Database, Schema, view.Name, true, CancellationToken.None);
                script.Append($"\n-- Vista {view.Name}\n").Append(ddl.Create.Trim()).Append(separator);
            }
            OpenScript(script.ToString());
            StatusMessage = $"Script del modello generato ({Tables.Count} oggetti).";
        }
        catch (Exception exception) { StatusMessage = $"Generazione non riuscita: {exception.Message}"; }
        finally { IsLoading = false; }
    }

    /// <summary>Prima le tabelle riferite, poi quelle che le riferiscono (i cicli restano in ordine alfabetico).</summary>
    private IEnumerable<DiagramTable> DependencyOrder(IEnumerable<DiagramTable> tables)
    {
        var pending = tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var done = new HashSet<DiagramTable>();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(t => Relations.Where(r => r.From == t && r.To != t).All(r => done.Contains(r.To) || !pending.Contains(r.To))) ?? pending[0];
            pending.Remove(ready);
            done.Add(ready);
            yield return ready;
        }
    }
}
