using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public sealed record MoveConnectionRequest(Guid ProfileId, string Folder);
public sealed record MoveFolderRequest(string Folder, string Parent);
public sealed record ReorderRequest(string Key, int Delta);

/// <summary>Dove rilasciare un elemento trascinato rispetto al nodo di destinazione.</summary>
public enum DropPosition { Before, Inside, After }

/// <summary>
/// Cartelle e sottocartelle delle connessioni («Clienti/Roma»), ordine personalizzato (trascinamento, Sposta su/giù),
/// rinomina, eliminazione e modifica connessione.
/// </summary>
public partial class MainWindowViewModel
{
    private const char Separator = '/';

    // ───────────── Percorsi ─────────────

    internal static string NormalizeFolder(string? path) =>
        string.Join(Separator, (path ?? string.Empty).Split(Separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    private static string ParentOf(string path) => path.LastIndexOf(Separator) is var i and >= 0 ? path[..i] : string.Empty;
    private static string LeafOf(string path) => path.LastIndexOf(Separator) is var i and >= 0 ? path[(i + 1)..] : path;
    private static string Combine(string parent, string leaf) => parent.Length == 0 ? leaf : $"{parent}{Separator}{leaf}";
    private static bool SamePath(string? a, string? b) => string.Equals(NormalizeFolder(a), NormalizeFolder(b), StringComparison.OrdinalIgnoreCase);
    private static bool IsUnder(string path, string ancestor) =>
        SamePath(path, ancestor) || NormalizeFolder(path).StartsWith(NormalizeFolder(ancestor) + Separator, StringComparison.OrdinalIgnoreCase);
    internal static string DisplayPath(string path) => path.Replace(Separator.ToString(), " › ");

    public static string FolderKey(string path) => "f:" + NormalizeFolder(path).ToLowerInvariant();
    public static string ConnectionKey(Guid id) => "c:" + id;

    private string FolderOf(ConnectionProfile profile) => NormalizeFolder(profile.Folder);
    private static bool HasFolder(ConnectionProfile profile) => NormalizeFolder(profile.Folder).Length > 0;

    /// <summary>Tutte le cartelle (anche intermedie e vuote), in percorso completo.</summary>
    public IReadOnlyList<string> AllFolders()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Profiles.Select(FolderOf).Concat(AppSettings.Current.ConnectionFolders.Select(NormalizeFolder)).Where(f => f.Length > 0))
            for (var path = folder; path.Length > 0; path = ParentOf(path)) paths.Add(path);
        return paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string? CurrentFolder()
    {
        for (var node = SelectedNode; node is not null; node = node.Parent)
            if (node.Kind == TreeNodeKind.Group) return node.FolderPath;
        return null;
    }

    private static Guid? TabProfileId(WorkspaceTabViewModel tab) => tab switch
    {
        TableTabViewModel table => table.ProfileId,
        TableDesignerViewModel designer => designer.Profile.Id,
        SchemaObjectEditorViewModel editor => editor.Profile.Id,
        SequenceEditorViewModel sequence => sequence.Profile.Id,
        _ => null
    };

    // ───────────── Costruzione dell'albero ─────────────

    private List<TreeNodeViewModel> BuildFolderTree()
    {
        var folders = AllFolders();
        var collapsed = new HashSet<string>(AppSettings.Current.CollapsedFolders.Select(NormalizeFolder), StringComparer.OrdinalIgnoreCase);
        List<TreeNodeViewModel> Children(string parent)
        {
            var nodes = new List<TreeNodeViewModel>();
            foreach (var path in folders.Where(f => SamePath(ParentOf(f), parent)))
            {
                var group = new TreeNodeViewModel(LeafOf(path), "folder", TreeNodeKind.Group) { FolderPath = path, ExpandHandler = OnNodeExpandRequested, IsExpanded = !collapsed.Contains(path) };
                group.SetChildren(Children(path));
                group.PropertyChanged += OnFolderNodeChanged;
                nodes.Add(group);
            }
            nodes.AddRange(Profiles.Where(p => SamePath(FolderOf(p), parent)).Select(ConnectionNode));
            return SortSiblings(nodes);
        }
        return Children(string.Empty);
    }

    private static string KeyOf(TreeNodeViewModel node) => node.Kind == TreeNodeKind.Group ? FolderKey(node.FolderPath!) : ConnectionKey(node.Profile!.Id);

    /// <summary>Ordine: quello scelto dall'utente; per gli elementi mai spostati cartelle prima, preferiti, poi alfabetico.</summary>
    private static List<TreeNodeViewModel> SortSiblings(IEnumerable<TreeNodeViewModel> nodes)
    {
        var order = AppSettings.Current.TreeOrder;
        return nodes
            .OrderBy(n => order.IndexOf(KeyOf(n)) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(n => n.Kind == TreeNodeKind.Group ? 0 : 1)
            .ThenByDescending(n => n.Profile?.IsFavorite == true)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Si ricorda quali cartelle sono chiuse.</summary>
    private void OnFolderNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TreeNodeViewModel.IsExpanded) || sender is not TreeNodeViewModel { FolderPath: { } path } node) return;
        var collapsed = AppSettings.Current.CollapsedFolders;
        collapsed.RemoveAll(f => SamePath(f, path));
        if (!node.IsExpanded) collapsed.Add(path);
        AppSettings.Current.Save();
    }

