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
        {
            var actions = GetContextActions(item).ToList();
            if (category.IsTableCategory && SelectedObjectRows.Count > 1)
                for (var i = 0; i < actions.Count; i++)
                    if (actions[i].Parameter is TableOperation { Kind: "drop" })
                        actions[i] = new($"Elimina {SelectedObjectRows.Count} oggetti selezionati…", DeleteSelectedObjectsCommand, Glyph: "trash");
            return actions;
        }
        var categoryActions = (category.Category == "Tabelle" ? TablesCategoryActions(category) : GetContextActions(category)).ToList();
        if (category.IsTableCategory && SelectedObjectRows.Count > 0)
            categoryActions.Insert(0, new($"Elimina {SelectedObjectRows.Count} oggetti selezionati…", DeleteSelectedObjectsCommand, Glyph: "trash"));
        return categoryActions;
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
        if (!category.IsTableCategory || IsBusy || category.Profile is not { } profile || category.Database is not { } database || category.Schema is not { } schema) return;
        var rows = (SelectedObjectRows.Count > 0 ? SelectedObjectRows : ObjectsTab.SelectedObject is { } one ? [one] : []).Where(r => ObjectsTab.VisibleObjects.Contains(r)).ToArray();
        if (rows.Length == 0)
        {
            StatusMessage = "Seleziona una o più tabelle da eliminare.";
            return;
        }
        IsBusy = true;
        try
        {
            var result = await new Services.TableDeletionService(_databaseAccess, _dialogs).DeleteAsync(profile, database, schema, rows.Select(r => r.Name).ToArray(), category.Category == "Viste", CancellationToken.None);
            if (!result.Confirmed) return;
            foreach (var name in result.Deleted) CloseTabsForTable(profile.Id, database, name, schema);
            SelectedObjectRows = [];
            await RefreshTablesOfAsync(profile, database, schema);
            StatusMessage = $"{result.Deleted.Count} di {rows.Length} oggetti eliminati.";
            if (result.Error is { } error) await _dialogs.ShowMessageAsync("Eliminazione non completata", StatusMessage + "\n\n" + error, isError: true);
        }
        catch (Exception error) { await ReportErrorAsync("Eliminazione non riuscita", error); }
        finally { IsBusy = false; }
    }

    /// <summary>Import/Export Wizard dal menu della categoria: usa la tabella selezionata nell'elenco Oggetti.</summary>
    [RelayCommand]
    private async Task CategoryWizardAsync(TableOperation? operation)
    {
        if (operation is null) return;
        if (operation.Kind == "import") { await OpenImportWizardAsync(operation.Node); return; }
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
