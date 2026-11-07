using CommunityToolkit.Mvvm.Input;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public sealed record NewSchemaObjectRequest(TreeNodeViewModel Node, SchemaObjectKind Kind, string RoutineType = "FUNCTION");

/// <summary>Funzioni, procedure, trigger, eventi, sequenze e viste: menu, editor, nuova, elimina, esegui.</summary>
public partial class MainWindowViewModel
{
    private static string KindName(SchemaObjectKind kind, DatabaseObjectInfo? item = null) => kind switch
    {
        SchemaObjectKind.Routine => item?.Kind == "PROCEDURE" ? "procedura" : "funzione",
        SchemaObjectKind.Trigger => "trigger",
        SchemaObjectKind.Event => "evento",
        SchemaObjectKind.Sequence => "sequenza",
        _ => "vista"
    };

    private IReadOnlyList<ContextAction> SchemaCategoryActions(TreeNodeViewModel node)
    {
        var kind = node.SchemaKind!.Value;
        var actions = new List<ContextAction>();
        if (kind == SchemaObjectKind.Routine)
        {
            actions.Add(new("Nuova funzione", NewSchemaObjectCommand, new NewSchemaObjectRequest(node, kind), "plus"));
            actions.Add(new("Nuova procedura", NewSchemaObjectCommand, new NewSchemaObjectRequest(node, kind, "PROCEDURE"), "plus"));
        }
        else actions.Add(new($"Nuov{(kind is SchemaObjectKind.Sequence ? "a sequenza" : kind is SchemaObjectKind.Event ? "o evento" : "o trigger")}", NewSchemaObjectCommand, new NewSchemaObjectRequest(node, kind), "plus"));
        actions.Add(ContextAction.Separator);
        actions.Add(new("Esegui file SQL…", ExecuteSqlFileCommand, node, "play"));
        actions.Add(new("Trova nel database…", OpenFindCommand, node, "search"));
        actions.Add(ContextAction.Separator);
        actions.Add(new("Aggiorna", RefreshNodeCommand, node, "refresh"));
        return actions;
    }

    private IReadOnlyList<ContextAction> SchemaObjectActions(TreeNodeViewModel node)
    {
        var kind = node.SchemaKind!.Value;
        var what = KindName(kind, node.Info);
        var actions = new List<ContextAction>
        {
            new($"Modifica {what}", OpenSchemaObjectCommand, node, "pencil"),
        };
        if (kind == SchemaObjectKind.Routine) actions.Add(new($"Esegui {what}…", RunRoutineCommand, node, "play"));
        actions.Add(ContextAction.Separator);
        if (node.Parent is { } category) actions.AddRange(SchemaCategoryActions(category).TakeWhile(a => !a.IsSeparator));
        actions.Add(new($"Elimina {what}", DeleteSchemaObjectCommand, node, "trash"));
        actions.Add(ContextAction.Separator);
        actions.Add(new("Copia nome", CopyObjectNameCommand, node, "copy"));
        actions.Add(new("Aggiorna", RefreshNodeCommand, node.Parent, "refresh"));
        return actions;
    }

    private IReadOnlyList<ContextAction> ViewsCategoryActions(TreeNodeViewModel node) =>
    [
        new("Nuova vista", NewSchemaObjectCommand, new NewSchemaObjectRequest(node, SchemaObjectKind.View), "plus"),
        ContextAction.Separator,
        new("Export Wizard…", CategoryWizardCommand, new TableOperation(node, "export"), "export"),
        new("Esegui file SQL…", ExecuteSqlFileCommand, node, "play"),
        new("Trova nel database…", OpenFindCommand, node, "search"),
        ContextAction.Separator,
        new("Aggiorna", RefreshNodeCommand, node, "refresh"),
    ];

    // ───── Barra dell'elenco Oggetti per Funzioni/Trigger/Eventi/Sequenze ─────

    [RelayCommand]
    private void EditSelectedObject()
    {
        if (SelectedObjectNode() is { } node) OpenSchemaObjectEditor(node);
    }

    [RelayCommand]
    private void NewInCurrentCategory(string? routineType)
    {
        if (ObjectsTab.Source is { SchemaKind: { } kind } category) NewSchemaObject(new NewSchemaObjectRequest(category, kind, routineType ?? "FUNCTION"));
        else if (ObjectsTab.Source is { Category: "Viste" } views) NewSchemaObject(new NewSchemaObjectRequest(views, SchemaObjectKind.View));
    }

    [RelayCommand]
    private Task DeleteSelectedSchemaObjectAsync() => SelectedObjectNode() is { } node ? DeleteSchemaObjectAsync(node) : Task.CompletedTask;

