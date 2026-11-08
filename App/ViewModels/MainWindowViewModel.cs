using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly string[] MySqlCategories = ["Tabelle", "Viste", "Funzioni", "Trigger", "Eventi", "Query salvate", "Backup"];
    private static readonly string[] PostgresCategories = ["Tabelle", "Viste", "Funzioni", "Trigger", "Sequenze", "Query salvate", "Backup"];

    /// <summary>Categorie dell'albero: tabelle e viste, gli oggetti gestiti dal dialetto, query salvate e backup.</summary>
    private IReadOnlyList<string> CategoriesFor(ConnectionProfile profile) => profile.Kind switch
    {
        DatabaseKind.MySql => MySqlCategories,
        DatabaseKind.PostgreSql => PostgresCategories,
        _ => ["Tabelle", "Viste", .. _databaseAccess.Provider(profile).SchemaObjectKinds.Select(kind => kind switch
            {
                SchemaObjectKind.Routine => "Funzioni",
                SchemaObjectKind.Trigger => "Trigger",
                SchemaObjectKind.Event => "Eventi",
                _ => "Sequenze",
            }), "Query salvate", "Backup"],
    };

    private readonly IConnectionProfileStore _profileStore;
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly ConnectionDialogService _connectionDialog;
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogs;
    private readonly AutomationService _automation;
    private readonly HashSet<Guid> _connectedProfiles = [];
    private readonly Dictionary<Guid, TreeNodeViewModel> _connectionNodes = [];
    private CancellationTokenSource _treeOperations = new();
    private CancellationTokenSource? _activeOperation;
    private int _pendingTreeLoads;

    public MainWindowViewModel(IConnectionProfileStore profileStore, IDatabaseAccessService databaseAccess, ConnectionDialogService connectionDialog, IThemeService themeService, IDialogService dialogs, AutomationService automation)
    {
        _dialogs = dialogs;
        _automation = automation;
        _profileStore = profileStore;
        _databaseAccess = databaseAccess;
        _connectionDialog = connectionDialog;
        _themeService = themeService;
        ObjectsTab = new ObjectsTabViewModel(OpenObjectAsync, RefreshObjectsAsync, RunObjectWizardAsync, () => OpenImportWizardAsync(null));
        OpenTabs.Add(ObjectsTab);
        SelectedTab = ObjectsTab;
        Info = new InfoPanelViewModel(databaseAccess);
        LibraryStore.QueriesChanged += OnSavedQueriesChanged;
        ObjectsTab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ObjectsTabViewModel.SelectedObject)) UpdateInfoTarget();
        };
    }

    /// <summary>Pannello informazioni a destra (proprietà e DDL).</summary>
    public InfoPanelViewModel Info { get; } = null!;
    [ObservableProperty] private bool _isTreeVisible = true;
    [ObservableProperty] private bool _isInfoVisible = true;

    [RelayCommand]
    private void ToggleTree() => IsTreeVisible = !IsTreeVisible;

    [RelayCommand]
    private void ToggleInfo() => IsInfoVisible = !IsInfoVisible;

    partial void OnSelectedTabChanged(WorkspaceTabViewModel? value) => UpdateInfoTarget();

    /// <summary>Sceglie cosa mostrare nel pannello informazioni: tabella aperta, oggetto selezionato o nodo dell albero.</summary>
    private void UpdateInfoTarget()
    {
        if (Info is null) return;
        InfoTarget? target = null;
        if (SelectedTab is TableTabViewModel table && Profiles.FirstOrDefault(p => p.Id == table.ProfileId) is { } tableProfile)
            target = new InfoTarget(tableProfile, table.IsView ? InfoKind.View : InfoKind.Table, table.Database, table.Schema, table.TableName);
        else if (SelectedTab == ObjectsTab && ObjectsTab.SelectedObject is { } row && ObjectsTab.Source is { Profile: { } profile } source)
            target = new InfoTarget(profile, row.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? InfoKind.View : InfoKind.Table, source.Database, source.Schema, row.Name);
        else if (SelectedNode is { Profile: { } nodeProfile } node && !node.IsPlaceholder)
        {
            target = node.Kind switch
            {
                TreeNodeKind.Connection => new InfoTarget(nodeProfile, InfoKind.Connection, IsConnected: node.IsLoaded),
                TreeNodeKind.Database when !nodeProfile.Kind.HasSchemas() => new InfoTarget(nodeProfile, InfoKind.Database, node.Database, ObjectCount: FindTablesCategory(node)?.Objects?.Count),
                TreeNodeKind.Database => new InfoTarget(nodeProfile, InfoKind.Database, node.Database),
                TreeNodeKind.Schema => new InfoTarget(nodeProfile, InfoKind.Schema, node.Database, node.Schema, ObjectCount: FindTablesCategory(node)?.Objects?.Count),
                TreeNodeKind.Category when node.Database == node.Schema => new InfoTarget(nodeProfile, InfoKind.Database, node.Database, ObjectCount: node.Parent is { } db ? FindTablesCategory(db)?.Objects?.Count : null),
                TreeNodeKind.Category => new InfoTarget(nodeProfile, InfoKind.Schema, node.Database, node.Schema, ObjectCount: node.Parent is { } sc ? FindTablesCategory(sc)?.Objects?.Count : null),
                TreeNodeKind.Table => new InfoTarget(nodeProfile, node.Category == "Viste" ? InfoKind.View : InfoKind.Table, node.Database, node.Schema, node.Name),
                _ => null
            };
        }
        _ = Info.ShowAsync(target);
    }

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = [];
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public ObservableCollection<WorkspaceTabViewModel> OpenTabs { get; } = [];
    public ObjectsTabViewModel ObjectsTab { get; }

    [ObservableProperty] private TreeNodeViewModel? _selectedNode;
    [ObservableProperty] private WorkspaceTabViewModel? _selectedTab;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _statusMessage = "Pronto · aggiungi una connessione per iniziare";
    [ObservableProperty] private string _activeContext = "Nessuna connessione";
    [ObservableProperty] private string _elapsedTime = "—";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTablesActive), nameof(IsViewsActive), nameof(IsFunctionsActive), nameof(IsEventsActive), nameof(IsTriggersActive),
        nameof(IsSequencesActive), nameof(IsUsersActive), nameof(IsQueriesActive), nameof(IsBackupActive))]
    private string _activeCategory = "Tabelle";

    public bool IsTablesActive => ActiveCategory == "Tabelle";
    public bool IsViewsActive => ActiveCategory == "Viste";
    public bool IsFunctionsActive => ActiveCategory == "Funzioni";
    public bool IsEventsActive => ActiveCategory == "Eventi";
    public bool IsTriggersActive => ActiveCategory == "Trigger";
    public bool IsSequencesActive => ActiveCategory == "Sequenze";
    public bool IsUsersActive => ActiveCategory == "Utenti";
    public bool IsQueriesActive => ActiveCategory == "Query salvate";
    public bool IsBackupActive => ActiveCategory == "Backup";

    public int ConnectionCount => _connectedProfiles.Count;
    public bool ShowWelcome => Profiles.Count == 0;
    public bool HasTreeFilter => SearchText.Trim().Length > 0;

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (value is null || value.IsPlaceholder) return;
        UpdateInfoTarget();
        _ = ActivateAndRefreshInfoAsync(value);
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyTreeFilter();
        OnPropertyChanged(nameof(HasTreeFilter));
    }

    public async Task InitializeAsync()
    {
        try
        {
            var saved = await _profileStore.LoadAsync();
            Profiles.Clear();
            foreach (var profile in saved) Profiles.Add(profile);
            RebuildConnectionTree();
            RefreshStartPage();
            await RestoreSessionAsync();
            await ProcessPendingOpenAsync();
            StatusMessage = Profiles.Count == 0 ? "Nessuna connessione salvata · crea la tua prima connessione" : $"{Profiles.Count} connessioni salvate";
        }
        catch (Exception exception) { StatusMessage = $"Impossibile leggere le connessioni salvate: {exception.Message}"; }
    }

    // ───────────────────────── Comandi ─────────────────────────

    [RelayCommand]
    private async Task AddConnectionAsync()
    {
        var profile = await _connectionDialog.ShowAsync(null, AllFolders(), CurrentFolder());
        if (profile is null) return;
        Profiles.Add(profile);
        RebuildConnectionTree();
        StatusMessage = $"Connessione «{profile.Name}» aggiunta.";
        await SaveProfilesAsync();
    }

    [RelayCommand]
    private async Task ConnectSelectedAsync()
    {
        var node = SelectedNode;
        var connection = node is null ? null : ConnectionOf(node);
        if (connection is null)
        {
            StatusMessage = "Seleziona prima una connessione.";
            return;
        }
        if (connection.IsLoaded) connection.Reset();
        connection.IsExpanded = true;
        await EnsureLoadedAsync(connection);
    }

    [RelayCommand]
    private void DisconnectSelected()
    {
        var connection = SelectedNode is null ? null : ConnectionOf(SelectedNode);
        if (connection?.Profile is null) return;
        connection.Reset();
        _connectedProfiles.Remove(connection.Profile.Id);
        OnPropertyChanged(nameof(ConnectionCount));
        if (ObjectsTab.Source is { } source && ConnectionOf(source) == connection) ObjectsTab.Clear();
        StatusMessage = $"Disconnesso da «{connection.Name}».";
    }

    [RelayCommand]
    private async Task RefreshObjectsAsync()
    {
        var category = ObjectsTab.Source;
        if (category is null) return;
        var expanded = category.IsExpanded;
        category.Reset();
        category.IsExpanded = expanded;
        await EnsureLoadedAsync(category);
        ShowCategoryObjects(category, ObjectsTab.SelectedObject?.Name);
    }

    /// <summary>Doppio clic su un nodo dell'albero: apre la tabella.</summary>
    [RelayCommand]
    private async Task OpenNodeAsync(TreeNodeViewModel? node)
    {
        if (node is null || node.IsPlaceholder) return;
        // L'espansione con doppio clic è gestita dal TreeViewItem stesso; qui si aprono solo le tabelle.
        if (node.Kind == TreeNodeKind.Table) await OpenTableAsync(node.Profile!, node.Database!, node.Schema!, node.Name);
        else if (node.Kind == TreeNodeKind.DbObject) OpenSchemaObjectEditor(node);
        else if (node.Kind is TreeNodeKind.SavedQuery or TreeNodeKind.BackupFile) await OpenLibraryItemAsync(node);
    }

    [RelayCommand]
    private void CloseTab(WorkspaceTabViewModel? tab)
    {
        if (tab is null || !tab.CanClose) return;
        var index = OpenTabs.IndexOf(tab);
        _ = tab.CloseAsync();
        OpenTabs.Remove(tab);
        if (SelectedTab == tab || SelectedTab is null) SelectedTab = OpenTabs[Math.Clamp(index - 1, 0, OpenTabs.Count - 1)];
    }

    [RelayCommand]
    private async Task DeleteSelectedConnectionAsync()
    {
        var node = SelectedNode is null ? null : ConnectionOf(SelectedNode);
        if (node?.Profile is null) return;
        if (!await _dialogs.ConfirmAsync("Elimina connessione",
                $"Eliminare la connessione «{node.Profile.Name}» ({node.Profile.Host})?\n\nViene tolta solo da Navifox: i database sul server non vengono toccati.", "Elimina", danger: true))
            return;
        foreach (var tab in OpenTabs.Where(t => t.CanClose && TabProfileId(t) == node.Profile.Id).ToArray()) CloseTab(tab);
        var profile = Profiles.FirstOrDefault(item => item.Id == node.Profile.Id);
        if (profile is not null) Profiles.Remove(profile);
        _connectedProfiles.Remove(node.Profile.Id);
        _connectionNodes.Remove(node.Profile.Id);
        if (ObjectsTab.Source is { } source && ConnectionOf(source) == node) ObjectsTab.Clear();
        RebuildConnectionTree();
        await SaveProfilesAsync();
        OnPropertyChanged(nameof(ConnectionCount));
        StatusMessage = "Connessione rimossa.";
    }

    [RelayCommand]
    private void ClearTreeFilter() => SearchText = string.Empty;

    [RelayCommand]
    private void ToggleTheme()
    {
        _themeService.Toggle();
        IsDarkTheme = _themeService.IsDark;
        // La scelta resta anche alla prossima apertura (prima cambiava solo i colori in memoria).
        AppSettings.Current.Theme = IsDarkTheme ? "Scuro" : "Chiaro";
        AppSettings.Current.Save();
    }

    [RelayCommand]
    private void Exit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.Shutdown();
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _activeOperation?.Cancel();
        _treeOperations.Cancel();
        _treeOperations.Dispose();
        _treeOperations = new CancellationTokenSource();
    }

    /// <summary>Pulsanti della toolbar (Tabelle, Viste, …): mostra quella categoria del database corrente.</summary>
    [RelayCommand]
    private async Task ShowCategoryAsync(string? category)
    {
        if (string.IsNullOrEmpty(category)) return;
        ActiveCategory = category;
        SelectedTab = ObjectsTab;
        var container = SelectedNode;
        while (container is not null && container.Kind is not (TreeNodeKind.Database or TreeNodeKind.Schema)) container = container.Parent;
        if (container is null)
        {
            StatusMessage = "Seleziona prima un database nell’albero.";
            return;
        }
        await EnsureLoadedAsync(container);
        // PostgreSQL: dal database si scende nel primo schema (di solito «public»).
        if (container.Kind == TreeNodeKind.Database && container.Children.FirstOrDefault(child => child.Kind == TreeNodeKind.Schema) is { } schema)
        {
            container = container.Children.FirstOrDefault(child => child.Name == container.Profile!.Kind.DefaultSchema(container.Name)) ?? schema;
            await EnsureLoadedAsync(container);
        }
        var node = container.Children.FirstOrDefault(child => child.Kind == TreeNodeKind.Category && child.Category == category);
        if (node is not null)
        {
            container.IsExpanded = true;
            SelectedNode = node;
            return;
        }
        ObjectsTab.Clear();
        StatusMessage = $"L’elenco «{category}» è previsto in una fase successiva.";
    }

    [RelayCommand]
    private async Task ShowObjectsForSelectedNodeAsync()
    {
        SelectedTab = ObjectsTab;
        if (SelectedNode is { } node) await ActivateNodeAsync(node);
    }

    // ───────────────────────── Albero ─────────────────────────

    /// <summary>Selezione di un nodo: carica ciò che serve e aggiorna la scheda Oggetti.</summary>
    private async Task ActivateNodeAsync(TreeNodeViewModel node)
    {
        UpdateActiveContext(node);
        switch (node.Kind)
        {
            case TreeNodeKind.Connection:
                await EnsureLoadedAsync(node);
                node.IsExpanded = node.IsLoaded;
                break;
            case TreeNodeKind.Database:
            case TreeNodeKind.Schema:
                await EnsureLoadedAsync(node);
                node.IsExpanded = node.IsLoaded;
                if (FindTablesCategory(node) is { } tables)
                {
                    ActiveCategory = "Tabelle";
                    await ShowCategoryAsync(tables);
                }
                break;
            case TreeNodeKind.Category when node.IsObjectCategory:
                ActiveCategory = node.Category!;
                await ShowCategoryAsync(node);
                break;
            case TreeNodeKind.Category:
                ActiveCategory = node.Category!;
                SelectedTab = ObjectsTab;
                ObjectsTab.Clear();
                StatusMessage = $"L’elenco «{node.Name}» è previsto in una fase successiva.";
                break;
            case TreeNodeKind.TableGroup when node.CategoryNode is { } groupCategory:
                if (ObjectsTab.Source != groupCategory) ShowCategoryObjects(groupCategory);
                break;
            case TreeNodeKind.Table or TreeNodeKind.DbObject or TreeNodeKind.SavedQuery or TreeNodeKind.BackupFile when node.CategoryNode is { } category:
                if (ObjectsTab.Source != category) ShowCategoryObjects(category, node.Name);
                else ObjectsTab.SelectedObject = ObjectsTab.VisibleObjects.FirstOrDefault(row => row.Name == node.Name);
                break;
        }
    }

    private async Task ActivateAndRefreshInfoAsync(TreeNodeViewModel node)
    {
        await ActivateNodeAsync(node);
        if (SelectedNode == node) UpdateInfoTarget();
    }

    private async Task ShowCategoryAsync(TreeNodeViewModel category)
    {
        SelectedTab = ObjectsTab;
        ObjectsTab.IsLoading = !category.IsLoaded;
        await EnsureLoadedAsync(category);
        ObjectsTab.IsLoading = false;
        // Se nel frattempo l'utente ha aperto un'altra scheda (query, tabella…) non lo si riporta su «Oggetti».
        if (category.IsLoaded) ShowCategoryObjects(category, bringToFront: SelectedTab == ObjectsTab);
    }

    private void ShowCategoryObjects(TreeNodeViewModel category, string? selectName = null, bool bringToFront = true)
    {
        SelectedObjectRows = [];
        if (bringToFront) SelectedTab = ObjectsTab;
        // Visualizza › Appiattisci elenco oggetti: tabelle e viste dello stesso database/schema in un unico elenco.
        var objects = IsFlatObjectList && category.IsTableCategory && category.Parent is { } parent
            ? parent.Children.Where(child => child.IsTableCategory).SelectMany(child => child.Objects ?? [])
            : category.Objects ?? [];
        ObjectsTab.Show(category, objects.Select(info => new ObjectRowViewModel(info)), selectName);
        StatusMessage = $"{category.Objects?.Count ?? 0} oggetti in {category.Database}";
    }

    private void OnNodeExpandRequested(TreeNodeViewModel node) => _ = EnsureLoadedAsync(node);

    /// <summary>Carica pigramente i figli di un nodo (connessione, database, schema, categoria).</summary>
    private async Task EnsureLoadedAsync(TreeNodeViewModel node)
    {
        if (node.IsLoaded || node.Profile is null) return;
        if (node.IsLoading)
        {
            while (node.IsLoading) await Task.Delay(50);
            return;
        }
        var profile = node.Profile;
        var token = _treeOperations.Token;
        node.IsLoading = true;
        BeginTreeLoad();
        var timer = Stopwatch.StartNew();
        try
        {
            switch (node.Kind)
            {
                case TreeNodeKind.Connection:
                {
                    var databases = await _databaseAccess.ListDatabasesAsync(profile, token);
                    node.SetChildren(databases.Select(database => new TreeNodeViewModel(database, "database", TreeNodeKind.Database, profile, database)));
                    _connectedProfiles.Add(profile.Id);
                    RememberConnection(profile);
                    OnPropertyChanged(nameof(ConnectionCount));
                    StatusMessage = $"Connesso a {profile.Endpoint} · {databases.Count} database";
                    break;
                }
                case TreeNodeKind.Database when !profile.Kind.HasSchemas():
                    // MySQL/MariaDB (database = schema) e SQLite (schema «main»): si va direttamente alle categorie.
                    node.SetChildren(CreateCategories(profile, node.Database!, profile.Kind.DefaultSchema(node.Database!)));
                    break;
                case TreeNodeKind.Database:
                {
                    var schemas = await _databaseAccess.ListSchemasAsync(profile, node.Database!, token);
                    node.SetChildren(schemas.Select(schema => new TreeNodeViewModel(schema, "layers", TreeNodeKind.Schema, profile, node.Database, schema)));
                    StatusMessage = $"{schemas.Count} schemi in {node.Database}";
                    break;
                }
                case TreeNodeKind.Schema:
                    node.SetChildren(CreateCategories(profile, node.Database!, node.Schema!));
                    break;
                case TreeNodeKind.Category when node.SchemaKind is { } schemaKind:
                {
                    var items = await _databaseAccess.ListSchemaObjectsAsync(profile, node.Database!, node.Schema!, schemaKind, token);
                    node.Objects = items;
                    node.SetChildren(items.Select(info => new TreeNodeViewModel(info.Name, ObjectRowViewModel.GlyphFor(info.Kind), TreeNodeKind.DbObject, profile, node.Database, node.Schema, node.Category) { Info = info }));
                    break;
                }
                case TreeNodeKind.Category when node.IsLocalCategory:
                    LoadLocalCategory(node);
                    break;
                case TreeNodeKind.Category when node.IsObjectCategory:
                {
                    var all = await _databaseAccess.ListTablesAsync(profile, node.Database!, node.Schema!, token);
                    var wanted = node.Category == "Viste" ? "VIEW" : "TABLE";
                    var objects = all.Where(item => item.Kind.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                                    && (wanted == "VIEW" || !item.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase))).ToArray();
                    node.Objects = objects;
                    var glyph = wanted == "VIEW" ? "eye" : "table";
                    node.SetChildren(objects.Select(info => new TreeNodeViewModel(info.Name, glyph, TreeNodeKind.Table, profile, node.Database, node.Schema, node.Category) { Info = info }));
                    if (node.Category == "Tabelle") ApplyTableGroups(node);
                    break;
                }
            }
            ElapsedTime = $"{timer.Elapsed.TotalSeconds:0.00} s";
            if (HasTreeFilter) ApplyTreeFilter();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            node.Reset();
            StatusMessage = "Operazione annullata.";
        }
        catch (Exception exception)
        {
            node.Reset();
            StatusMessage = $"Operazione non riuscita su «{node.Name}»: {exception.Message}";
        }
        finally
        {
            node.IsLoading = false;
            EndTreeLoad();
        }
    }

    private IEnumerable<TreeNodeViewModel> CreateCategories(ConnectionProfile profile, string database, string schema) =>
        CategoriesFor(profile)
        .Select(category => new TreeNodeViewModel(category, CategoryGlyph(category), TreeNodeKind.Category, profile, database, schema, category));

    private static TreeNodeViewModel? FindTablesCategory(TreeNodeViewModel node) =>
        node.Children.FirstOrDefault(child => child.Kind == TreeNodeKind.Category && child.Category == "Tabelle");

    private static TreeNodeViewModel? ConnectionOf(TreeNodeViewModel node)
    {
        for (var current = node; current is not null; current = current.Parent)
            if (current.Kind == TreeNodeKind.Connection) return current;
        return null;
    }

    private void BeginTreeLoad()
    {
        _pendingTreeLoads++;
        IsBusy = true;
    }

    private void EndTreeLoad()
    {
        _pendingTreeLoads = Math.Max(0, _pendingTreeLoads - 1);
        IsBusy = _pendingTreeLoads > 0 || _activeOperation is not null;
    }

    private void UpdateActiveContext(TreeNodeViewModel node)
    {
        if (node.Profile is null) return;
        ActiveContext = node.Database is null
            ? $"{node.Profile.Name} · server"
            : node.Schema is { } schema && schema != node.Database
                ? $"{node.Profile.Name} · {node.Database} · {schema}"
                : $"{node.Profile.Name} · {node.Database}";
    }

    /// <summary>Ricostruisce la radice (cartelle + connessioni) riusando i nodi già caricati.</summary>
    private void RebuildConnectionTree()
    {
        var roots = BuildFolderTree();

        RootNodes.Clear();
        foreach (var root in roots) RootNodes.Add(root);
        ApplyTreeFilter();
        OnPropertyChanged(nameof(ShowWelcome));
    }


    private TreeNodeViewModel ConnectionNode(ConnectionProfile profile)
    {
        if (_connectionNodes.TryGetValue(profile.Id, out var existing)) return existing;
        var node = new TreeNodeViewModel(profile.Name, "server", TreeNodeKind.Connection, profile)
        {
            ExpandHandler = OnNodeExpandRequested
        };
        _connectionNodes[profile.Id] = node;
        return node;
    }

    /// <summary>
    /// Filtro dell'albero: un nodo resta visibile se il suo nome (o l'host per le connessioni) contiene il testo,
    /// se contiene discendenti corrispondenti, oppure se un antenato corrisponde. Cerca su connessioni, database,
    /// schemi e tabelle già caricati; i rami con risultati vengono espansi.
    /// </summary>
    private void ApplyTreeFilter()
    {
        var query = SearchText.Trim();
        foreach (var root in RootNodes) FilterNode(root, query, ancestorMatched: false);
    }

    private static bool FilterNode(TreeNodeViewModel node, string query, bool ancestorMatched)
    {
        if (node.IsPlaceholder)
        {
            node.IsVisible = true;
            return false;
        }
        if (query.Length == 0)
        {
            foreach (var child in node.Children) FilterNode(child, query, false);
            node.IsVisible = true;
            return true;
        }
        var self = node.Kind != TreeNodeKind.Category && node.Kind != TreeNodeKind.Group
                   && (node.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                       || (node.Kind == TreeNodeKind.Connection && node.Profile?.Host.Contains(query, StringComparison.OrdinalIgnoreCase) == true));
        var anyDescendant = false;
        foreach (var child in node.Children) anyDescendant |= FilterNode(child, query, ancestorMatched || self);
        node.IsVisible = self || anyDescendant || ancestorMatched;
        if (anyDescendant && node.IsLoaded) node.IsExpanded = true;
        return self || anyDescendant;
    }

    // ───────────────────────── Schede ─────────────────────────

    private Task OpenObjectAsync(ObjectRowViewModel row)
    {
        var source = ObjectsTab.Source;
        if (source?.Profile is null || source.Database is null || source.Schema is null)
        {
            StatusMessage = "Seleziona una tabella nell’elenco degli oggetti.";
            return Task.CompletedTask;
        }
        if (source.SchemaKind is not null)
        {
            if (source.Children.FirstOrDefault(child => child.Name == row.Name) is { } node) OpenSchemaObjectEditor(node);
            return Task.CompletedTask;
        }
        if (source.IsLocalCategory)
            return source.Children.FirstOrDefault(child => child.Name == row.Name) is { } item ? OpenLibraryItemAsync(item) : Task.CompletedTask;
        return OpenTableAsync(source.Profile, source.Database, source.Schema, row.Name);
    }

    /// <summary>Import/Export Wizard dalla scheda Oggetti: apre la tabella e avvia la procedura.</summary>
    private async Task RunObjectWizardAsync(ObjectRowViewModel row, string kind)
    {
        await OpenObjectAsync(row);
        if (SelectedTab is not TableTabViewModel tab || tab.TableName != row.Name) return;
        if (kind == "import") await tab.ImportCommand.ExecuteAsync(null);
        else await tab.ExportCommand.ExecuteAsync(null);
    }

    private async Task OpenTableAsync(ConnectionProfile profile, string database, string schema, string table)
    {
        RememberTable(profile, database, schema, table);
        var existing = OpenTabs.OfType<TableTabViewModel>().FirstOrDefault(tab =>
            tab.ProfileId == profile.Id && tab.Database == database && tab.Schema == schema && tab.TableName == table);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }
        var isView = FindObjectKind(profile.Id, database, schema, table)?.Contains("VIEW", StringComparison.OrdinalIgnoreCase) == true;
        var tab = new TableTabViewModel(_databaseAccess, _dialogs, profile, database, schema, table, isView);
        tab.OpenImportWizard = path => OpenImportWizardAsync(new TreeNodeViewModel(table, "table", TreeNodeKind.Table, profile, database, schema, isView ? "Viste" : "Tabelle"), path);
        OpenTabs.Add(tab);
        SelectedTab = tab;
        await RunOperationAsync(async token =>
        {
            await tab.LoadAsync(token);
            StatusMessage = tab.StatusMessage;
        }, CancellationToken.None);
    }

    /// <summary>Tipo (TABLE/VIEW) dell'oggetto, se già caricato nell'albero.</summary>
    private string? FindObjectKind(Guid profileId, string database, string schema, string table) =>
        _connectionNodes.TryGetValue(profileId, out var connection)
            ? connection.SelfAndDescendants()
                .Where(node => node.IsTableCategory && node.Database == database && node.Schema == schema)
                .SelectMany(node => node.Objects ?? [])
                .FirstOrDefault(info => info.Name == table)?.Kind
            : null;

    private async Task RunOperationAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        _activeOperation?.Cancel();
        _activeOperation?.Dispose();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeOperation = linked;
        IsBusy = true;
        var timer = Stopwatch.StartNew();
        try { await action(linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { StatusMessage = "Operazione annullata."; }
        catch (Exception exception) { StatusMessage = $"Operazione non riuscita: {exception.Message}"; }
        finally
        {
            timer.Stop();
            ElapsedTime = $"{timer.Elapsed.TotalSeconds:0.00} s";
            if (ReferenceEquals(_activeOperation, linked)) _activeOperation = null;
            IsBusy = _pendingTreeLoads > 0 || _activeOperation is not null;
        }
    }

    private async Task SaveProfilesAsync()
    {
        try { await _profileStore.SaveAsync(Profiles.ToArray()); }
        catch (Exception exception) { StatusMessage = $"Impossibile salvare le connessioni: {exception.Message}"; }
    }

    private static string CategoryGlyph(string category) => category switch
    {
        "Tabelle" => "table", "Viste" => "eye", "Funzioni" => "function", "Eventi" => "clock", "Trigger" => "zap", "Sequenze" => "hash", "Query salvate" => "bookmark", _ => "archive"
    };
}
