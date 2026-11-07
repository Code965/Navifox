using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;
using Navifox.Core;

namespace Navifox.App.ViewModels;

public sealed record TableGroupRequest(TreeNodeViewModel Table, string? Group);
public sealed record FavoriteEntry(string Key, string Title, string Subtitle, string Glyph);

/// <summary>
/// Query salvate, backup, gruppi di tabelle, preferiti, stampa, collegamenti rapidi sul desktop e aree di lavoro (sessione).
/// </summary>
public partial class MainWindowViewModel
{
    // ───────────── Categorie locali: Query salvate e Backup ─────────────

    private void LoadLocalCategory(TreeNodeViewModel node)
    {
        var profile = node.Profile!;
        var database = node.Database!;
        if (node.Category == "Query salvate")
        {
            var queries = LibraryStore.QueriesFor(profile.Id, database);
            node.Objects = queries.Select(q => new DatabaseObjectInfo(q.Name, "QUERY", ModifiedAt: q.Modified, Detail: Preview(q.Sql), Identity: q.Id.ToString())).ToArray();
            node.SetChildren(node.Objects.Select(info => new TreeNodeViewModel(info.Name, "bookmark", TreeNodeKind.SavedQuery, profile, database, node.Schema, node.Category) { Info = info }));
        }
        else
        {
            var files = LibraryStore.Backups(profile.Id, database);
            node.Objects = files.Select(f => new DatabaseObjectInfo(f.Name, "BACKUP", DataBytes: f.Length, ModifiedAt: f.LastWriteTime,
                Detail: $"{f.LastWriteTime:g} · {FormatSize(f.Length)}", Identity: f.FullName)).ToArray();
            node.SetChildren(node.Objects.Select(info => new TreeNodeViewModel(info.Name, "archive", TreeNodeKind.BackupFile, profile, database, node.Schema, node.Category) { Info = info }));
        }
    }

    private static string Preview(string sql)
    {
        var line = string.Join(' ', sql.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.0} GB",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024d):0} KB"
    };

