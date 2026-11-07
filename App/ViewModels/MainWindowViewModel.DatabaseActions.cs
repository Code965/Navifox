using System.Text;
using CommunityToolkit.Mvvm.Input;
using MosaicDB.App.Services;
using MosaicDB.Core.Models;

namespace MosaicDB.App.ViewModels;

/// <summary>Azioni del menu contestuale dell'albero (database, connessioni, tabelle).</summary>
public partial class MainWindowViewModel
{
    private const string Soon = "Disponibile in una fase successiva";

    public IReadOnlyList<ContextAction> GetContextActions(TreeNodeViewModel node) => node.Kind switch
    {
        TreeNodeKind.Connection =>
        [
            node.IsLoaded
                ? new ContextAction("Chiudi connessione", DisconnectSelectedCommand, null, "unplug")
                : new ContextAction("Apri connessione", ConnectSelectedCommand, null, "power"),
            ContextAction.Separator,
            new("Nuovo database…", NewDatabaseCommand, node, "plus"),
            new("Nuova query", NewQueryCommand, node, "file-code"),
            ContextAction.Separator,
            new("Console…", OpenConsoleCommand, node, "terminal"),
            new("Server Monitor", OpenServerMonitorCommand, node, "activity"),
            new("Esegui file SQL…", ExecuteSqlFileCommand, node, "play"),
            ContextAction.Separator,
            new("Aggiorna", RefreshNodeCommand, node, "refresh", node.IsLoaded),
            ContextAction.Separator,
            new("Elimina connessione", DeleteSelectedConnectionCommand, null, "trash"),
        ],
        TreeNodeKind.Database =>
        [
            new("Chiudi database", CloseDatabaseCommand, node, "unplug", node.IsLoaded),
            ContextAction.Separator,
            new("Modifica database…", EditDatabaseCommand, node, "pencil"),
            new("Nuovo database…", NewDatabaseCommand, node, "plus"),
            new("Elimina database", DeleteDatabaseCommand, node, "trash"),
            ContextAction.Separator,
            new("Nuova query", NewQueryCommand, node, "file-code"),
            ContextAction.Separator,
            new("Console…", OpenConsoleCommand, node, "terminal"),
            new("Esegui file SQL…", ExecuteSqlFileCommand, node, "play"),
            new("Dump file SQL", Glyph: "export", Children:
            [
                new("Struttura e dati…", DumpDatabaseCommand, new DumpRequest(node, true)),
                new("Solo struttura…", DumpDatabaseCommand, new DumpRequest(node, false)),
            ]),
            new("Stampa database", Glyph: "printer", IsEnabled: false, ToolTip: Soon),
            new("Reverse database a modello…", OpenModelCommand, node, "network"),
            new("Trova nel database…", OpenFindCommand, node, "search"),
            new("Trasferimento dati…", OpenDataTransferCommand, node, "transfer"),
            new("Incolla tabelle", PasteTablesCommand, node, "download", HasClipboard),
            ContextAction.Separator,
            new("Aggiorna", RefreshNodeCommand, node, "refresh"),
        ],
        TreeNodeKind.Schema or TreeNodeKind.Category =>
        [
            new("Nuova query", NewQueryCommand, node, "file-code"),
            new("Modello", OpenModelCommand, node, "network"),
            new("Trova…", OpenFindCommand, node, "search"),
            new("Copia tutte le tabelle", CopyTablesCommand, node, "copy", node.Category == "Tabelle" && node.Objects is not null),
            new("Incolla tabelle", PasteTablesCommand, node, "download", HasClipboard),
            ContextAction.Separator,
            new("Aggiorna", RefreshNodeCommand, node, "refresh"),
        ],
        TreeNodeKind.Table => TableContextActions(node),
        TreeNodeKind.Group => [new("Nuova connessione…", AddConnectionCommand, null, "plus")],
        _ => []
    };

    // ───────────── Database ─────────────

    [RelayCommand]
    private void CloseDatabase(TreeNodeViewModel? node)
    {
        if (node is null) return;
        if (ObjectsTab.Source is { } source && IsUnder(source, node)) ObjectsTab.Clear();
        node.Reset();
        StatusMessage = $"Database «{node.Name}» chiuso.";
    }

    [RelayCommand]
    private async Task RefreshNodeAsync(TreeNodeViewModel? node)
    {
        if (node is null || node.Profile is null) return;
        var showObjects = ObjectsTab.Source is { } source && IsUnder(source, node);
        var selectedName = ObjectsTab.SelectedObject?.Name;
        node.Reset();
        node.IsExpanded = true;
        await EnsureLoadedAsync(node);
        if (!showObjects) return;
        var category = node.IsObjectCategory ? node : node.Kind is TreeNodeKind.Database or TreeNodeKind.Schema ? FindTablesCategory(node) : null;
        if (category is null) return;
        await EnsureLoadedAsync(category);
        ShowCategoryObjects(category, selectedName);
    }

