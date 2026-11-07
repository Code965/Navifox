using CommunityToolkit.Mvvm.Input;
using Navifox.App.Controls;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Tabelle copiate con Ctrl+C (dall'albero o dalla scheda Oggetti).</summary>
public sealed record TableClipboard(ConnectionProfile Profile, string Database, string Schema, IReadOnlyList<string> Tables)
{
    public string Label => Tables.Count == 1 ? $"«{Tables[0]}»" : $"{Tables.Count} tabelle";
}

/// <summary>
/// Copia/incolla di tabelle: stesso database → copia della tabella (struttura e dati);
/// altro database o connessione dello stesso dialetto → Trasferimento dati già impostato.
/// Tra dialetti diversi (MySQL ↔ PostgreSQL) l'incolla è bloccato.
/// </summary>
public partial class MainWindowViewModel
{
    private TableClipboard? _clipboard;

    public bool HasClipboard => _clipboard is not null;

    /// <summary>Tabelle selezionate nella scheda Oggetti (impostate dalla vista, per la copia multipla).</summary>
    public IReadOnlyList<ObjectRowViewModel> SelectedObjectRows { get; set; } = [];

    [RelayCommand]
    private void CopyTables(TreeNodeViewModel? node)
    {
        TableClipboard? clip = null;
        if (node is null && SelectedTab == ObjectsTab && ObjectsTab.Source is { Profile: { } p, Database: { } d, Schema: { } s })
        {
            var rows = SelectedObjectRows.Count > 0 ? SelectedObjectRows : ObjectsTab.SelectedObject is { } one ? [one] : [];
            var tables = rows.Where(r => !r.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase)).Select(r => r.Name).ToArray();
            if (tables.Length > 0) clip = new TableClipboard(p, d, s, tables);
        }
        else if ((node ?? SelectedNode) is { Kind: TreeNodeKind.Table, Profile: { } tp, Database: { } td, Schema: { } ts, Category: not "Viste" } table)
            clip = new TableClipboard(tp, td, ts, [table.Name]);
        else if ((node ?? SelectedNode) is { Kind: TreeNodeKind.Category, Category: "Tabelle", Profile: { } cp, Database: { } cd, Schema: { } cs, Objects: { } objects })
            clip = new TableClipboard(cp, cd, cs, objects.Select(o => o.Name).ToArray());

        if (clip is null)
        {
            StatusMessage = "Seleziona una o più tabelle da copiare.";
            return;
        }
        _clipboard = clip;
        OnPropertyChanged(nameof(HasClipboard));
        StatusMessage = $"Copiato {clip.Label} da {clip.Profile.Name} · {clip.Database}. Seleziona un database e premi Ctrl+V.";
    }

    [RelayCommand]
    private async Task PasteTablesAsync(TreeNodeViewModel? node)
    {
        if (_clipboard is not { } clip)
        {
            StatusMessage = "Niente da incollare: copia prima una tabella (Ctrl+C).";
            return;
        }
        if (CurrentDatabaseContext(node) is not { } target)
        {
            StatusMessage = "Seleziona il database di destinazione nell’albero e premi Ctrl+V.";
            return;
        }
        if (target.Profile.Kind != clip.Profile.Kind)
        {
            await _dialogs.ShowMessageAsync("Incolla non disponibile",
                $"Non è possibile incollare tabelle da {DialectVisuals.DialectName(clip.Profile)} ({clip.Profile.Name}) a {DialectVisuals.DialectName(target.Profile)} ({target.Profile.Name}).\n\n" +
                "Copia e incolla tra connessioni sono possibili solo tra database dello stesso tipo: MySQL ↔ MySQL oppure PostgreSQL ↔ PostgreSQL.", isError: true);
            return;
        }

        var sameDatabase = target.Profile.Id == clip.Profile.Id && target.Database == clip.Database && target.Schema == clip.Schema;
        if (!sameDatabase)
        {
            // Trasferimento già impostato: origine, destinazione e tabelle; si apre sul riepilogo.
            await _dialogs.ShowDataTransferAsync(new DataTransferViewModel(_databaseAccess, Profiles.ToArray(), clip.Profile, clip.Database, _dialogs,
                target.Profile, target.Database, clip.Tables, jumpToSummary: true));
            await RefreshTablesOfAsync(target.Profile, target.Database, target.Schema);
            return;
        }
        await DuplicateTablesAsync(clip, withData: true);
    }

    /// <summary>Incolla nello stesso database: crea «nome_copy» (struttura con indici e dati).</summary>
    private async Task DuplicateTablesAsync(TableClipboard clip, bool withData)
    {
        var provider = _databaseAccess.Provider(clip.Profile);
        var mysql = provider.UsesMySqlScriptSyntax;
        var existing = (await _databaseAccess.ListTablesAsync(clip.Profile, clip.Database, clip.Schema, CancellationToken.None))
            .Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(string Source, string Copy)>();
        foreach (var table in clip.Tables)
        {
            var name = $"{table}_copy";
            for (var i = 2; existing.Contains(name); i++) name = $"{table}_copy{i}";
            existing.Add(name);
            plan.Add((table, name));
        }
        var list = string.Join("\n", plan.Select(p => $"  • {p.Source} → {p.Copy}"));
        if (!await _dialogs.ConfirmAsync("Duplica tabelle", $"Creare una copia ({(withData ? "struttura e dati" : "solo struttura")}) in «{clip.Database}»?\n\n{list}", "Crea copia")) return;

        string Name(string table) => mysql ? provider.QuoteIdentifier(table) : $"{provider.QuoteIdentifier(clip.Schema)}.{provider.QuoteIdentifier(table)}";
        var errors = new List<string>();
        foreach (var (source, copy) in plan)
        {
            var sql = mysql
                ? $"CREATE TABLE {Name(copy)} LIKE {Name(source)};" + (withData ? $"\nINSERT INTO {Name(copy)} SELECT * FROM {Name(source)};" : string.Empty)
                : $"CREATE TABLE {Name(copy)} (LIKE {Name(source)} INCLUDING ALL);" + (withData ? $"\nINSERT INTO {Name(copy)} OVERRIDING SYSTEM VALUE SELECT * FROM {Name(source)};\n{ResyncSequencesSql(Name(copy))}" : string.Empty);
            StatusMessage = $"Copia di {source} in corso…";
            var results = await _databaseAccess.ExecuteBatchAsync(clip.Profile, clip.Database, sql, 0, CancellationToken.None);
            if (results.FirstOrDefault(r => r.Error is not null) is { } failed) errors.Add($"{source}: {failed.Error!.Message}");
        }
        await RefreshTablesOfAsync(clip.Profile, clip.Database, clip.Schema);
        if (errors.Count > 0) await _dialogs.ShowMessageAsync("Copia non completata", string.Join("\n", errors), isError: true);
        else StatusMessage = plan.Count == 1 ? $"Creata la tabella «{plan[0].Copy}»." : $"Create {plan.Count} copie di tabelle.";
    }

    /// <summary>
    /// PostgreSQL: dopo la copia dei dati riallinea i contatori di identità/serial della nuova tabella al valore massimo,
    /// altrimenti il primo INSERT ripartirebbe da 1 violando la chiave primaria.
    /// </summary>
    private static string ResyncSequencesSql(string qualifiedTable)
    {
        var literal = "'" + qualifiedTable.Replace("'", "''") + "'";
        return "DO $nf$ DECLARE r record; BEGIN " +
               $"FOR r IN SELECT a.attname, pg_get_serial_sequence({literal}, a.attname) AS seq FROM pg_attribute a " +
               $"WHERE a.attrelid = CAST({literal} AS regclass) AND a.attnum > 0 AND NOT a.attisdropped LOOP " +
               "IF r.seq IS NOT NULL THEN " +
               $"EXECUTE format('SELECT setval(%L, COALESCE((SELECT MAX(%I) FROM %s), 0) + 1, false)', r.seq, r.attname, {literal}); " +
               "END IF; END LOOP; END $nf$;";
    }

    /// <summary>Ricarica la categoria «Tabelle» del database indicato, se già aperta nell'albero.</summary>
    private async Task RefreshTablesOfAsync(ConnectionProfile profile, string database, string schema)
    {
        if (!_connectionNodes.TryGetValue(profile.Id, out var connection)) return;
        var category = connection.SelfAndDescendants().FirstOrDefault(n => n.Kind == TreeNodeKind.Category && n.Category == "Tabelle" && n.Database == database && n.Schema == schema);
        if (category is null) return;
        if (category.IsLoaded) await RefreshNodeAsync(category);
        if (ObjectsTab.Source == category) ShowCategoryObjects(category, bringToFront: false);
    }
}