    private void OnSavedQueriesChanged(Guid profileId, string database)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (!_connectionNodes.TryGetValue(profileId, out var connection)) return;
                await EnsureLoadedAsync(connection);
                connection.IsExpanded = true;
                var databaseNode = connection.Children.FirstOrDefault(n => n.Kind == TreeNodeKind.Database && n.Database == database);
                if (databaseNode is not null)
                {
                    await EnsureLoadedAsync(databaseNode);
                    databaseNode.IsExpanded = true;
                    var schemaNode = databaseNode.Children.FirstOrDefault(n => n.Kind == TreeNodeKind.Schema && n.Schema == connection.Profile!.Kind.DefaultSchema(database))
                        ?? databaseNode.Children.FirstOrDefault(n => n.Kind == TreeNodeKind.Schema);
                    if (schemaNode is not null) { await EnsureLoadedAsync(schemaNode); schemaNode.IsExpanded = true; }
                }
                foreach (var category in connection.SelfAndDescendants().Where(n => n.Kind == TreeNodeKind.Category && n.Category == "Query salvate" && n.Database == database).ToArray())
                {
                    LoadLocalCategory(category);
                    category.IsExpanded = true;
                    if (ObjectsTab.Source == category) ShowCategoryObjects(category, bringToFront: false);
                }
                if (HasTreeFilter) ApplyTreeFilter();
            }
            catch (Exception exception) { StatusMessage = $"Query salvata, ma non è stato possibile aggiornare l’albero: {exception.Message}"; }
        });
    }

    private TreeNodeViewModel? FindCategoryNode(Guid profileId, string database, string category) =>
        _connectionNodes.TryGetValue(profileId, out var connection)
            ? connection.SelfAndDescendants().FirstOrDefault(n => n.Kind == TreeNodeKind.Category && n.Category == category && n.Database == database)
            : null;

    private async Task RefreshCategoryNodeAsync(TreeNodeViewModel category)
    {
        await RefreshNodeAsync(category);
        if (ObjectsTab.Source == category) ShowCategoryObjects(category, bringToFront: false);
    }

    private IReadOnlyList<ContextAction> LocalCategoryActions(TreeNodeViewModel node) => node.Category == "Query salvate"
        ?
        [
            new("Nuova query", NewQueryCommand, node, "file-code"),
            new("Apri la cartella delle query", OpenQueryFolderCommand, node, "folder"),
            ContextAction.Separator,
            new("Aggiorna", RefreshNodeCommand, node, "refresh"),
        ]
        :
        [
            new("Nuovo backup", CreateBackupCommand, node, "archive"),
            new("Ripristina da file…", RestoreBackupFromFileCommand, node, "import"),
            new("Apri la cartella dei backup", OpenBackupFolderCommand, node, "folder"),
            ContextAction.Separator,
            new("Aggiorna", RefreshNodeCommand, node, "refresh"),
        ];

    private IReadOnlyList<ContextAction> SavedQueryActions(TreeNodeViewModel node) =>
    [
        new("Apri query", OpenLibraryItemCommand, node, "file-code"),
        new("Esegui", RunSavedQueryCommand, node, "play"),
        ContextAction.Separator,
        new("Rinomina…", RenameSavedQueryCommand, node, "pencil"),
        new("Elimina", DeleteLibraryItemCommand, node, "trash"),
        new("Aggiungi ai preferiti", AddFavoriteCommand, node, "star"),
        ContextAction.Separator,
        new("Aggiorna", RefreshNodeCommand, node.CategoryNode, "refresh"),
    ];

    [RelayCommand]
    private void OpenQueryFolder(TreeNodeViewModel? node)
    {
        if (node is not { Profile: { } profile, Database: { } database }) return;
        var directory = LibraryStore.QueryDirectory(profile.Id, database);
        Directory.CreateDirectory(directory);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true }); }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private IReadOnlyList<ContextAction> BackupActions(TreeNodeViewModel node) =>
    [
        new("Ripristina backup…", OpenLibraryItemCommand, node, "import"),
        new("Apri come query", OpenBackupAsQueryCommand, node, "file-code"),
        new("Apri la cartella dei backup", OpenBackupFolderCommand, node.CategoryNode, "folder"),
        ContextAction.Separator,
        new("Elimina backup", DeleteLibraryItemCommand, node, "trash"),
        ContextAction.Separator,
        new("Nuovo backup", CreateBackupCommand, node.CategoryNode, "archive"),
        new("Aggiorna", RefreshNodeCommand, node.CategoryNode, "refresh"),
    ];

    [RelayCommand]
    private async Task OpenLibraryItemAsync(TreeNodeViewModel? node)
    {
        if (node is { Kind: TreeNodeKind.SavedQuery, Info.Identity: { } id, Profile: { } profile } && LibraryStore.Query(Guid.Parse(id)) is { } query)
        {
            if (OpenTabs.OfType<QueryTabViewModel>().FirstOrDefault(t => t.SavedQueryId == query.Id) is { } open)
            {
                SelectedTab = open;
                return;
            }
            var tab = new QueryTabViewModel(_databaseAccess, profile, query.Database, false, Profiles.ToArray(), _dialogs) { SqlText = query.Sql };
            tab.AttachSavedQuery(query);
            OpenTabs.Add(tab);
            SelectedTab = tab;
            return;
        }
        if (node is { Kind: TreeNodeKind.BackupFile, Info.Identity: { } path, Profile: { } backupProfile, Database: { } database })
            await RestoreBackupAsync(backupProfile, database, path);
    }

    [RelayCommand]
    private async Task RunSavedQueryAsync(TreeNodeViewModel? node)
    {
        await OpenLibraryItemAsync(node);
        if (SelectedTab is QueryTabViewModel tab) await tab.ExecuteAllCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task RenameSavedQueryAsync(TreeNodeViewModel? node)
    {
        if (node?.Info?.Identity is not { } id || LibraryStore.Query(Guid.Parse(id)) is not { } query) return;
        var name = await _dialogs.PromptAsync("Rinomina query", "Nuovo nome:", query.Name, "Rinomina");
        if (string.IsNullOrWhiteSpace(name)) return;
        query.Name = name.Trim();
        var saved = LibraryStore.SaveQuery(query);
        foreach (var tab in OpenTabs.OfType<QueryTabViewModel>().Where(t => t.SavedQueryId == query.Id)) tab.AttachSavedQuery(saved);
    }

    [RelayCommand]
    private async Task DeleteLibraryItemAsync(TreeNodeViewModel? node)
    {
        if (node?.Info?.Identity is not { } identity) return;
        if (node.Kind == TreeNodeKind.SavedQuery)
        {
            if (!await _dialogs.ConfirmAsync("Elimina query", $"Eliminare la query salvata «{node.Name}»?", "Elimina", danger: true)) return;
            LibraryStore.DeleteQuery(Guid.Parse(identity));
            StatusMessage = $"Query «{node.Name}» eliminata.";
            return;
        }
        if (!await _dialogs.ConfirmAsync("Elimina backup", $"Eliminare il file di backup «{node.Name}»?\n\nIl file viene cancellato definitivamente.", "Elimina", danger: true)) return;
        try { File.Delete(identity); }
        catch (Exception exception) { await ReportErrorAsync("Impossibile eliminare il backup", exception); return; }
        if (node.CategoryNode is { } category) await RefreshCategoryNodeAsync(category);
        StatusMessage = $"Backup «{node.Name}» eliminato.";
    }

    [RelayCommand]
    private async Task CreateBackupAsync(TreeNodeViewModel? node)
    {
        if (CurrentDatabaseContext(node) is not { } context) return;
        var directory = LibraryStore.BackupDirectory(context.Profile.Id, context.Database);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{context.Database}_{DateTime.Now:yyyyMMdd_HHmmss}.sql");
        var progress = new Progress<string>(message => StatusMessage = $"Backup di {context.Database}: {message}");
        await RunOperationAsync(async token =>
        {
            await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            await _databaseAccess.DumpDatabaseAsync(context.Profile, context.Database, includeData: true, writer, progress, token);
        }, CancellationToken.None);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            StatusMessage = "Backup non riuscito.";
            return;
        }
        StatusMessage = $"Backup creato: {Path.GetFileName(path)} ({FormatSize(new FileInfo(path).Length)}).";
        Notify($"Backup di «{context.Database}» completato", Path.GetFileName(path));
        if (FindCategoryNode(context.Profile.Id, context.Database, "Backup") is { } category)
        {
            if (category.IsLoaded) await RefreshCategoryNodeAsync(category);
        }
    }

    [RelayCommand]
    private async Task RestoreBackupFromFileAsync(TreeNodeViewModel? node)
    {
        if (CurrentDatabaseContext(node) is not { } context) return;
        var path = await _dialogs.PickOpenFileAsync("Ripristina da file SQL", new FileFilter("Script SQL", "*.sql"), new FileFilter("Tutti i file", "*.*"));
        if (path is not null) await RestoreBackupAsync(context.Profile, context.Database, path);
    }

    private async Task RestoreBackupAsync(ConnectionProfile profile, string database, string path)
    {
        if (!await _dialogs.ConfirmAsync("Ripristina backup",
                $"Eseguire «{Path.GetFileName(path)}» su «{database}» ({profile.Name})?\n\nLe tabelle presenti nel backup vengono eliminate e ricreate con i dati del backup.", "Ripristina", danger: true)) return;
        if (!await SqlGuard.ConfirmProductionAsync(_dialogs, profile, $"Stai per ripristinare «{Path.GetFileName(path)}» sul database «{database}»: i dati attuali delle tabelle coinvolte verranno sostituiti.")) return;
        var target = FindCategoryNode(profile.Id, database, "Tabelle")?.CategoryNode;
        await ExecuteSqlFileOnAsync(profile, database, path);
        if (FindCategoryNode(profile.Id, database, "Tabelle") is { } tables) await RefreshCategoryNodeAsync(tables);
        Notify("Ripristino completato", $"{Path.GetFileName(path)} → {database}");
    }

    [RelayCommand]
    private async Task OpenBackupAsQueryAsync(TreeNodeViewModel? node)
    {
        if (node is not { Info.Identity: { } path, Profile: { } profile, Database: { } database }) return;
        if (new FileInfo(path).Length > 20_000_000)
        {
            await _dialogs.ShowMessageAsync("Backup troppo grande", "Il file supera i 20 MB: aprilo con un editor esterno oppure usa «Ripristina backup».");
            return;
        }
        var tab = new QueryTabViewModel(_databaseAccess, profile, database, false, Profiles.ToArray(), _dialogs) { SqlText = await File.ReadAllTextAsync(path), FilePath = path };
        OpenTabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void OpenBackupFolder(TreeNodeViewModel? node)
    {
        if (CurrentDatabaseContext(node) is not { } context) return;
        var directory = LibraryStore.BackupDirectory(context.Profile.Id, context.Database);
        Directory.CreateDirectory(directory);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true }); }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    // ───────────── Gruppi di tabelle ─────────────

    private static string GroupKey(TreeNodeViewModel node) => $"{node.Profile!.Id}|{node.Database}|{node.Schema}";

    private Dictionary<string, string> GroupsOf(string key) =>
        AppSettings.Current.TableGroups.Where(g => g.Key == key && g.Table is not null)
            .GroupBy(g => g.Table!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Group, StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> GroupNames(string key) =>
        AppSettings.Current.TableGroups.Where(g => g.Key == key).Select(g => g.Group).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Categoria «Tabelle»: le tabelle assegnate a un gruppo vanno sotto la cartella del gruppo.</summary>
    private void ApplyTableGroups(TreeNodeViewModel category)
    {
        var key = GroupKey(category);
        var names = GroupNames(key);
        if (names.Count == 0) return;
        var assignment = GroupsOf(key);
        var tables = category.Children.Where(c => c.Kind == TreeNodeKind.Table).ToList();
        var nodes = new List<TreeNodeViewModel>();
        foreach (var name in names)
        {
            var group = new TreeNodeViewModel(name, "folder", TreeNodeKind.TableGroup, category.Profile, category.Database, category.Schema, category.Category);
            group.SetChildren(tables.Where(t => assignment.TryGetValue(t.Name, out var g) && string.Equals(g, name, StringComparison.OrdinalIgnoreCase)).ToArray());
            nodes.Add(group);
        }
        nodes.AddRange(tables.Where(t => !assignment.ContainsKey(t.Name)));
        category.SetChildren(nodes);
    }

    private IReadOnlyList<ContextAction> ManageGroupActions(TreeNodeViewModel table)
    {
        var category = table.CategoryNode ?? table.Parent!;
        var key = GroupKey(category);
        var current = GroupsOf(key).GetValueOrDefault(table.Name);
        var actions = GroupNames(key).Where(g => !string.Equals(g, current, StringComparison.OrdinalIgnoreCase))
            .Select(g => new ContextAction(g, MoveTableToGroupCommand, new TableGroupRequest(table, g), "folder")).ToList();
        if (current is not null) actions.Add(new ContextAction("Togli dal gruppo", MoveTableToGroupCommand, new TableGroupRequest(table, null), "x"));
        if (actions.Count > 0) actions.Add(ContextAction.Separator);
        actions.Add(new ContextAction("Nuovo gruppo…", NewTableGroupCommand, table, "plus"));
        return actions;
    }

    private IReadOnlyList<ContextAction> TableGroupActions(TreeNodeViewModel group) =>
    [
        new("Rinomina gruppo…", RenameTableGroupCommand, group, "pencil"),
        new("Elimina gruppo", DeleteTableGroupCommand, group, "trash"),
        ContextAction.Separator,
        new("Nuova tabella", NewTableCommand, group, "plus"),
    ];

    [RelayCommand]
    private async Task NewTableGroupAsync(TreeNodeViewModel? node)
    {
        if (node is null) return;
        var name = await _dialogs.PromptAsync("Nuovo gruppo", "Nome del gruppo di tabelle:", string.Empty, "Crea");
        if (string.IsNullOrWhiteSpace(name)) return;
        var category = node.Kind == TreeNodeKind.Category ? node : node.CategoryNode!;
        var key = GroupKey(category);
        AppSettings.Current.TableGroups.Add(new TableGroupEntry { Key = key, Group = name.Trim(), Table = node.Kind == TreeNodeKind.Table ? node.Name : null });
        AppSettings.Current.Save();
        await RefreshCategoryNodeAsync(category);
    }

    [RelayCommand]
    private async Task MoveTableToGroupAsync(TableGroupRequest? request)
    {
        if (request?.Table is not { } table || table.CategoryNode is not { } category) return;
        var key = GroupKey(category);
        var groups = AppSettings.Current.TableGroups;
        groups.RemoveAll(g => g.Key == key && string.Equals(g.Table, table.Name, StringComparison.OrdinalIgnoreCase));
        if (request.Group is not null) groups.Add(new TableGroupEntry { Key = key, Group = request.Group, Table = table.Name });
        AppSettings.Current.Save();
        await RefreshCategoryNodeAsync(category);
    }

    [RelayCommand]
    private async Task RenameTableGroupAsync(TreeNodeViewModel? group)
    {
        if (group is not { Kind: TreeNodeKind.TableGroup } || group.CategoryNode is not { } category) return;
        var name = await _dialogs.PromptAsync("Rinomina gruppo", "Nuovo nome:", group.Name, "Rinomina");
        if (string.IsNullOrWhiteSpace(name)) return;
        var key = GroupKey(category);
        foreach (var entry in AppSettings.Current.TableGroups.Where(g => g.Key == key && string.Equals(g.Group, group.Name, StringComparison.OrdinalIgnoreCase))) entry.Group = name.Trim();
        AppSettings.Current.Save();
        await RefreshCategoryNodeAsync(category);
    }

    [RelayCommand]
    private async Task DeleteTableGroupAsync(TreeNodeViewModel? group)
    {
        if (group is not { Kind: TreeNodeKind.TableGroup } || group.CategoryNode is not { } category) return;
        var key = GroupKey(category);
        AppSettings.Current.TableGroups.RemoveAll(g => g.Key == key && string.Equals(g.Group, group.Name, StringComparison.OrdinalIgnoreCase));
        AppSettings.Current.Save();
        await RefreshCategoryNodeAsync(category);
        StatusMessage = $"Gruppo «{group.Name}» eliminato (le tabelle restano).";
    }

    // ───────────── Preferiti ─────────────

    public IReadOnlyList<FavoriteEntry> Favorites => AppSettings.Current.Favorites.Select(DescribeFavorite).OfType<FavoriteEntry>().ToArray();

    private FavoriteEntry? DescribeFavorite(string key)
    {
        var parts = key.Split('|');
        if (parts[0] == "query" && parts.Length == 2 && Guid.TryParse(parts[1], out var id) && LibraryStore.Query(id) is { } query)
            return new FavoriteEntry(key, query.Name, $"Query · {query.Database}", "bookmark");
        if (parts[0] == "table" && parts.Length == 5 && Profiles.FirstOrDefault(p => p.Id.ToString() == parts[1]) is { } profile)
            return new FavoriteEntry(key, parts[4], $"{profile.Name} · {parts[2]}", "table");
        return null;
    }

    [RelayCommand]
    private void AddFavorite(TreeNodeViewModel? node)
    {
        string? key = node switch
        {
            { Kind: TreeNodeKind.Table, Profile: { } p } => $"table|{p.Id}|{node.Database}|{node.Schema}|{node.Name}",
            { Kind: TreeNodeKind.SavedQuery, Info.Identity: { } id } => $"query|{id}",
            null when SelectedTab is TableTabViewModel table => $"table|{table.ProfileId}|{table.Database}|{table.Schema}|{table.TableName}",
            null when SelectedTab is QueryTabViewModel { SavedQueryId: { } queryId } => $"query|{queryId}",
            _ => null
        };
        if (key is null)
        {
            StatusMessage = "Si possono aggiungere ai preferiti tabelle e query salvate.";
            return;
        }
        if (!AppSettings.Current.Favorites.Contains(key)) AppSettings.Current.Favorites.Add(key);
        AppSettings.Current.Save();
        OnPropertyChanged(nameof(Favorites));
        StatusMessage = $"«{DescribeFavorite(key)?.Title}» aggiunto ai preferiti.";
    }

    [RelayCommand]
    private async Task OpenFavoriteAsync(string? key)
    {
        if (key is null) return;
        var parts = key.Split('|');
        if (parts[0] == "query" && Guid.TryParse(parts[1], out var id) && LibraryStore.Query(id) is { } query && Profiles.FirstOrDefault(p => p.Id == query.ProfileId) is { } queryProfile)
        {
            var tab = OpenTabs.OfType<QueryTabViewModel>().FirstOrDefault(t => t.SavedQueryId == id);
            if (tab is null)
            {
                tab = new QueryTabViewModel(_databaseAccess, queryProfile, query.Database, false, Profiles.ToArray(), _dialogs) { SqlText = query.Sql };
                tab.AttachSavedQuery(query);
                OpenTabs.Add(tab);
            }
            SelectedTab = tab;
        }
        else if (parts[0] == "table" && parts.Length == 5 && Profiles.FirstOrDefault(p => p.Id.ToString() == parts[1]) is { } profile)
            await OpenTableAsync(profile, parts[2], parts[3], parts[4]);
    }

    [RelayCommand]
    private void RemoveFavorite(string? key)
    {
        if (key is null) return;
        AppSettings.Current.Favorites.Remove(key);
        AppSettings.Current.Save();
        OnPropertyChanged(nameof(Favorites));
    }

    // ───────────── Stampa (report HTML da stampare dal browser) ─────────────

    private const string ReportCss = """
                <style>
                body{font:12px/1.45 "Segoe UI",system-ui,sans-serif;color:#1d1b19;margin:28px}
                h1{font-size:20px;margin:0 0 2px} h2{font-size:15px;margin:26px 0 6px;color:#c2410c;border-bottom:2px solid #e8590c;padding-bottom:3px}
                .meta{color:#6b6560;margin-bottom:14px} table{border-collapse:collapse;width:100%;margin:6px 0 10px}
                th,td{border:1px solid #ddd6cf;padding:4px 7px;text-align:left;vertical-align:top} th{background:#f6f1ec}
                .k{color:#b7791f;font-weight:600} .small{color:#6b6560;font-size:11px} section{break-inside:avoid}
                @media print{body{margin:12mm} h2{break-after:avoid}}
                </style>
                """;


    [RelayCommand]
    private async Task PrintAsync(TreeNodeViewModel? node)
    {
        if (node?.Profile is not { } profile || node.Database is not { } database) return;
        var schema = node.Schema ?? profile.Kind.DefaultSchema(database);
        var provider = _databaseAccess.Provider(profile);
        StatusMessage = "Preparazione della stampa…";
        try
        {
            await using var connection = await _databaseAccess.OpenAsync(profile, database, CancellationToken.None);
            var tables = node.Kind == TreeNodeKind.Table
                ? [node.Name]
                : (await provider.ListTablesAsync(connection, database, schema, CancellationToken.None)).Where(t => !t.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase)).Select(t => t.Name).ToArray();
            var html = new StringBuilder();
            string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
            html.Append("<!doctype html><html lang=\"it\"><head><meta charset=\"utf-8\"><title>").Append(E(database)).Append(" – Navifox</title>");
            html.Append(ReportCss);
            html.Append("</head><body onload=\"setTimeout(()=>print(),400)\">");
            html.Append($"<h1>{E(node.Kind == TreeNodeKind.Table ? node.Name : database)}</h1>");
            html.Append($"<div class=\"meta\">{E(profile.Name)} · {E(Controls.DialectVisuals.Summary(profile))} · database {E(database)} · {DateTime.Now:g} · {tables.Length} tabelle</div>");
            foreach (var table in tables)
            {
                var design = await provider.GetTableDesignAsync(connection, database, schema, table, CancellationToken.None);
                html.Append($"<section><h2>{E(table)}</h2>");
                if (!string.IsNullOrEmpty(design.Comment)) html.Append($"<div class=\"small\">{E(design.Comment)}</div>");
                html.Append("<table><tr><th>Campo</th><th>Tipo</th><th>Null</th><th>Predefinito</th><th>Chiave</th><th>Commento</th></tr>");
                foreach (var c in design.Columns)
                    html.Append($"<tr><td>{E(c.Name)}</td><td>{E(c.Type)}{(c.Length is { } l ? $"({l}{(c.Decimals is { } d ? $",{d}" : string.Empty)})" : string.Empty)}{(c.AutoIncrement ? " · auto" : string.Empty)}</td><td>{(c.NotNull ? "NO" : "sì")}</td><td>{E(c.Default)}</td><td class=\"k\">{(c.KeyOrder > 0 ? "PK" : string.Empty)}</td><td>{E(c.Comment)}</td></tr>");
                html.Append("</table>");
                if (design.Indexes.Count > 0)
                    html.Append("<div class=\"small\"><b>Indici:</b> " + string.Join(" · ", design.Indexes.Select(i => $"{E(i.Name)} ({E(i.Kind)}: {E(string.Join(", ", i.Columns))})")) + "</div>");
                if (design.ForeignKeys.Count > 0)
                    html.Append("<div class=\"small\"><b>Chiavi esterne:</b> " + string.Join(" · ", design.ForeignKeys.Select(k => $"{E(string.Join(", ", k.Columns))} → {E(k.ReferencedTable)}({E(string.Join(", ", k.ReferencedColumns))})")) + "</div>");
                if (node.Kind == TreeNodeKind.Table)
                {
                    var data = await provider.QueryPagedAsync(connection, schema, table, 0, 200, CancellationToken.None);
                    html.Append($"<h2>Dati (prime {data.Rows.Count} righe)</h2><table><tr>{string.Concat(data.Columns.Select(c => $"<th>{E(c.Name)}</th>"))}</tr>");
                    foreach (var row in data.Rows) html.Append("<tr>" + string.Concat(row.Select(v => $"<td>{E(v is null ? "NULL" : Convert.ToString(v, System.Globalization.CultureInfo.CurrentCulture))}</td>")) + "</tr>");
                    html.Append("</table>");
                }
                html.Append("</section>");
            }
            html.Append("</body></html>");
            var path = Path.Combine(Path.GetTempPath(), $"navifox_{(node.Kind == TreeNodeKind.Table ? node.Name : database)}_{DateTime.Now:HHmmss}.html");
            await File.WriteAllTextAsync(path, html.ToString());
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusMessage = "Report aperto nel browser: si apre la finestra di stampa (puoi anche salvare in PDF).";
        }
        catch (Exception exception) { await ReportErrorAsync("Stampa non riuscita", exception); }
    }

    // ───────────── Collegamento rapido sul desktop ─────────────

    /// <summary>Crea sul desktop un collegamento che apre Navifox direttamente sulla tabella.</summary>
    [RelayCommand]
    private async Task CreateShortcutAsync(TreeNodeViewModel? node)
    {
        if (node is not { Kind: TreeNodeKind.Table, Profile: { } profile, Database: { } database, Schema: { } schema }) return;
        try
        {
            // Installazione Velopack: il collegamento punta all'avvio stabile (…\Navifox\Navifox.exe), non alla cartella della versione.
            var exe = Environment.ProcessPath!;
            var stable = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!, "Navifox.exe");
            if (Path.GetFileName(Path.GetDirectoryName(exe)) == "current" && File.Exists(stable)) exe = stable;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var file = Path.Combine(desktop, $"{node.Name} ({profile.Name}).lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Collegamenti non supportati su questo sistema.");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(file);
            link.TargetPath = exe;
            link.Arguments = $"--open \"table|{profile.Id}|{database}|{schema}|{node.Name}\"";
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = $"Apri {node.Name} in Navifox";
            link.Save();
            StatusMessage = $"Collegamento «{Path.GetFileName(file)}» creato sul desktop.";
        }
        catch (Exception exception) { await ReportErrorAsync("Impossibile creare il collegamento", exception); }
    }

    /// <summary>Argomento --open «table|…» (collegamenti rapidi): apre la tabella all'avvio.</summary>
    public string? PendingOpen { get; set; }

    private async Task ProcessPendingOpenAsync()
    {
        if (PendingOpen is not { } key) return;
        PendingOpen = null;
        await OpenFavoriteAsync(key);
    }

    // ───────────── Sessione e aree di lavoro ─────────────

    private Workspace CaptureWorkspace()
    {
        var workspace = new Workspace();
        foreach (var tab in OpenTabs)
        {
            switch (tab)
            {
                case TableTabViewModel table:
                    workspace.Tabs.Add(new SessionTab { Kind = "table", ProfileId = table.ProfileId, Database = table.Database, Schema = table.Schema, Table = table.TableName });
                    break;
                case QueryTabViewModel { IsConsole: false } query when query.SelectedProfile is { } profile:
                    workspace.Tabs.Add(new SessionTab { Kind = "query", ProfileId = profile.Id, Database = query.SelectedDatabase, Sql = query.SqlText, FilePath = query.FilePath, SavedQueryId = query.SavedQueryId });
                    break;
            }
        }
        workspace.Selected = SelectedTab is null ? 0 : Math.Max(0, OpenTabs.IndexOf(SelectedTab));
        return workspace;
    }

    private async Task RestoreWorkspaceAsync(Workspace workspace)
    {
        foreach (var tab in workspace.Tabs)
        {
            if (Profiles.FirstOrDefault(p => p.Id == tab.ProfileId) is not { } profile) continue;
            if (tab.Kind == "table" && tab.Database is { } db && tab.Schema is { } schema && tab.Table is { } table)
            {
                var view = new TableTabViewModel(_databaseAccess, _dialogs, profile, db, schema, table, false);
                OpenTabs.Add(view);
                _ = view.LoadAsync(CancellationToken.None);
            }
            else if (tab.Kind == "query")
            {
                var query = new QueryTabViewModel(_databaseAccess, profile, tab.Database, false, Profiles.ToArray(), _dialogs) { SqlText = tab.Sql ?? string.Empty, FilePath = tab.FilePath };
                if (tab.SavedQueryId is { } id && LibraryStore.Query(id) is { } saved) query.AttachSavedQuery(saved);
                OpenTabs.Add(query);
            }
        }
        if (workspace.Selected > 0 && workspace.Selected < OpenTabs.Count) SelectedTab = OpenTabs[workspace.Selected];
        await Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions WorkspaceJson = new() { WriteIndented = true };

    /// <summary>Chiamato alla chiusura della finestra: ricorda le schede aperte.</summary>
    public void SaveSession()
    {
        try
        {
            AppSettings.Current.LastSession = JsonSerializer.Serialize(CaptureWorkspace(), WorkspaceJson);
            AppSettings.Current.Save();
        }
        catch { /* la sessione è facoltativa */ }
    }

    private async Task RestoreSessionAsync()
    {
        if (!AppSettings.Current.RestoreSession || string.IsNullOrWhiteSpace(AppSettings.Current.LastSession)) return;
        try
        {
            if (JsonSerializer.Deserialize<Workspace>(AppSettings.Current.LastSession) is { Tabs.Count: > 0 } workspace)
            {
                await RestoreWorkspaceAsync(workspace);
                StatusMessage = $"Riaperte {workspace.Tabs.Count} schede della sessione precedente.";
            }
        }
        catch { /* sessione non leggibile: si riparte puliti */ }
    }

    [RelayCommand]
    private async Task SaveWorkspaceAsync()
    {
        var path = await _dialogs.PickSaveFileAsync("Salva area di lavoro", "area-di-lavoro.nfx", "nfx", new FileFilter("Area di lavoro Navifox", "*.nfx"));
        if (path is null) return;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(CaptureWorkspace(), WorkspaceJson));
        StatusMessage = $"Area di lavoro salvata: {Path.GetFileName(path)} ({OpenTabs.Count - 1} schede).";
    }

    [RelayCommand]
    private async Task OpenWorkspaceAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Apri area di lavoro", new FileFilter("Area di lavoro Navifox", "*.nfx"), new FileFilter("Tutti i file", "*.*"));
        if (path is null) return;
        try
        {
            if (JsonSerializer.Deserialize<Workspace>(await File.ReadAllTextAsync(path)) is not { } workspace) return;
            await RestoreWorkspaceAsync(workspace);
            StatusMessage = $"Area di lavoro «{Path.GetFileNameWithoutExtension(path)}» aperta ({workspace.Tabs.Count} schede).";
        }
        catch (Exception exception) { await ReportErrorAsync("Impossibile aprire l’area di lavoro", exception); }
    }
}