    [RelayCommand]
    private async Task NewDatabaseAsync(TreeNodeViewModel? node)
    {
        var connection = node is null ? null : ConnectionOf(node);
        if (connection?.Profile is not { } profile) return;
        try
        {
            var charsets = await _databaseAccess.ListCharsetsAsync(profile, CancellationToken.None);
            var dialog = new DatabaseDialogViewModel(_databaseAccess.Provider(profile), charsets, null);
            if (!await _dialogs.ShowDatabaseDialogAsync(dialog)) return;
            await _databaseAccess.ExecuteOnServerAsync(profile, dialog.Statements, CancellationToken.None);
            await ReloadConnectionAsync(connection, dialog.Result.Name);
            StatusMessage = $"Database «{dialog.Result.Name}» creato.";
        }
        catch (Exception exception) { await ReportErrorAsync("Impossibile creare il database", exception); }
    }

    [RelayCommand]
    private async Task EditDatabaseAsync(TreeNodeViewModel? node)
    {
        if (node?.Profile is not { } profile || node.Database is not { } database) return;
        var connection = ConnectionOf(node);
        try
        {
            var charsets = await _databaseAccess.ListCharsetsAsync(profile, CancellationToken.None);
            var current = await _databaseAccess.GetDatabaseOptionsAsync(profile, database, CancellationToken.None);
            var dialog = new DatabaseDialogViewModel(_databaseAccess.Provider(profile), charsets, current);
            if (!await _dialogs.ShowDatabaseDialogAsync(dialog)) return;
            await _databaseAccess.ExecuteOnServerAsync(profile, dialog.Statements, CancellationToken.None);
            if (dialog.Result.Name != database && connection is not null)
            {
                CloseTabsFor(profile.Id, database);
                await ReloadConnectionAsync(connection, dialog.Result.Name);
            }
            StatusMessage = $"Database «{dialog.Result.Name}» aggiornato.";
        }
        catch (Exception exception) { await ReportErrorAsync("Impossibile modificare il database", exception); }
    }

    [RelayCommand]
    private async Task DeleteDatabaseAsync(TreeNodeViewModel? node)
    {
        if (node?.Profile is not { } profile || node.Database is not { } database) return;
        var confirmed = await _dialogs.ConfirmAsync("Elimina database",
            $"Eliminare definitivamente il database «{database}» su «{profile.Name}»?\n\nTutte le tabelle e i dati verranno persi. L’operazione non può essere annullata.",
            "Elimina", danger: true);
        if (!confirmed) return;
        try
        {
            var sql = _databaseAccess.Provider(profile).BuildDropDatabaseSql(database);
            await _databaseAccess.ExecuteOnServerAsync(profile, [sql], CancellationToken.None);
            if (ObjectsTab.Source is { } source && IsUnder(source, node)) ObjectsTab.Clear();
            CloseTabsFor(profile.Id, database);
            node.Parent?.Children.Remove(node);
            if (SelectedNode == node) SelectedNode = node.Parent;
            StatusMessage = $"Database «{database}» eliminato.";
        }
        catch (Exception exception) { await ReportErrorAsync($"Impossibile eliminare «{database}»", exception); }
    }

    // ───────────── Query / console ─────────────

    [RelayCommand]
    private void NewQuery(TreeNodeViewModel? node) => OpenQueryTab(node ?? SelectedNode, isConsole: false);

    [RelayCommand]
    private void OpenConsole(TreeNodeViewModel? node) => OpenQueryTab(node ?? SelectedNode, isConsole: true);

    private void OpenQueryTab(TreeNodeViewModel? node, bool isConsole)
    {
        var profile = node?.Profile;
        if (profile is null)
        {
            StatusMessage = "Seleziona una connessione o un database nell’albero.";
            return;
        }
        var tab = new QueryTabViewModel(_databaseAccess, profile, node!.Database, isConsole, Profiles.ToArray(), _dialogs);
        if (!isConsole && node.Kind == TreeNodeKind.Table)
        {
            var provider = _databaseAccess.Provider(profile);
            tab.SqlText = $"SELECT * FROM {provider.QuoteIdentifier(node.Schema!)}.{provider.QuoteIdentifier(node.Name)} LIMIT 100;";
        }
        OpenTabs.Add(tab);
        SelectedTab = tab;
    }

    // ───────────── File SQL ─────────────

