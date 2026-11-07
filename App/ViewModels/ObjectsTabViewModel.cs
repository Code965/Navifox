using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MosaicDB.App.ViewModels;

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
    private ObjectRowViewModel? _selectedObject;

    public bool HasSource => Source is not null;
    public bool IsEmpty => VisibleObjects.Count == 0;

    public void Show(TreeNodeViewModel category, IEnumerable<ObjectRowViewModel> rows, string? selectName = null)
    {
        Source = category;
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
    }

    public void Clear()
    {
        Source = null;
        _all = [];
        Title = "Oggetti";
        Context = "Seleziona un database o la voce «Tabelle» nell’albero.";
        ApplyFilter();
        OnPropertyChanged(nameof(HasSource));
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
        foreach (var row in _all.Where(row => row.Matches(query))) VisibleObjects.Add(row);
        if (selected is not null && VisibleObjects.Contains(selected)) SelectedObject = selected;
        CountLabel = query.Length == 0
            ? $"{_all.Count} {(_all.Count == 1 ? "oggetto" : "oggetti")}"
            : $"{VisibleObjects.Count} di {_all.Count} oggetti · filtro «{query}»";
        OnPropertyChanged(nameof(IsEmpty));
    }
}