    [RelayCommand]
    private Task RunSelectedRoutineAsync() => SelectedObjectNode() is { } node ? RunRoutineAsync(node) : Task.CompletedTask;

    [RelayCommand]
    private void OpenSchemaObject(TreeNodeViewModel? node)
    {
        if (node is not null) OpenSchemaObjectEditor(node);
    }

    private void OpenSchemaObjectEditor(TreeNodeViewModel node)
    {
        if (node is not { Profile: { } profile, Database: { } database, Schema: { } schema }) return;
        if (node.Kind == TreeNodeKind.Table && IsViewNode(node))
        {
            OpenViewEditor(profile, database, schema, node.Info ?? new DatabaseObjectInfo(node.Name, "VIEW"));
            return;
        }
        if (node is not { Kind: TreeNodeKind.DbObject, SchemaKind: { } kind, Info: { } item }) return;
        if (kind == SchemaObjectKind.Sequence)
        {
            if (OpenTabs.OfType<SequenceEditorViewModel>().FirstOrDefault(t => t.Profile.Id == profile.Id && t.Database == database && t.Schema == schema && t.Name == item.ObjectName) is { } open)
            {
                SelectedTab = open;
                return;
            }
            var sequence = new SequenceEditorViewModel(_databaseAccess, _dialogs, profile, database, schema, item.ObjectName, s => RefreshCategoryAsync(s.Profile, s.Database, s.Schema, "Sequenze"));
            OpenTabs.Add(sequence);
            SelectedTab = sequence;
            _ = sequence.LoadAsync();
            return;
        }
        if (OpenTabs.OfType<SchemaObjectEditorViewModel>().FirstOrDefault(t => t.Profile.Id == profile.Id && t.Database == database && t.Schema == schema && t.Kind == kind
                && t.Item?.Name == item.Name && t.Item?.Identity == item.Identity) is { } existing)
        {
            SelectedTab = existing;
            return;
        }
        AddEditor(new SchemaObjectEditorViewModel(_databaseAccess, _dialogs, profile, database, schema, kind, item, saved: OnSchemaObjectSavedAsync, openQuery: OpenQueryFor(profile, database)));
    }

    private void OpenViewEditor(ConnectionProfile profile, string database, string schema, DatabaseObjectInfo? view)
    {
        if (view is not null && OpenTabs.OfType<SchemaObjectEditorViewModel>().FirstOrDefault(t => t.Profile.Id == profile.Id && t.Database == database && t.Schema == schema
                && t.IsView && t.Item?.ObjectName == view.ObjectName) is { } existing)
        {
            SelectedTab = existing;
            return;
        }
        var firstTable = FindCategory(profile, database, schema, "Tabelle")?.Objects?.FirstOrDefault()?.Name;
        AddEditor(new SchemaObjectEditorViewModel(_databaseAccess, _dialogs, profile, database, schema, SchemaObjectKind.View, view,
            view is null ? SchemaObjectSql.Template(profile.Kind == DatabaseKind.MySql, schema, SchemaObjectKind.View, firstTable) : null,
            OnSchemaObjectSavedAsync, OpenQueryFor(profile, database)));
    }

    private void AddEditor(SchemaObjectEditorViewModel editor)
    {
        OpenTabs.Add(editor);
        SelectedTab = editor;
        _ = editor.LoadAsync();
    }

    /// <summary>Apre una query con il testo indicato (Esegui funzione, Anteprima vista); se richiesto la esegue subito.</summary>
    private Action<string, bool> OpenQueryFor(ConnectionProfile profile, string database) => (sql, run) =>
    {
        var tab = new QueryTabViewModel(_databaseAccess, profile, database, false, Profiles.ToArray(), _dialogs) { SqlText = sql };
        OpenTabs.Add(tab);
        SelectedTab = tab;
        if (run) _ = tab.ExecuteAllCommand.ExecuteAsync(null);
    };

    private async Task OnSchemaObjectSavedAsync(SchemaObjectEditorViewModel editor)
    {
        StatusMessage = editor.StatusMessage;
        await RefreshCategoryAsync(editor.Profile, editor.Database, editor.Schema, editor.IsView ? "Viste" : editor.Kind switch
        {
            SchemaObjectKind.Routine => "Funzioni",
            SchemaObjectKind.Trigger => "Trigger",
            SchemaObjectKind.Event => "Eventi",
            _ => "Sequenze"
        });
    }

