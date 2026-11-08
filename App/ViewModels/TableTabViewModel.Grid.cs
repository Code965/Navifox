using CommunityToolkit.Mvvm.Input;

namespace Navifox.App.ViewModels;

/// <summary>Annulla a più passi, modifica multipla, confronto di righe e raggruppamento con subtotali.</summary>
public partial class TableTabViewModel
{
    // ───────────── Annulla (più passi, fino al salvataggio) ─────────────

    private readonly Stack<List<Action>> _undo = new();
    private List<Action>? _undoGroup;
    private int _groupDepth;
    private bool _undoing;
    /// <summary>Ultima cella registrata: le digitazioni successive nella stessa cella non aggiungono passi.</summary>
    private CellViewModel? _lastUndoCell;

    /// <summary>La vista lo chiama quando l'utente passa a un'altra cella: la prossima modifica è un nuovo passo.</summary>
    public void EndCellUndoStep() => _lastUndoCell = null;

    public bool CanUndo => _undo.Count > 0;

    private void RecordUndo(Action undo)
    {
        if (_undoing) return;
        if (_undoGroup is not null) { _undoGroup.Add(undo); _lastUndoCell = null; return; }
        _undo.Push([undo]);
        if (_undo.Count > 500) TrimUndo();
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Raggruppa più modifiche in un solo passo di «Annulla» (incolla, modifica multipla, elimina righe).</summary>
    private IDisposable BeginUndoGroup()
    {
        if (_groupDepth++ == 0) _undoGroup = [];
        return new UndoScope(this);
    }

    private sealed class UndoScope(TableTabViewModel owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--owner._groupDepth > 0) return;
            var group = owner._undoGroup;
            owner._undoGroup = null;
            if (group is { Count: > 0 }) { owner._undo.Push(group); owner.OnPropertyChanged(nameof(CanUndo)); }
        }
    }

    private void TrimUndo()
    {
        var keep = _undo.Take(400).Reverse().ToArray();
        _undo.Clear();
        foreach (var step in keep) _undo.Push(step);
    }

    private void ClearUndo()
    {
        _undo.Clear();
        _lastUndoCell = null;
        OnPropertyChanged(nameof(CanUndo));
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0) { StatusMessage = "Niente da annullare."; return; }
        var step = _undo.Pop();
        _lastUndoCell = null;
        _undoing = true;
        try { for (var i = step.Count - 1; i >= 0; i--) step[i](); }
        finally { _undoing = false; }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CurrentCellText));
        RefreshPending();
        StatusMessage = _undo.Count == 0 ? "Modifica annullata · nessun altro passo." : $"Modifica annullata · {_undo.Count} passi ancora annullabili (Ctrl+Z).";
    }

    // ───────────── Modifica multipla ─────────────

    /// <summary>Scrive lo stesso valore in tutte le celle indicate (righe × colonne). «NULL» imposta NULL. Un solo passo di Annulla.</summary>
    public async Task MultiEditAsync(IReadOnlyList<TableRowViewModel> rows, IReadOnlyList<int> columns)
    {
        if (!EnsureEditable()) return;
        var editable = columns.Where(i => i >= 0 && i < Columns.Count && !Columns[i].IsGenerated && !Columns[i].IsAutoIncrement).ToArray();
        var targets = rows.Where(r => !r.IsDeleted).ToArray();
        if (editable.Length == 0 || targets.Length == 0) { StatusMessage = "Seleziona più righe (o una colonna intera) di una colonna modificabile."; return; }
        var names = string.Join(", ", editable.Select(i => Columns[i].Name));
        var first = targets[0].CellAt(editable[0]);
        var value = await _dialogs.PromptAsync("Modifica multipla",
            $"Nuovo valore per {names} in {targets.Length:N0} righe.\nScrivi NULL per impostare NULL. La modifica resta da salvare con ✓ (Ctrl+Z per annullare).",
            first is { IsNull: false } ? first.Text : string.Empty, "Applica");
        if (value is null) return;
        using (BeginUndoGroup())
            foreach (var row in targets)
                foreach (var column in editable)
                {
                    var cell = row.CellAt(column);
                    if (cell is null) continue;
                    if (value == "NULL") cell.SetNull();
                    else cell.EditText = value;
                }
        OnPropertyChanged(nameof(CurrentCellText));
        RefreshPending();
        StatusMessage = $"{targets.Length * editable.Length:N0} celle modificate · ✓ o Ctrl+S per salvare, Ctrl+Z per annullare.";
    }

    // ───────────── Confronto tra due righe ─────────────

    /// <summary>Colonna, valore della prima riga, valore della seconda, uguali?</summary>
    public IReadOnlyList<(string Column, string First, string Second, bool Same)> CompareRows(TableRowViewModel first, TableRowViewModel second) =>
        Columns.Select((column, i) =>
        {
            var a = first.IsNull(i) ? "(NULL)" : first.TextAt(i);
            var b = second.IsNull(i) ? "(NULL)" : second.TextAt(i);
            return (column.Name, a, b, a == b);
        }).ToArray();

    // ───────────── Raggruppamento con subtotali ─────────────

    /// <summary>Gruppi della colonna (righe caricate): valore, numero di righe e somma di ogni colonna numerica.</summary>
    public (IReadOnlyList<string> Headers, IReadOnlyList<object?[]> Rows) GroupBy(int column)
    {
        var numeric = Enumerable.Range(0, Columns.Count)
            .Where(i => i != column && Views.ResultGridBuilder.IsNumeric(Columns[i].DataType) && !Columns[i].IsPrimaryKey && !Columns[i].IsAutoIncrement)
            .ToArray();
        var headers = new List<string> { Columns[column].Name, "Righe" };
        headers.AddRange(numeric.Select(i => $"Somma {Columns[i].Name}"));
        var rows = Rows.Where(r => !r.IsDeleted)
            .GroupBy(r => r.IsNull(column) ? "(NULL)" : r.TextAt(column))
            .Select(g =>
            {
                var values = new List<object?> { g.Key, (long)g.Count() };
                values.AddRange(numeric.Select(i => (object?)g.Sum(r => Number(r.ValueAt(i)))));
                return values.ToArray();
            })
            .OrderByDescending(r => (long)r[1]!)
            .ToList();
        var total = new List<object?> { "Totale", (long)Rows.Count(r => !r.IsDeleted) };
        total.AddRange(numeric.Select(i => (object?)Rows.Where(r => !r.IsDeleted).Sum(r => Number(r.ValueAt(i)))));
        rows.Add(total.ToArray());
        return (headers, rows);
    }

    private static decimal Number(object? value)
    {
        try
        {
            return value switch
            {
                null or DBNull or bool => 0,
                string text => decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0,
                double d when !double.IsFinite(d) => 0,
                _ => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        catch (Exception error) when (error is InvalidCastException or OverflowException or FormatException) { return 0; }
    }
}