    private IEnumerable<TreeNodeViewModel> SiblingsOf(string parent)
    {
        var children = parent.Length == 0 ? RootNodes : FindFolderNode(parent)?.Children;
        return children?.Where(c => c.Kind is TreeNodeKind.Group or TreeNodeKind.Connection) ?? [];
    }

    private TreeNodeViewModel? FindFolderNode(string path) =>
        RootNodes.SelectMany(n => n.SelfAndDescendants()).FirstOrDefault(n => n.Kind == TreeNodeKind.Group && SamePath(n.FolderPath, path));

    /// <summary>Fissa nell'elenco d'ordine la disposizione attuale dei fratelli di una cartella (o della radice).</summary>
    private void MaterializeOrder(string parent, IReadOnlyList<string>? keys = null)
    {
        keys ??= SiblingsOf(parent).Select(KeyOf).ToArray();
        var order = AppSettings.Current.TreeOrder;
        order.RemoveAll(k => keys.Contains(k));
        order.AddRange(keys);
    }

    // ───────────── Menu ─────────────

    private IReadOnlyList<ContextAction> FolderActions(TreeNodeViewModel folder)
    {
        var path = folder.FolderPath!;
        return
        [
            new("Nuova connessione qui…", NewConnectionInFolderCommand, path, "plus"),
            new("Nuova sottocartella…", NewFolderCommand, path, "folder"),
            new("Nuova cartella…", NewFolderCommand, string.Empty, "folder"),
            ContextAction.Separator,
            new("Rinomina cartella…", RenameFolderCommand, path, "pencil"),
            new("Sposta su", ReorderCommand, new ReorderRequest(FolderKey(path), -1), "arrow-up"),
            new("Sposta giù", ReorderCommand, new ReorderRequest(FolderKey(path), 1), "arrow-down"),
            new("Sposta in cartella", Glyph: "folder", Children: MoveFolderActions(path)),
            ContextAction.Separator,
            new("Elimina cartella", DeleteFolderCommand, path, "trash"),
        ];
    }

    private IReadOnlyList<ContextAction> MoveFolderActions(string path)
    {
        var actions = AllFolders()
            .Where(f => !IsUnder(f, path) && !SamePath(f, ParentOf(path)))
            .Select(f => new ContextAction(DisplayPath(f), MoveFolderCommand, new MoveFolderRequest(path, f), "folder"))
            .ToList();
        if (ParentOf(path).Length > 0) actions.Add(new ContextAction("Radice", MoveFolderCommand, new MoveFolderRequest(path, string.Empty), "server"));
        return actions.Count > 0 ? actions : [new ContextAction("(nessuna altra cartella)", IsEnabled: false)];
    }

