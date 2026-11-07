using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MosaicDB.Core.Models;

namespace MosaicDB.App.ViewModels;

public enum TreeNodeKind { Group, Connection, Database, Schema, Category, Table, Placeholder }

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
    }

    public string Name { get; }
    public string Glyph { get; }
    public TreeNodeKind Kind { get; }
    public ConnectionProfile? Profile { get; }
    public string? Database { get; }
    public string? Schema { get; }
    public string? Category { get; }
    public TreeNodeViewModel? Parent { get; private set; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    /// <summary>Metadati degli oggetti caricati per una categoria Tabelle/Viste.</summary>
    public IReadOnlyList<DatabaseObjectInfo>? Objects { get; set; }

    /// <summary>Invocato quando un nodo lazy viene espanso e non ha ancora i figli.</summary>
    public Action<TreeNodeViewModel>? ExpandHandler { get; set; }

    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isVisible = true;

    public bool IsPlaceholder => Kind == TreeNodeKind.Placeholder;
    public bool IsConnection => Kind == TreeNodeKind.Connection;
    public bool IsObjectCategory => Kind == TreeNodeKind.Category && Category is "Tabelle" or "Viste";
    private bool IsLazy => Kind is TreeNodeKind.Connection or TreeNodeKind.Database or TreeNodeKind.Schema || IsObjectCategory;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !IsLoaded && !IsLoading && IsLazy) ExpandHandler?.Invoke(this);
    }

    public void SetChildren(IEnumerable<TreeNodeViewModel> children)
    {
        Children.Clear();
        foreach (var child in children)
        {
            child.Parent = this;
            child.ExpandHandler ??= ExpandHandler;
            Children.Add(child);
        }
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
