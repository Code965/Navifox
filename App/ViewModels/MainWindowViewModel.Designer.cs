using CommunityToolkit.Mvvm.Input;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Progetta tabella / Nuova tabella e menu della categoria «Tabelle» (albero ed elenco oggetti).</summary>
public partial class MainWindowViewModel
{
    /// <summary>Clic su un nodo già selezionato (es. «Tabelle» dopo aver aperto una scheda): ripropone l'elenco degli oggetti.</summary>
    public void ReactivateNode(TreeNodeViewModel node)
    {
        if (node.IsPlaceholder || node != SelectedNode) return;
        if (node.Kind is TreeNodeKind.Category or TreeNodeKind.Database or TreeNodeKind.Schema) _ = ActivateNodeAsync(node);
    }

    private IReadOnlyList<ContextAction> TablesCategoryActions(TreeNodeViewModel node) =>
    [
        new("Nuova tabella", NewTableCommand, node, "plus"),
        ContextAction.Separator,
        new("Import Wizard…", CategoryWizardCommand, new TableOperation(node, "import"), "import"),
        new("Export Wizard…", CategoryWizardCommand, new TableOperation(node, "export"), "export"),
        ContextAction.Separator,
        new("Esegui file SQL…", ExecuteSqlFileCommand, node, "play"),
        new("Trova nel database…", OpenFindCommand, node, "search"),
        ContextAction.Separator,
        new("Nuovo gruppo…", NewTableGroupCommand, node, "folder"),
        new("Copia tutte le tabelle", CopyTablesCommand, node, "copy", node.Objects is { Count: > 0 }),
        new("Incolla", PasteTablesCommand, node, "download", HasClipboard),
        ContextAction.Separator,
        new("Modello", OpenModelCommand, node, "network"),
        new("Aggiorna", RefreshNodeCommand, node, "refresh"),
    ];

    /// <summary>Menu dell'elenco Oggetti: sulla riga quello della tabella, nello spazio vuoto quello della categoria.</summary>
    public IReadOnlyList<ContextAction> GetObjectListActions(ObjectRowViewModel? row)
    {
        if (ObjectsTab.Source is not { } category) return [];
        if (row is not null && category.SelfAndDescendants().FirstOrDefault(child => child.Kind is TreeNodeKind.Table or TreeNodeKind.DbObject or TreeNodeKind.SavedQuery or TreeNodeKind.BackupFile && child.Name == row.Name) is { } item)
            return GetContextActions(item);
        return category.Category == "Tabelle" ? TablesCategoryActions(category) : GetContextActions(category);
    }

    private TreeNodeViewModel? SelectedObjectNode()
    {
        if (ObjectsTab.Source is not { } category || ObjectsTab.SelectedObject is not { } row) return null;
        return category.SelfAndDescendants().FirstOrDefault(child => child.Kind is TreeNodeKind.Table or TreeNodeKind.DbObject or TreeNodeKind.SavedQuery or TreeNodeKind.BackupFile && child.Name == row.Name);
    }

    [RelayCommand]
    private void DesignTable(TreeNodeViewModel? node)
    {
        node ??= SelectedTab == ObjectsTab ? SelectedObjectNode() : SelectedNode;
        if (node is not { Kind: TreeNodeKind.Table, Profile: { } profile, Database: { } database, Schema: { } schema })
        {
            StatusMessage = "Seleziona la tabella da progettare.";
            return;
        }
        if (IsViewNode(node))
        {
            OpenSchemaObjectEditor(node);
            return;
        }
        OpenDesigner(profile, database, schema, node.Name);
    }

    [RelayCommand]
    private void NewTable(TreeNodeViewModel? node)
    {
        if (CurrentDatabaseContext(node ?? (SelectedTab == ObjectsTab ? ObjectsTab.Source : null)) is not { } context)
        {
            StatusMessage = "Seleziona un database (o la voce «Tabelle») in cui creare la tabella.";
            return;
        }
        OpenDesigner(context.Profile, context.Database, context.Schema, null);
    }