    private IReadOnlyList<ContextAction> MoveToFolderActions(TreeNodeViewModel connection)
    {
        var profile = connection.Profile!;
        var current = FolderOf(profile);
        var actions = AllFolders()
            .Where(f => !SamePath(f, current))
            .Select(f => new ContextAction(DisplayPath(f), MoveConnectionCommand, new MoveConnectionRequest(profile.Id, f), "folder"))
            .ToList();
        if (current.Length > 0) actions.Add(new ContextAction("Radice (nessuna cartella)", MoveConnectionCommand, new MoveConnectionRequest(profile.Id, string.Empty), "server"));
        if (actions.Count > 0) actions.Add(ContextAction.Separator);
        actions.Add(new ContextAction("Nuova cartella…", NewFolderCommand, profile.Id, "plus"));
        return actions;
    }

    /// <summary>Voci aggiunte al menu della connessione: rinomina e ordine.</summary>
    private IReadOnlyList<ContextAction> ConnectionOrganizeActions(TreeNodeViewModel node) =>
    [
        new("Rinomina connessione…", RenameConnectionCommand, node, "pencil"),
        new("Modifica connessione…", EditConnectionCommand, node, "settings"),
        new("Sposta su", ReorderCommand, new ReorderRequest(ConnectionKey(node.Profile!.Id), -1), "arrow-up"),
        new("Sposta giù", ReorderCommand, new ReorderRequest(ConnectionKey(node.Profile!.Id), 1), "arrow-down"),
        new("Sposta in cartella", Glyph: "folder", Children: MoveToFolderActions(node)),
    ];

    // ───────────── Comandi ─────────────

    [RelayCommand]
    private async Task NewConnectionInFolderAsync(string? folder)
    {
        var profile = await _connectionDialog.ShowAsync(null, AllFolders(), folder);
        if (profile is null) return;
        profile = profile with { Folder = NormalizeFolder(profile.Folder) };
        Profiles.Add(profile);
        RebuildConnectionTree();
        StatusMessage = $"Connessione «{profile.Name}» aggiunta{(HasFolder(profile) ? $" in «{DisplayPath(profile.Folder)}»" : string.Empty)}.";
        await SaveProfilesAsync();
    }

    [RelayCommand]
    private async Task EditConnectionAsync(TreeNodeViewModel? node)
    {
        var connection = node is null ? null : ConnectionOf(node);
        if (connection?.Profile is not { } current || Profiles.FirstOrDefault(p => p.Id == current.Id) is not { } saved) return;
        var updated = await _connectionDialog.ShowAsync(null, AllFolders(), null, saved);
        if (updated is null) return;
        updated = updated with { Folder = NormalizeFolder(updated.Folder) };
        Profiles[Profiles.IndexOf(saved)] = updated;
        // Il nodo va ricreato: host, utente o password possono essere cambiati.
        if (ObjectsTab.Source is { } source && ConnectionOf(source) == connection) ObjectsTab.Clear();
        _connectionNodes.Remove(updated.Id);
        _connectedProfiles.Remove(updated.Id);
        _databaseAccess.Provider(saved).ClearPools();
        OnPropertyChanged(nameof(ConnectionCount));
        RebuildConnectionTree();
        await SaveProfilesAsync();
        StatusMessage = $"Connessione «{updated.Name}» aggiornata.";
    }

