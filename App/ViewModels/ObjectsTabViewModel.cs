using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Navifox.App.ViewModels;

/// <summary>Scheda fissa "Oggetti": elenco filtrabile di tabelle/viste del database selezionato.</summary>
public partial class ObjectsTabViewModel : WorkspaceTabViewModel
{
    private readonly Func<ObjectRowViewModel, Task> _openTable;
    private readonly Func<Task> _refresh;
    private readonly Func<ObjectRowViewModel, string, Task>? _wizard;
    private List<ObjectRowViewModel> _all = [];

    public ObjectsTabViewModel(Func<ObjectRowViewModel, Task> openTable, Func<Task> refresh, Func<ObjectRowViewModel, string, Task>? wizard = null)
    {
        _wizard = wizard;
        _openTable = openTable;
        _refresh = refresh;
    }

    public override string TabTitle => "Oggetti";
    public override string TabGlyph => "list";
    public override bool CanClose => false;

    public ObservableCollection<ObjectRowViewModel> VisibleObjects { get; } = [];

    /// <summary>Nodo categoria (Tabelle/Viste) da cui provengono gli oggetti mostrati.</summary>
    public TreeNodeViewModel? Source { get; private set; }

    [ObservableProperty] private string _title = "Oggetti";
    [ObservableProperty] private string _context = "Seleziona un database o la voce «Tabelle» nell’albero.";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _countLabel = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand), nameof(ImportCommand), nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ObjectRowViewModel? _selectedObject;

    public bool HasSelection => SelectedObject is not null;

    // ───── Visualizza: Elenco / Dettaglio, ordinamento, colonne ─────
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDetailMode), nameof(ShowDetail), nameof(ShowList))] private bool _isListMode;
    public bool ShowDetail => HasSource && !IsListMode;
    public bool ShowList => HasSource && IsListMode;
    public bool IsDetailMode => !IsListMode;
    [ObservableProperty] private string _sortKey = string.Empty;
    [ObservableProperty] private bool _sortDescending;

    /// <summary>Colonne visibili nella vista Dettaglio (intestazione → visibile).</summary>
    public Dictionary<string, bool> ColumnVisibility { get; } = new()
    {
        ["Auto Increment"] = true, ["Data modifica"] = true, ["Dimensione dati"] = true, ["Engine"] = true, ["Righe"] = true, ["Commento"] = true,
    };

    public event Action? ColumnsChanged;

    public void ToggleColumn(string header)
    {
        if (!ColumnVisibility.TryGetValue(header, out var visible)) return;
        ColumnVisibility[header] = !visible;
        ColumnsChanged?.Invoke();
    }

    public void SortBy(string key)
    {
        if (SortKey == key) SortDescending = !SortDescending;
        else
        {
            SortKey = key;
            SortDescending = key is "Righe" or "Dimensione dati" or "Data modifica";
        }
        ApplyFilter();
    }

    public bool HasSource => Source is not null;
    public bool IsEmpty => VisibleObjects.Count == 0;

    /// <summary>True per Tabelle/Viste (colonne righe, dimensione, engine…); false per funzioni, trigger, eventi e sequenze.</summary>
    public bool IsTableList => Source is null || Source.IsTableCategory;
    public bool IsRoutineList => Source?.SchemaKind == Navifox.Core.Models.SchemaObjectKind.Routine || Source?.Category == "Query salvate";
    public bool IsViewList => Source?.Category == "Viste";
    public string NewLabel => Source?.SchemaKind switch
    {
        Navifox.Core.Models.SchemaObjectKind.Routine => "Nuova funzione",
        Navifox.Core.Models.SchemaObjectKind.Trigger => "Nuovo trigger",
        Navifox.Core.Models.SchemaObjectKind.Event => "Nuovo evento",
        Navifox.Core.Models.SchemaObjectKind.Sequence => "Nuova sequenza",
        _ when Source?.Category == "Query salvate" => "Nuova query",
        _ when Source?.Category == "Backup" => "Nuovo backup",
        _ => "Nuovo"
    };
    public string EditLabel => Source?.SchemaKind switch
    {
        Navifox.Core.Models.SchemaObjectKind.Routine => "Modifica funzione",
        Navifox.Core.Models.SchemaObjectKind.Trigger => "Modifica trigger",
        Navifox.Core.Models.SchemaObjectKind.Event => "Modifica evento",
        Navifox.Core.Models.SchemaObjectKind.Sequence => "Progetta sequenza",
        _ when Source?.Category == "Query salvate" => "Apri query",
        _ when Source?.Category == "Backup" => "Ripristina",
        _ => "Modifica"
    };

    public void Show(TreeNodeViewModel category, IEnumerable<ObjectRowViewModel> rows, string? selectName = null)
    {
        Source = category;
        OnPropertyChanged(nameof(IsTableList));
        OnPropertyChanged(nameof(IsRoutineList));
        OnPropertyChanged(nameof(IsViewList));
        OnPropertyChanged(nameof(NewLabel));
        OnPropertyChanged(nameof(EditLabel));
        ColumnsChanged?.Invoke();
        Title = category.Category ?? "Oggetti";
        Context = category.Profile is null
            ? string.Empty
            : category.Schema is { } schema && schema != category.Database
                ? $"{category.Profile.Name} · {category.Database} · {schema}"
                : $"{category.Profile.Name} · {category.Database}";
        _all = rows.ToList();
        ApplyFilter();
        SelectedObject = selectName is null ? null : VisibleObjects.FirstOrDefault(row => row.Name == selectName);
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowList));
    }

    public void Clear()
    {
        Source = null;
        _all = [];
        Title = "Oggetti";
        Context = "Seleziona un database o la voce «Tabelle» nell’albero.";
        ApplyFilter();
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowList));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnIsSearchOpenChanged(bool value)
    {
        if (!value) SearchText = string.Empty;
    }

    [RelayCommand]
    private void ToggleSearch() => IsSearchOpen = !IsSearchOpen;

    [RelayCommand]
    private void CloseSearch() => IsSearchOpen = false;

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private Task OpenSelectedAsync() => SelectedObject is null ? Task.CompletedTask : _openTable(SelectedObject);

    private bool CanOpenSelected() => SelectedObject is not null;

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private Task ImportAsync() => SelectedObject is null || _wizard is null ? Task.CompletedTask : _wizard(SelectedObject, "import");

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private Task ExportAsync() => SelectedObject is null || _wizard is null ? Task.CompletedTask : _wizard(SelectedObject, "export");

    [RelayCommand]
    private Task RefreshAsync() => _refresh();

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var selected = SelectedObject;
        VisibleObjects.Clear();
        IEnumerable<ObjectRowViewModel> rows = _all.Where(row => row.Matches(query));
        Func<ObjectRowViewModel, IComparable>? key = SortKey switch
        {
            "Nome" => row => row.Name,
            "Righe" => row => row.RowsValue,
            "Dimensione dati" => row => row.DataSizeValue,
            "Data modifica" => row => row.ModifiedValue,
            "Auto Increment" => row => row.AutoIncrementValue,
            "Tipo" => row => row.Kind,
            _ => null
        };
        if (key is not null) rows = SortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        foreach (var row in rows) VisibleObjects.Add(row);
        if (selected is not null && VisibleObjects.Contains(selected)) SelectedObject = selected;
        CountLabel = query.Length == 0
            ? $"{_all.Count} {(_all.Count == 1 ? "oggetto" : "oggetti")}"
            : $"{VisibleObjects.Count} di {_all.Count} oggetti · filtro «{query}»";
        OnPropertyChanged(nameof(IsEmpty));
    }
}
