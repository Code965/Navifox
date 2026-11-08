using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Voce della barra comandi (Ctrl+K).</summary>
public sealed record PaletteItem(string Title, string Subtitle, string Glyph, string Group, Func<Task> Run, string? Shortcut = null);

/// <summary>Elemento recente della pagina iniziale.</summary>
public sealed record RecentEntry(string Title, string Subtitle, string Glyph, Func<Task> Open);

/// <summary>Barra comandi Ctrl+K (comandi, connessioni, database, tabelle, oggetti) e pagina iniziale con gli elementi recenti.</summary>
public partial class MainWindowViewModel
{
    [ObservableProperty] private bool _isPaletteOpen;
    [ObservableProperty] private string _paletteQuery = string.Empty;
    [ObservableProperty] private PaletteItem? _selectedPaletteItem;

    public ObservableCollection<PaletteItem> PaletteResults { get; } = [];

    [RelayCommand]
    private void OpenPalette()
    {
        PaletteQuery = string.Empty;
        IsPaletteOpen = true;
        RefreshPalette();
    }

    [RelayCommand]
    private void ClosePalette() => IsPaletteOpen = false;

    partial void OnPaletteQueryChanged(string value) => RefreshPalette();

    [RelayCommand]
    private async Task RunPaletteItemAsync(PaletteItem? item)
    {
        item ??= SelectedPaletteItem ?? PaletteResults.FirstOrDefault();
        if (item is null) return;
        IsPaletteOpen = false;
        await item.Run();
    }

    public void MovePaletteSelection(int delta)
    {
        if (PaletteResults.Count == 0) return;
        var index = SelectedPaletteItem is null ? -1 : PaletteResults.IndexOf(SelectedPaletteItem);
        SelectedPaletteItem = PaletteResults[Math.Clamp(index + delta, 0, PaletteResults.Count - 1)];
    }

    private IEnumerable<PaletteItem> PaletteCommands() =>
    [
        new("Nuova query", "Apre una scheda SQL sul database selezionato", "file-code", "Comandi", () => { NewQueryCommand.Execute(null); return Task.CompletedTask; }),
        new("Nuova connessione", "MySQL, MariaDB, PostgreSQL…", "plus", "Comandi", () => AddConnectionCommand.ExecuteAsync(null)),
        new("Nuova tabella", "Progetta una tabella nel database selezionato", "table", "Comandi", () => { NewTableCommand.Execute(null); return Task.CompletedTask; }),
        new("Console", "Riga di comando SQL", "terminal", "Comandi", () => { OpenConsoleCommand.Execute(null); return Task.CompletedTask; }, "F6"),
        new("Server Monitor", "Processi, variabili e stato dei server", "activity", "Comandi", () => { OpenServerMonitorCommand.Execute(null); return Task.CompletedTask; }),
        new("Trasferimento dati", "Copia tabelle tra database", "transfer", "Comandi", () => OpenDataTransferCommand.ExecuteAsync(null)),
        new("Genera dati di prova", "Dati inventati nella tabella selezionata", "plus", "Comandi", () => OpenDataGeneratorCommand.ExecuteAsync(null)),
        new("Sincronizzazione struttura", "Confronta due database e genera lo script delle differenze", "layers", "Comandi", () => OpenStructureSyncCommand.ExecuteAsync(null)),
        new("Sincronizzazione dati", "Confronta i dati di due database", "sync", "Comandi", () => OpenDataSyncCommand.ExecuteAsync(null)),
        new("Utenti e privilegi", "Utenti, password e permessi del server", "users", "Comandi", () => { OpenUsersCommand.Execute(null); return Task.CompletedTask; }),
        new("Trova nel database", "Cerca un valore in tutte le tabelle", "search", "Comandi", () => { OpenFindCommand.Execute(null); return Task.CompletedTask; }, "Ctrl+Maiusc+F"),
        new("Registro attività", "Query, trasferimenti e passaggi salvati in ordine", "history", "Comandi", () => { OpenHistoryCommand.Execute(null); return Task.CompletedTask; }, "Ctrl+L"),
        new("Modello ER", "Diagramma relazionale del database", "network", "Comandi", () => OpenModelCommand.ExecuteAsync(null)),
        new("Automazione", "Attività pianificate (dump, query, export)", "workflow", "Comandi", () => { OpenAutomationCommand.Execute(null); return Task.CompletedTask; }),
        new("Apri file SQL", "Apre uno script in una nuova query", "folder", "Comandi", () => OpenExternalSqlCommand.ExecuteAsync(null)),
        new("Opzioni", "Preferenze di Navifox", "settings", "Comandi", () => OpenOptionsCommand.ExecuteAsync(null)),
        new("Tema chiaro / scuro", "Cambia tema", "sun-moon", "Comandi", () => { ToggleThemeCommand.Execute(null); return Task.CompletedTask; }),
        new("Mostra/nascondi pannello connessioni", string.Empty, "panel-left", "Comandi", () => { ToggleTree(); return Task.CompletedTask; }, "Ctrl+Maiusc+L"),
        new("Mostra/nascondi pannello informazioni", string.Empty, "panel-right", "Comandi", () => { ToggleInfo(); return Task.CompletedTask; }, "Ctrl+Maiusc+I"),
        new("Verifica aggiornamenti", string.Empty, "download", "Comandi", () => CheckUpdatesCommand.ExecuteAsync(null)),
    ];