    [RelayCommand]
    private async Task ExecuteSqlFileAsync(TreeNodeViewModel? node)
    {
        if (node?.Profile is not { } profile) return;
        var path = await _dialogs.PickOpenFileAsync("Esegui file SQL", new FileFilter("Script SQL", "*.sql"), new FileFilter("Tutti i file", "*.*"));
        if (path is null) return;
        var target = node.Database ?? profile.Name;
        var executed = 0;
        Exception? failure = null;
        var progress = new Progress<ScriptProgress>(value =>
            StatusMessage = $"Esecuzione {Path.GetFileName(path)} su {target}: {value.Executed:N0} istruzioni · {(value.TotalBytes == 0 ? 100 : value.BytesProcessed * 100 / value.TotalBytes)}%");
        await RunOperationAsync(async token =>
        {
            try { executed = await Task.Run(() => _databaseAccess.ExecuteScriptFileAsync(profile, node.Database, path, progress, token), token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { failure = exception; }
        }, CancellationToken.None);

        if (failure is ScriptExecutionException scriptError)
        {
            var excerpt = scriptError.Statement.Length > 400 ? scriptError.Statement[..400] + "…" : scriptError.Statement;
            await _dialogs.ShowMessageAsync("Esecuzione interrotta",
                $"L’istruzione n. {scriptError.StatementNumber} di «{Path.GetFileName(path)}» non è riuscita:\n\n{scriptError.InnerException?.Message}\n\n{excerpt}", isError: true);
            StatusMessage = $"Script interrotto all’istruzione {scriptError.StatementNumber}.";
        }
        else if (failure is not null) await ReportErrorAsync("Impossibile eseguire lo script", failure);
        else if (!StatusMessage.StartsWith("Operazione annullata", StringComparison.Ordinal))
            StatusMessage = $"«{Path.GetFileName(path)}» eseguito su {target}: {executed:N0} istruzioni.";

        if (node.Kind is TreeNodeKind.Database or TreeNodeKind.Connection && node.IsLoaded) await RefreshNodeAsync(node);
    }

    [RelayCommand]
    private async Task DumpDatabaseAsync(DumpRequest? request)
    {
        if (request?.Node is not { Profile: { } profile, Database: { } database }) return;
        var suffix = request.IncludeData ? string.Empty : "_struttura";
        var path = await _dialogs.PickSaveFileAsync(request.IncludeData ? "Dump SQL (struttura e dati)" : "Dump SQL (solo struttura)",
            $"{database}{suffix}_{DateTime.Now:yyyyMMdd_HHmm}.sql", "sql", new FileFilter("Script SQL", "*.sql"));
        if (path is null) return;
        Exception? failure = null;
        var progress = new Progress<string>(message => StatusMessage = $"Dump {database}: {message}");
        await RunOperationAsync(async token =>
        {
            try
            {
                await Task.Run(async () =>
                {
                    await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    await _databaseAccess.DumpDatabaseAsync(profile, database, request.IncludeData, writer, progress, token);
                }, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { failure = exception; }
        }, CancellationToken.None);
        if (failure is not null) await ReportErrorAsync($"Dump di «{database}» non riuscito", failure);
        else if (File.Exists(path) && !StatusMessage.StartsWith("Operazione annullata", StringComparison.Ordinal))
            StatusMessage = $"Dump di «{database}» salvato in {path} ({new FileInfo(path).Length / 1024d:N0} KB).";
    }

    // ───────────── Utilità ─────────────

    private async Task ReloadConnectionAsync(TreeNodeViewModel connection, string? selectDatabase)
    {
        if (ObjectsTab.Source is { } source && IsUnder(source, connection)) ObjectsTab.Clear();
        connection.Reset();
        connection.IsExpanded = true;
        await EnsureLoadedAsync(connection);
        if (selectDatabase is not null && connection.Children.FirstOrDefault(child => child.Name == selectDatabase) is { } created)
            SelectedNode = created;
    }

    private void CloseTabsFor(Guid profileId, string database)
    {
        foreach (var tab in OpenTabs.ToArray())
        {
            var matches = tab switch
            {
                TableTabViewModel table => table.ProfileId == profileId && table.Database == database,
                _ => false
            };
            if (matches) CloseTab(tab);
        }
    }

    private static bool IsUnder(TreeNodeViewModel node, TreeNodeViewModel ancestor)
    {
        for (var current = node; current is not null; current = current.Parent)
            if (current == ancestor) return true;
        return false;
    }

    private async Task ReportErrorAsync(string title, Exception exception)
    {
        StatusMessage = $"{title}: {exception.Message}";
        await _dialogs.ShowMessageAsync(title, exception.Message, isError: true);
    }
}