    [RelayCommand]
    private async Task DeleteSelectedObjectsAsync()
    {
        if (ObjectsTab.Source is not { } category) return;
        var rows = SelectedObjectRows.Count > 0 ? SelectedObjectRows : ObjectsTab.SelectedObject is { } one ? [one] : [];
        var nodes = rows.Select(r => category.SelfAndDescendants().FirstOrDefault(c => c.Kind == TreeNodeKind.Table && c.Name == r.Name)).OfType<TreeNodeViewModel>().ToArray();
        if (nodes.Length == 0)
        {
            StatusMessage = "Seleziona una o più tabelle da eliminare.";
            return;
        }
        if (nodes.Length == 1)
        {
            await TableOperationAsync(new TableOperation(nodes[0], "drop"));
            return;
        }
        var view = category.Category == "Viste";
        if (!await _dialogs.ConfirmAsync(view ? "Elimina viste" : "Elimina tabelle",
                $"Eliminare definitivamente {nodes.Length} {(view ? "viste" : "tabelle")} da «{category.Database}»?\n\n{string.Join("\n", nodes.Take(15).Select(n => "  • " + n.Name))}{(nodes.Length > 15 ? "\n  …" : string.Empty)}\n\nL’operazione non può essere annullata.",
                "Elimina", danger: true)) return;
        var errors = new List<string>();
        foreach (var node in nodes)
        {
            try
            {
                await RunTableSqlAsync(node, $"DROP {(view ? "VIEW" : "TABLE")} {TableName(node)}");
                CloseTabsForTable(node.Profile!.Id, node.Database!, node.Name);
            }
            catch (Exception exception) { errors.Add($"{node.Name}: {exception.Message}"); }
        }
        await RefreshTablesOfAsync(category.Profile!, category.Database!, category.Schema!);
        if (errors.Count > 0) await _dialogs.ShowMessageAsync("Eliminazione non completata", string.Join("\n", errors), isError: true);
        else StatusMessage = $"{nodes.Length} oggetti eliminati.";
    }

    /// <summary>Import/Export Wizard dal menu della categoria: usa la tabella selezionata nell'elenco Oggetti.</summary>
    [RelayCommand]
    private async Task CategoryWizardAsync(TableOperation? operation)
    {
        if (operation is null) return;
        var row = ObjectsTab.Source == operation.Node ? ObjectsTab.SelectedObject : null;
        if (row is null)
        {
            await ShowCategoryAsync(operation.Node);
            await _dialogs.ShowMessageAsync(operation.Kind == "import" ? "Import Wizard" : "Export Wizard",
                $"Seleziona nell’elenco la tabella {(operation.Kind == "import" ? "in cui importare" : "da esportare")}, poi scegli di nuovo {(operation.Kind == "import" ? "Import" : "Export")} Wizard (anche dal tasto destro sulla tabella).");
            return;
        }
        await RunObjectWizardAsync(row, operation.Kind);
    }

    private void OpenDesigner(ConnectionProfile profile, string database, string schema, string? table)
    {
        if (table is not null && OpenTabs.OfType<TableDesignerViewModel>().FirstOrDefault(t =>
                t.Profile.Id == profile.Id && t.Database == database && t.Schema == schema && t.TableName == table) is { } existing)
        {
            SelectedTab = existing;
            return;
        }
        var designer = new TableDesignerViewModel(_databaseAccess, _dialogs, profile, database, schema, table, OnDesignerSavedAsync);
        OpenTabs.Add(designer);
        SelectedTab = designer;
        _ = designer.LoadAsync();
    }

    private async Task OnDesignerSavedAsync(TableDesignerViewModel designer)
    {
        StatusMessage = designer.StatusMessage;
        await RefreshTablesOfAsync(designer.Profile, designer.Database, designer.Schema);
        // Le schede dati aperte sulla stessa tabella si ricaricano con la nuova struttura.
        foreach (var tab in OpenTabs.OfType<TableTabViewModel>().Where(t => t.ProfileId == designer.Profile.Id && t.Database == designer.Database && t.TableName == designer.TableName).ToArray())
            await tab.LoadAsync(CancellationToken.None);
        if (Info is not null) UpdateInfoTarget();
        foreach (var model in OpenTabs.OfType<ModelTabViewModel>().Where(m => m.TabConnection?.Id == designer.Profile.Id && m.Database == designer.Database && m.Schema == designer.Schema).ToArray())
        {
            model.SaveLayout();
            await model.LoadAsync();
        }
    }
}