    private void RefreshPalette()
    {
        var query = PaletteQuery.Trim();
        var results = new List<(PaletteItem Item, int Score)>();
        void Consider(PaletteItem item)
        {
            if (Score(item.Title, query) is var score and > 0) results.Add((item, score + (item.Group == "Comandi" ? 1 : 0)));
            else if (query.Length > 0 && item.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)) results.Add((item, 1));
        }
        foreach (var command in PaletteCommands()) Consider(command);
        foreach (var profile in Profiles)
            Consider(new PaletteItem(profile.Name, $"Connessione · {Controls.DialectVisuals.Summary(profile)}", "server", "Connessioni", () => SelectAndActivateAsync(ConnectionNodeFor(profile))));
        // Database, tabelle e oggetti già caricati nell'albero.
        foreach (var node in RootNodes.SelectMany(n => n.SelfAndDescendants()))
        {
            if (node.Kind is not (TreeNodeKind.Database or TreeNodeKind.Table or TreeNodeKind.DbObject) || node.Profile is null) continue;
            var where = node.Kind == TreeNodeKind.Database ? node.Profile.Name : $"{node.Profile.Name} · {node.Database}";
            var group = node.Kind switch { TreeNodeKind.Database => "Database", TreeNodeKind.Table => node.Category == "Viste" ? "Viste" : "Tabelle", _ => node.Category ?? "Oggetti" };
            var target = node;
            Consider(new PaletteItem(node.Name, where, node.Glyph, group, async () =>
            {
                await SelectAndActivateAsync(target);
                if (target.Kind is TreeNodeKind.Table or TreeNodeKind.DbObject) await OpenNodeCommand.ExecuteAsync(target);
            }));
        }
        PaletteResults.Clear();
        foreach (var (item, _) in results.OrderByDescending(r => r.Score).ThenBy(r => r.Item.Title.Length).Take(query.Length == 0 ? 40 : 60)) PaletteResults.Add(item);
        SelectedPaletteItem = PaletteResults.FirstOrDefault();
    }

    /// <summary>Punteggio: inizio parola &gt; contenuto &gt; iniziali/sottosequenza (es. «bol» trova «books_open_library»).</summary>
    private static int Score(string text, string query)
    {
        if (query.Length == 0) return 1;
        if (text.Equals(query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 80;
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index > 0) return char.IsLetterOrDigit(text[index - 1]) ? 50 : 65;
        var position = 0;
        foreach (var c in query)
        {
            position = text.IndexOf(c.ToString(), position, StringComparison.OrdinalIgnoreCase);
            if (position < 0) return 0;
            position++;
        }
        return 20;
    }

    private TreeNodeViewModel? ConnectionNodeFor(ConnectionProfile profile) =>
        RootNodes.SelectMany(n => n.SelfAndDescendants()).FirstOrDefault(n => n.Kind == TreeNodeKind.Connection && n.Profile?.Id == profile.Id);

    private async Task SelectAndActivateAsync(TreeNodeViewModel? node)
    {
        if (node is null) return;
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        IsTreeVisible = true;
        SelectedNode = node;
        await Task.Delay(50);
    }

    // ───────────── Pagina iniziale ─────────────

    public ObservableCollection<RecentEntry> RecentConnectionEntries { get; } = [];
    public ObservableCollection<RecentEntry> RecentTableEntries { get; } = [];
    public ObservableCollection<RecentEntry> RecentFileEntries { get; } = [];
    public bool HasRecentTables => RecentTableEntries.Count > 0;
    public bool HasRecentFiles => RecentFileEntries.Count > 0;

    public void RefreshStartPage()
    {
        RecentConnectionEntries.Clear();
        var recent = AppSettings.Current.RecentConnections.Select(id => Profiles.FirstOrDefault(p => p.Id.ToString() == id)).OfType<ConnectionProfile>()
            .Concat(Profiles.OrderByDescending(p => p.IsFavorite).ThenBy(p => p.Name)).Distinct().Take(6);
        foreach (var profile in recent)
        {
            var p = profile;
            RecentConnectionEntries.Add(new RecentEntry(p.Name, Controls.DialectVisuals.Summary(p), "server", () => SelectAndActivateAsync(ConnectionNodeFor(p))));
        }
        RecentTableEntries.Clear();
        foreach (var entry in AppSettings.Current.RecentTables.Take(8))
        {
            var parts = entry.Split('|');
            if (parts.Length != 4 || Profiles.FirstOrDefault(p => p.Id.ToString() == parts[0]) is not { } profile) continue;
            var (database, schema, table) = (parts[1], parts[2], parts[3]);
            RecentTableEntries.Add(new RecentEntry(table, $"{profile.Name} · {database}", "table", () => OpenTableAsync(profile, database, schema, table)));
        }
        RecentFileEntries.Clear();
        foreach (var path in AppSettings.Current.RecentFiles.Take(6))
        {
            var file = path;
            RecentFileEntries.Add(new RecentEntry(Path.GetFileName(file), Path.GetDirectoryName(file) ?? string.Empty, "file-code", () => OpenSqlFileAsync(file)));
        }
        OnPropertyChanged(nameof(HasRecentTables));
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    [RelayCommand]
    private Task OpenRecentEntryAsync(RecentEntry? entry) => entry?.Open() ?? Task.CompletedTask;

    private static void Remember(List<string> list, string value, int max)
    {
        list.RemoveAll(v => v == value);
        list.Insert(0, value);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        AppSettings.Current.Save();
    }

    private void RememberTable(ConnectionProfile profile, string database, string schema, string table)
    {
        Remember(AppSettings.Current.RecentTables, $"{profile.Id}|{database}|{schema}|{table}", 20);
        RefreshStartPage();
    }

    private void RememberConnection(ConnectionProfile profile)
    {
        Remember(AppSettings.Current.RecentConnections, profile.Id.ToString(), 10);
        RefreshStartPage();
    }
}
