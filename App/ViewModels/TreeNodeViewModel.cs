using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>DbObject = funzione, procedura, trigger, evento o sequenza (categoria nel campo Category).</summary>
/// <summary>TableGroup = gruppo di tabelle dell'utente; SavedQuery / BackupFile = elementi delle categorie locali «Query salvate» e «Backup».</summary>
public enum TreeNodeKind { Group, Connection, Database, Schema, Category, Table, DbObject, TableGroup, SavedQuery, BackupFile, Placeholder }

public partial class TreeNodeViewModel : ObservableObject
{
    public TreeNodeViewModel(string name, string glyph, TreeNodeKind kind, ConnectionProfile? profile = null, string? database = null, string? schema = null, string? category = null)
    {
        Name = name;
        Glyph = glyph;
        Kind = kind;
        Profile = profile;
        Database = database;
        Schema = schema;
        Category = category;
        if (IsLazy) Children.Add(CreatePlaceholder());
        else if (kind != TreeNodeKind.Group) IsLoaded = true;
        _visibleChildren = Children;
        // I figli cambiano mentre è attivo un filtro: si torna all'elenco completo finché il filtro non viene riapplicato.
        Children.CollectionChanged += (_, _) => { if (!ReferenceEquals(_visibleChildren, Children)) VisibleChildren = Children; };
    }

    private string _nodeName = string.Empty;

    /// <summary>Nome mostrato (modificabile: rinomina di connessioni e cartelle senza ricreare il nodo).</summary>
    public string Name
    {
        get => _nodeName;
        set => SetProperty(ref _nodeName, value);
    }

    /// <summary>Cartelle: percorso completo («Clienti/Roma»); null per gli altri nodi.</summary>
    public string? FolderPath { get; init; }
    public string Glyph { get; }
    public TreeNodeKind Kind { get; }
    public ConnectionProfile? Profile { get; }
    public string? Database { get; }
    public string? Schema { get; }
    public string? Category { get; }
    public TreeNodeViewModel? Parent { get; private set; }
    public BulkObservableCollection<TreeNodeViewModel> Children { get; } = [];

    private IList<TreeNodeViewModel> _visibleChildren;

    /// <summary>
    /// Figli mostrati nell'albero: tutti, oppure solo quelli che passano il filtro di ricerca.
    /// Con migliaia di tabelle è molto più rapido che creare le righe e nasconderle.
    /// </summary>
    public IList<TreeNodeViewModel> VisibleChildren
    {
        get => _visibleChildren;
        private set => SetProperty(ref _visibleChildren, value);
    }

    /// <summary>Mostra solo i figli visibili per il filtro (null = tutti).</summary>
    public void ShowFilteredChildren(bool filtered)
    {
        if (!filtered) { if (!ReferenceEquals(_visibleChildren, Children)) VisibleChildren = Children; return; }
        var visible = Children.Where(child => child.IsVisible).ToArray();
        if (visible.Length == Children.Count) { if (!ReferenceEquals(_visibleChildren, Children)) VisibleChildren = Children; return; }
        if (!ReferenceEquals(_visibleChildren, Children) && _visibleChildren.SequenceEqual(visible)) return;
        VisibleChildren = visible;
    }

    /// <summary>Metadati degli oggetti caricati per una categoria (Tabelle, Viste, Funzioni, Trigger…).</summary>
    public IReadOnlyList<DatabaseObjectInfo>? Objects { get; set; }

    /// <summary>Metadati dell'oggetto rappresentato dal nodo (tabelle, viste e oggetti di schema).</summary>
    public DatabaseObjectInfo? Info { get; init; }

    /// <summary>Tipo di oggetto di schema della categoria (Funzioni → Routine…); null per Tabelle/Viste e categorie non elencabili.</summary>
    public SchemaObjectKind? SchemaKind => SchemaKindOf(Category);

    public static SchemaObjectKind? SchemaKindOf(string? category) => category switch
    {
        "Funzioni" => SchemaObjectKind.Routine,
        "Trigger" => SchemaObjectKind.Trigger,
        "Eventi" => SchemaObjectKind.Event,
        "Sequenze" => SchemaObjectKind.Sequence,
        _ => null
    };

    /// <summary>Invocato quando un nodo lazy viene espanso e non ha ancora i figli.</summary>
    public Action<TreeNodeViewModel>? ExpandHandler { get; set; }

    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isVisible = true;

    public bool IsPlaceholder => Kind == TreeNodeKind.Placeholder;

    /// <summary>Le categorie possono contenere migliaia di tabelle: nell'albero se ne creano solo le righe visibili.</summary>
    public bool VirtualizeChildren => Kind == TreeNodeKind.Category;
    public bool IsConnection => Kind == TreeNodeKind.Connection;
    public bool IsObjectCategory => Kind == TreeNodeKind.Category && (Category is "Tabelle" or "Viste" or "Query salvate" or "Backup" || SchemaKind is not null);
    public bool IsLocalCategory => Kind == TreeNodeKind.Category && Category is "Query salvate" or "Backup";

    /// <summary>Categoria che contiene il nodo (anche attraverso un gruppo di tabelle).</summary>
    public TreeNodeViewModel? CategoryNode
    {
        get
        {
            for (var node = Parent; node is not null; node = node.Parent)
                if (node.Kind == TreeNodeKind.Category) return node;
            return null;
        }
    }
    public bool IsTableCategory => Kind == TreeNodeKind.Category && Category is "Tabelle" or "Viste";
    private bool IsLazy => Kind is TreeNodeKind.Connection or TreeNodeKind.Database or TreeNodeKind.Schema || IsObjectCategory;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !IsLoaded && !IsLoading && IsLazy) ExpandHandler?.Invoke(this);
        if (!value) Collapsed?.Invoke(this);
    }

    /// <summary>Rinomina sul posto (F2 o «Rinomina»): al posto del nome compare una casella di testo.</summary>
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editName = string.Empty;

    /// <summary>Un nodo è stato chiuso (dall'utente o dal programma).</summary>
    public static event Action<TreeNodeViewModel>? Collapsed;

    public void SetChildren(IEnumerable<TreeNodeViewModel> children)
    {
        var list = children.ToList();
        foreach (var child in list)
        {
            child.Parent = this;
            child.ExpandHandler ??= ExpandHandler;
        }
        // Un solo aggiornamento per tutto l'elenco (non uno per tabella).
        Children.ReplaceAll(list);
        IsLoaded = true;
    }

    /// <summary>Riporta il nodo allo stato "non caricato" (es. dopo un errore o una disconnessione).</summary>
    public void Reset()
    {
        Children.Clear();
        if (IsLazy) Children.Add(CreatePlaceholder());
        Objects = null;
        IsLoaded = false;
        IsExpanded = false;
    }

    public IEnumerable<TreeNodeViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var descendant in child.SelfAndDescendants()) yield return descendant;
    }

    private TreeNodeViewModel CreatePlaceholder() => new("Caricamento…", "", TreeNodeKind.Placeholder) { Parent = this };
}

/// <summary>ObservableCollection che sostituisce tutto il contenuto con una sola notifica.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