    private TreeNodeViewModel? FindCategory(ConnectionProfile profile, string database, string schema, string category) =>
        _connectionNodes.TryGetValue(profile.Id, out var connection)
            ? connection.SelfAndDescendants().FirstOrDefault(n => n.Kind == TreeNodeKind.Category && n.Category == category && n.Database == database && n.Schema == schema)
            : null;

    /// <summary>Ricarica una categoria dell'albero (se già aperta) e l'elenco Oggetti se la sta mostrando.</summary>
    private async Task RefreshCategoryAsync(ConnectionProfile profile, string database, string schema, string categoryName)
    {
        if (FindCategory(profile, database, schema, categoryName) is not { } category) return;
        if (category.IsLoaded) await RefreshNodeAsync(category);
        if (ObjectsTab.Source == category) ShowCategoryObjects(category, bringToFront: false);
    }

    [RelayCommand]
    private void NewSchemaObject(NewSchemaObjectRequest? request)
    {
        if (request is null || CurrentDatabaseContext(request.Node) is not { } context) return;
        var mysql = context.Profile.Kind == DatabaseKind.MySql;
        switch (request.Kind)
        {
            case SchemaObjectKind.View:
                OpenViewEditor(context.Profile, context.Database, context.Schema, null);
                return;
            case SchemaObjectKind.Sequence:
            {
                var sequence = new SequenceEditorViewModel(_databaseAccess, _dialogs, context.Profile, context.Database, context.Schema, null,
                    s => RefreshCategoryAsync(s.Profile, s.Database, s.Schema, "Sequenze"));
                OpenTabs.Add(sequence);
                SelectedTab = sequence;
                _ = sequence.LoadAsync();
                return;
            }
            default:
            {
                var table = request.Node.Kind == TreeNodeKind.Table ? request.Node.Name : FindCategory(context.Profile, context.Database, context.Schema, "Tabelle")?.Objects?.FirstOrDefault()?.Name;
                AddEditor(new SchemaObjectEditorViewModel(_databaseAccess, _dialogs, context.Profile, context.Database, context.Schema, request.Kind, null,
                    SchemaObjectSql.Template(mysql, context.Schema, request.Kind, table, request.RoutineType), OnSchemaObjectSavedAsync, OpenQueryFor(context.Profile, context.Database)));
                return;
            }
        }
    }

    [RelayCommand]
    private async Task DeleteSchemaObjectAsync(TreeNodeViewModel? node)
    {
        if (node is not { Kind: TreeNodeKind.DbObject, SchemaKind: { } kind, Info: { } item, Profile: { } profile, Database: { } database, Schema: { } schema }) return;
        var what = KindName(kind, item);
        if (!await _dialogs.ConfirmAsync($"Elimina {what}", $"Eliminare definitivamente {(kind == SchemaObjectKind.Sequence ? "la" : "il/la")} {what} «{item.Name}» da «{database}»?\n\nL’operazione non può essere annullata.", "Elimina", danger: true))
            return;
        try
        {
            await _databaseAccess.ExecuteStatementsAsync(profile, database, [SchemaObjectSql.DropSql(profile.Kind == DatabaseKind.MySql, schema, kind, item)], CancellationToken.None);
            foreach (var tab in OpenTabs.OfType<SchemaObjectEditorViewModel>().Where(t => t.Profile.Id == profile.Id && t.Database == database && t.Kind == kind && t.Item?.Name == item.Name).ToArray()) CloseTab(tab);
            StatusMessage = $"«{item.Name}» eliminato.";
            await RefreshCategoryAsync(profile, database, schema, node.Category!);
        }
        catch (Exception exception) { await ReportErrorAsync("Eliminazione non riuscita", exception); }
    }

    [RelayCommand]
    private async Task RunRoutineAsync(TreeNodeViewModel? node)
    {
        if (node is not { Kind: TreeNodeKind.DbObject, SchemaKind: SchemaObjectKind.Routine, Info: { } item, Profile: { } profile, Database: { } database, Schema: { } schema }) return;
        try
        {
            var parameters = await _databaseAccess.GetRoutineParametersAsync(profile, database, schema, item, CancellationToken.None);
            OpenQueryFor(profile, database)(SchemaObjectSql.RunTemplate(profile.Kind == DatabaseKind.MySql, schema, item, parameters), parameters.Count == 0);
        }
        catch (Exception exception) { await ReportErrorAsync("Impossibile preparare l’esecuzione", exception); }
    }

    [RelayCommand]
    private async Task CopyObjectNameAsync(TreeNodeViewModel? node)
    {
        if (node?.Info?.ObjectName is not { } name) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
            await clipboard.SetTextAsync(name);
        StatusMessage = $"Copiato «{name}».";
    }
}