    [RelayCommand]
    private async Task RenameConnectionAsync(TreeNodeViewModel? node)
    {
        var connection = node is null ? null : ConnectionOf(node);
        if (connection?.Profile is not { } current || Profiles.FirstOrDefault(p => p.Id == current.Id) is not { } saved) return;
        var name = await _dialogs.PromptAsync("Rinomina connessione", $"Nuovo nome per «{saved.Name}»:", saved.Name, "Rinomina");
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == saved.Name) return;
        var renamed = saved with { Name = name.Trim() };
        Profiles[Profiles.IndexOf(saved)] = renamed;
        connection.Name = renamed.Name;
        if (!_connectedProfiles.Contains(renamed.Id)) _connectionNodes.Remove(renamed.Id);
        RebuildConnectionTree();
        await SaveProfilesAsync();
        StatusMessage = $"Connessione rinominata in «{renamed.Name}».";
    }

    [RelayCommand]
    private Task MoveConnectionAsync(MoveConnectionRequest? request) =>
        request is null ? Task.CompletedTask : MoveConnectionToAsync(request.ProfileId, request.Folder);

    /// <summary>Sposta una connessione in una cartella ("" = radice), in fondo alla cartella.</summary>
    public async Task MoveConnectionToAsync(Guid profileId, string folder)
    {
        if (Profiles.FirstOrDefault(p => p.Id == profileId) is not { } profile) return;
        folder = NormalizeFolder(folder);
        if (SamePath(FolderOf(profile), folder)) return;
        SetConnectionFolder(profile, folder);
        AppSettings.Current.TreeOrder.Remove(ConnectionKey(profileId));
        AppSettings.Current.Save();
        RebuildConnectionTree();
        await SaveProfilesAsync();
        StatusMessage = folder.Length == 0 ? $"«{profile.Name}» spostata nella radice." : $"«{profile.Name}» spostata in «{DisplayPath(folder)}».";
    }

    private void SetConnectionFolder(ConnectionProfile profile, string folder)
    {
        // La cartella che si svuota resta nell'albero finché l'utente non la elimina.
        var previous = FolderOf(profile);
        var folders = AppSettings.Current.ConnectionFolders;
        if (previous.Length > 0 && !folders.Any(f => SamePath(f, previous))) folders.Add(previous);
        Profiles[Profiles.IndexOf(profile)] = profile with { Folder = folder };
        // Un nodo già connesso resta com'è (la cartella non serve al nodo); gli altri si ricreano.
        if (!_connectedProfiles.Contains(profile.Id)) _connectionNodes.Remove(profile.Id);
    }

    [RelayCommand]
    private async Task MoveFolderAsync(MoveFolderRequest? request)
    {
        if (request is null) return;
        if (MoveFolderInto(request.Folder, request.Parent) is not { } moved) return;
        await SaveProfilesAsync();
        StatusMessage = $"Cartella spostata in «{(request.Parent.Length == 0 ? "radice" : DisplayPath(request.Parent))}»: {DisplayPath(moved)}.";
    }

    /// <summary>Sposta una cartella (con tutto il contenuto) sotto un'altra. Restituisce il nuovo percorso.</summary>
    private string? MoveFolderInto(string folder, string parent)
    {
        folder = NormalizeFolder(folder);
        parent = NormalizeFolder(parent);
        if (folder.Length == 0 || IsUnder(parent, folder) || SamePath(ParentOf(folder), parent)) return null;
        var target = Combine(parent, LeafOf(folder));
        ReplaceFolderPrefix(folder, target);
        AppSettings.Current.TreeOrder.Remove(FolderKey(target));
        AppSettings.Current.Save();
        RebuildConnectionTree();
        return target;
    }

    /// <summary>Crea una cartella. Parametro: percorso della cartella madre (sottocartella) oppure Guid di una connessione da spostarvi.</summary>
    [RelayCommand]
    private async Task NewFolderAsync(object? argument)
    {
        var parent = argument as string ?? string.Empty;
        var title = parent.Length > 0 ? $"Nuova sottocartella in «{DisplayPath(parent)}»" : "Nuova cartella";
        var name = await _dialogs.PromptAsync(title, "Nome della cartella:", string.Empty, "Crea");
        name = NormalizeFolder(name);
        if (name.Length == 0) return;
        var path = Combine(parent, name);
        var existing = AllFolders().FirstOrDefault(f => SamePath(f, path));
        if (existing is null)
        {
            AppSettings.Current.ConnectionFolders.Add(path);
            AppSettings.Current.Save();
        }
        else path = existing;
        if (FindFolderNode(parent) is { } parentNode) parentNode.IsExpanded = true;
        if (argument is Guid id) await MoveConnectionToAsync(id, path);
        else
        {
            RebuildConnectionTree();
            StatusMessage = existing is null ? $"Cartella «{DisplayPath(path)}» creata: trascinaci dentro connessioni o altre cartelle." : $"La cartella «{DisplayPath(path)}» esiste già.";
        }
    }

    [RelayCommand]
    private async Task RenameFolderAsync(string? folder)
    {
        folder = NormalizeFolder(folder);
        if (folder.Length == 0) return;
        var name = NormalizeFolder(await _dialogs.PromptAsync("Rinomina cartella", $"Nuovo nome per «{LeafOf(folder)}»:", LeafOf(folder), "Rinomina"));
        if (name.Length == 0 || name.Contains(Separator) || name == LeafOf(folder)) return;
        var target = Combine(ParentOf(folder), name);
        ReplaceFolderPrefix(folder, target);
        RebuildConnectionTree();
        await SaveProfilesAsync();
        StatusMessage = $"Cartella «{LeafOf(folder)}» rinominata in «{name}».";
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(string? folder)
    {
        folder = NormalizeFolder(folder);
        if (folder.Length == 0) return;
        var count = Profiles.Count(p => IsUnder(FolderOf(p), folder));
        var parent = ParentOf(folder);
        var where = parent.Length == 0 ? "nella radice" : $"in «{DisplayPath(parent)}»";
        if (count > 0 && !await _dialogs.ConfirmAsync("Elimina cartella",
                $"Eliminare la cartella «{DisplayPath(folder)}»?\n\nLe {count} connessioni e le sottocartelle che contiene non vengono eliminate: passano {where}.", "Elimina cartella", danger: true))
            return;
        // Il contenuto sale di un livello: «A/B/C» con B eliminata diventa «A/C».
        foreach (var profile in Profiles.ToArray())
        {
            var path = FolderOf(profile);
            if (SamePath(path, folder)) SetConnectionFolder(profile, parent);
        }
        foreach (var sub in AllFolders().Where(f => SamePath(ParentOf(f), folder)).ToArray()) ReplaceFolderPrefix(sub, Combine(parent, LeafOf(sub)));
        AppSettings.Current.ConnectionFolders.RemoveAll(f => SamePath(f, folder));
        AppSettings.Current.TreeOrder.Remove(FolderKey(folder));
        AppSettings.Current.Save();
        RebuildConnectionTree();
        await SaveProfilesAsync();
        StatusMessage = $"Cartella «{DisplayPath(folder)}» eliminata.";
    }

    /// <summary>Rinomina/sposta un ramo di cartelle: connessioni, cartelle vuote, ordine e stato aperto/chiuso.</summary>
    private void ReplaceFolderPrefix(string from, string to)
    {
        string Rewrite(string path) => SamePath(path, from) ? to : to + NormalizeFolder(path)[NormalizeFolder(from).Length..];
        foreach (var profile in Profiles.ToArray())
            if (IsUnder(FolderOf(profile), from)) SetConnectionFolder(profile, Rewrite(FolderOf(profile)));
        var settings = AppSettings.Current;
        var folders = settings.ConnectionFolders.Select(NormalizeFolder).Where(f => f.Length > 0).Select(f => IsUnder(f, from) ? Rewrite(f) : f).ToList();
        if (!folders.Contains(to, StringComparer.OrdinalIgnoreCase)) folders.Add(to);
        settings.ConnectionFolders = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.TreeOrder = settings.TreeOrder.Select(k => k.StartsWith("f:", StringComparison.Ordinal) && IsUnder(k[2..], from) ? FolderKey(Rewrite(k[2..])) : k).ToList();
        settings.CollapsedFolders = settings.CollapsedFolders.Select(f => IsUnder(f, from) ? Rewrite(f) : f).ToList();
        settings.Save();
    }

    // ───────────── Ordine ─────────────

    [RelayCommand]
    private void Reorder(ReorderRequest? request)
    {
        if (request is null) return;
        var node = RootNodes.SelectMany(n => n.SelfAndDescendants()).FirstOrDefault(n => n.Kind is TreeNodeKind.Group or TreeNodeKind.Connection && KeyOf(n) == request.Key);
        if (node is null) return;
        var parent = node.Kind == TreeNodeKind.Group ? ParentOf(node.FolderPath!) : FolderOf(node.Profile!);
        var keys = SiblingsOf(parent).Select(KeyOf).ToList();
        var index = keys.IndexOf(request.Key);
        var target = index + request.Delta;
        if (index < 0 || target < 0 || target >= keys.Count) return;
        (keys[index], keys[target]) = (keys[target], keys[index]);
        MaterializeOrder(parent, keys);
        AppSettings.Current.Save();
        RebuildConnectionTree();
        SelectedNode = node.Kind == TreeNodeKind.Group ? FindFolderNode(node.FolderPath!) : node;
    }

    /// <summary>
    /// Trascinamento: sposta una connessione o una cartella prima/dopo un nodo, oppure dentro una cartella.
    /// <paramref name="target"/> null = radice (in fondo).
    /// </summary>
    public async Task DropAsync(string key, TreeNodeViewModel? target, DropPosition position)
    {
        var isFolder = key.StartsWith("f:", StringComparison.Ordinal);
        var folderPath = isFolder ? AllFolders().FirstOrDefault(f => FolderKey(f) == key) : null;
        var profile = isFolder ? null : Profiles.FirstOrDefault(p => ConnectionKey(p.Id) == key);
        if (folderPath is null && profile is null) return;
        if (target is not null && KeyOf(target) == key) return;

        // Cartella di destinazione e posizione tra i fratelli.
        var inside = target is null || target.Kind == TreeNodeKind.Group && position == DropPosition.Inside;
        var parent = target is null ? string.Empty
            : inside ? target.FolderPath!
            : target.Kind == TreeNodeKind.Group ? ParentOf(target.FolderPath!) : FolderOf(target.Profile!);
        if (folderPath is not null && IsUnder(parent, folderPath))
        {
            StatusMessage = "Una cartella non può essere spostata dentro sé stessa.";
            return;
        }

        // 1. Cambio di cartella.
        var movedKey = key;
        if (profile is not null && !SamePath(FolderOf(profile), parent)) SetConnectionFolder(profile, parent);
        if (folderPath is not null && !SamePath(ParentOf(folderPath), parent))
        {
            var newPath = Combine(parent, LeafOf(folderPath));
            ReplaceFolderPrefix(folderPath, newPath);
            movedKey = FolderKey(newPath);
        }
        RebuildConnectionTree();

        // 2. Posizione: prima/dopo il nodo di destinazione, oppure in fondo alla cartella.
        var keys = SiblingsOf(parent).Select(KeyOf).Where(k => k != movedKey).ToList();
        var at = keys.Count;
        if (!inside && target is not null && keys.IndexOf(KeyOf(target)) is var t and >= 0) at = position == DropPosition.After ? t + 1 : t;
        keys.Insert(Math.Min(at, keys.Count), movedKey);
        MaterializeOrder(parent, keys);
        AppSettings.Current.Save();
        if (inside && target is not null) AppSettings.Current.CollapsedFolders.RemoveAll(f => SamePath(f, target.FolderPath));
        RebuildConnectionTree();
        await SaveProfilesAsync();
        var name = profile?.Name ?? LeafOf(folderPath!);
        StatusMessage = $"«{name}» spostata{(parent.Length == 0 ? " nella radice" : $" in «{DisplayPath(parent)}»")}.";
    }
}
