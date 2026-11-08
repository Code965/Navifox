using CommunityToolkit.Mvvm.ComponentModel;

namespace Navifox.App.ViewModels;

/// <summary>Una cella della griglia: valore originale, valore corrente e stato di modifica.</summary>
/// <summary>Stato di una cella prima di una modifica (per «Annulla» a più passi).</summary>
public sealed record CellSnapshot(object? Value, string Text, bool IsNull, bool IsModified, bool IsAssigned);

public partial class CellViewModel : ObservableObject
{
    /// <summary>Sta per cambiare valore (digitazione, NULL, incolla): riceve lo stato precedente.</summary>
    public event Action<CellViewModel, CellSnapshot>? Changing;

    private CellSnapshot Snapshot() => new(Value, Text, IsNull, IsModified, IsAssigned);

    partial void OnTextChanging(string value)
    {
        if (!_suppress) Changing?.Invoke(this, Snapshot());
    }

    /// <summary>Ripristina lo stato salvato (Annulla).</summary>
    public void Restore(CellSnapshot snapshot)
    {
        _suppress = true;
        Value = snapshot.Value;
        Text = snapshot.Text;
        IsNull = snapshot.IsNull;
        IsModified = snapshot.IsModified;
        IsAssigned = snapshot.IsAssigned;
        _suppress = false;
        OnPropertyChanged(nameof(EditText));
    }

    private object? _original;
    private bool _suppress;

    public CellViewModel(object? value)
    {
        _original = value;
        Value = value;
        _text = TableRowViewModel.Format(value);
        _isNull = value is null;
    }

    /// <summary>Valore corrente: l'oggetto letto dal database, oppure la stringa digitata dall'utente.</summary>
    public object? Value { get; private set; }
    public object? OriginalValue => _original;

    [ObservableProperty] private string _text;
    [ObservableProperty] private bool _isNull;
    [ObservableProperty] private bool _isModified;
    /// <summary>Per una riga nuova distingue NULL esplicito da un campo lasciato al valore predefinito.</summary>
    [ObservableProperty] private bool _isAssigned;

    /// <summary>Testo da mostrare in modifica: vuoto per i NULL.</summary>
    public string EditText
    {
        get => IsNull ? string.Empty : Text;
        set
        {
            if (IsNull && value.Length == 0) return;
            Text = value;
        }
    }

    partial void OnTextChanged(string value)
    {
        if (_suppress) return;
        IsAssigned = true;
        Value = value;
        IsNull = false;
        IsModified = !Equals(TableRowViewModel.Format(_original), value) || _original is null;
        OnPropertyChanged(nameof(EditText));
    }

    public void SetNull() => SetValue(null);

    public void SetValue(object? value)
    {
        Changing?.Invoke(this, Snapshot());
        _suppress = true;
        Value = value is byte[] bytes ? bytes.ToArray() : value;
        Text = TableRowViewModel.Format(Value);
        IsNull = Value is null;
        IsAssigned = true;
        IsModified = !(_original is byte[] original && Value is byte[] current ? original.SequenceEqual(current) : Equals(_original, Value));
        _suppress = false;
        OnPropertyChanged(nameof(EditText));
    }

    public void Revert()
    {
        _suppress = true;
        Value = _original;
        Text = TableRowViewModel.Format(_original);
        IsNull = _original is null;
        IsModified = false;
        IsAssigned = false;
        _suppress = false;
        OnPropertyChanged(nameof(EditText));
    }

    public void AcceptChanges()
    {
        _original = Value;
        IsModified = false;
        IsAssigned = false;
    }
}

public enum RowState { Unchanged, Modified, Added, Deleted }

public partial class TableRowViewModel : ObservableObject
{
    // Valori grezzi letti dal database; le celle (oggetti osservabili) vengono create solo quando servono,
    // così anche centinaia di migliaia di righe restano leggere in memoria.
    private readonly object?[] _values;
    private readonly CellViewModel?[] _cells;

    public TableRowViewModel(IEnumerable<object?> values, bool isNew = false)
    {
        _values = values as object?[] ?? values.ToArray();
        _cells = new CellViewModel?[_values.Length];
        _isNew = isNew;
        if (isNew) _state = RowState.Added;
    }

    /// <summary>Tutte le celle (le crea se necessario).</summary>
    public IReadOnlyList<CellViewModel> Cells => Enumerable.Range(0, _values.Length).Select(index => CellAt(index)!).ToArray();
    public int CellCount => _values.Length;
    public IReadOnlyList<object?> Values => Enumerable.Range(0, _values.Length).Select(index => _cells[index]?.Value ?? (_cells[index] is null ? _values[index] : null)).ToArray();

    [ObservableProperty] private RowState _state;
    [ObservableProperty] private bool _isDeleted;
    private bool _isNew;

    public bool IsNew => _isNew;
    public bool HasChanges => State != RowState.Unchanged;
    public string StateGlyph => State switch { RowState.Added => "✱", RowState.Modified => "✎", RowState.Deleted => "✕", _ => string.Empty };

    public CellViewModel? CellAt(int index)
    {
        if (index < 0 || index >= _values.Length) return null;
        if (_cells[index] is { } existing) return existing;
        var cell = new CellViewModel(_values[index]);
        cell.Changing += (changed, before) => CellChanging?.Invoke(this, changed, before);
        cell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CellViewModel.IsModified)) RefreshState();
        };
        _cells[index] = cell;
        return cell;
    }

    /// <summary>Valore attuale (modificato o letto dal database) senza creare la cella.</summary>
    public object? ValueAt(int index) => index < 0 || index >= _values.Length ? null : _cells[index] is { } cell ? cell.Value : _values[index];

    public bool IsNull(int index) => _cells.ElementAtOrDefault(index) is { } cell ? cell.IsNull : index < _values.Length && _values[index] is null;
    public string TextAt(int index) => _cells.ElementAtOrDefault(index) is { } cell ? cell.Text : index < _values.Length ? Format(_values[index]) : string.Empty;

    partial void OnIsDeletedChanged(bool value) => RefreshState();
    partial void OnIsDeletedChanging(bool value) => DeletedChanging?.Invoke(this, !value);

    /// <summary>Una cella della riga sta per cambiare (riga, cella, stato precedente).</summary>
    public event Action<TableRowViewModel, CellViewModel, CellSnapshot>? CellChanging;
    /// <summary>La riga sta per essere contrassegnata (o non più) per l'eliminazione: riceve il valore precedente.</summary>
    public event Action<TableRowViewModel, bool>? DeletedChanging;
    partial void OnStateChanged(RowState value) => OnPropertyChanged(nameof(StateGlyph));

    public void Revert()
    {
        foreach (var cell in _cells) cell?.Revert();
        IsDeleted = false;
    }

    public void AcceptChanges()
    {
        foreach (var cell in _cells) cell?.AcceptChanges();
        _isNew = false;
        RefreshState();
    }

    private void RefreshState() => State = IsDeleted ? RowState.Deleted : _isNew ? RowState.Added : _cells.Any(cell => cell?.IsModified == true) ? RowState.Modified : RowState.Unchanged;

    public static string Format(object? value) => value switch
    {
        null => "(NULL)",
        byte[] bytes => $"(BLOB) {bytes.Length:N0} byte",
        DateTime dateTime => dateTime.TimeOfDay == TimeSpan.Zero ? dateTime.ToString("yyyy-MM-dd") : dateTime.ToString("yyyy-MM-dd HH:mm:ss"),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss zzz"),
        bool flag => flag ? "1" : "0",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}
