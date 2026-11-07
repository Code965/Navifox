using Navifox.App.Services;
using Navifox.Core;

namespace Navifox.App.ViewModels;

public partial class TableTabViewModel
{
    public event Action<TableRowViewModel, int>? NewRowsCreated;

    private int FirstWritableColumn() => Columns.Select((c, i) => (c, i)).FirstOrDefault(p => !p.c.IsAutoIncrement && !p.c.IsGenerated).i;

    /// <summary>Freccia giù in fondo ai dati: una sola bozza vuota, senza salvare sul database.</summary>
    public bool AddRowAtEnd(TableRowViewModel? current)
    {
        if (!CanEdit || IsBusy || HasMore || Columns.Count == 0 || (Rows.Count > 0 && !ReferenceEquals(Rows.LastOrDefault(), current))) return false;
        if (current is { IsNew: true } && !current.Cells.Any(c => c.IsAssigned)) return true;
        AddRow();
        return true;
    }

    public GridRowsClipboard? CopyRows(IReadOnlyList<int>? columnIndexes = null, bool allRows = false)
    {
        var columns = columnIndexes?.Where(i => i >= 0 && i < Columns.Count).Distinct().ToArray() ?? Enumerable.Range(0, Columns.Count).ToArray();
        var selected = RowsForCopy().ToHashSet();
        var rows = Rows.Where(r => !r.IsDeleted && (allRows || selected.Contains(r))).ToArray();
        if (columns.Length == 0 || rows.Length == 0) { StatusMessage = "Seleziona le righe da copiare."; return null; }
        return new GridRowsClipboard(columns.Select(i => Columns[i].Name).ToArray(),
            rows.Select(row => columns.Select(i => ClipboardCell.From(row.CellAt(i)?.Value)).ToArray()).ToArray(), $"{_profile.Name} / {Database} / {TableName}");
    }

    public async Task<int> PasteRowsAsync(GridRowsClipboard data)
    {
        if (!EnsureEditable() || Columns.Count == 0) return 0;
        try
        {
            var plan = RowPastePlan.Create(data, Columns, _profile.Kind);
            var conflicts = CountPasteKeyConflicts(plan);
            var warnings = new List<string>();
            if (data.ByPosition) warnings.Add("Il testo non ha intestazioni: le colonne vengono abbinate per posizione dalla cella selezionata.");
            if (plan.IgnoredColumns.Count > 0) warnings.Add("Colonne di origine senza corrispondenza (non verranno incollate): " + string.Join(", ", plan.IgnoredColumns));
            if (plan.RequiredColumns.Count > 0) warnings.Add("Campi obbligatori da compilare prima di salvare: " + string.Join(", ", plan.RequiredColumns));
            if (conflicts > 0) warnings.Add($"{conflicts:N0} righe hanno chiavi già presenti nella pagina o ripetute negli appunti. Non verranno sovrascritte: modifica le chiavi delle nuove righe prima di salvare.");
            if (warnings.Count > 0)
            {
                var mapping = string.Join(", ", plan.Mappings.Take(16).Select(m => data.Columns[m.Source] == m.Name ? m.Name : $"{data.Columns[m.Source]} → {m.Name}"));
                if (!await _dialogs.ConfirmAsync("Incolla righe: controlla l’abbinamento",
                    $"Origine: {data.Source}\nDestinazione: {_profile.Name} / {Database} / {TableName}\n{data.Rows.Length:N0} nuove righe\nColonne: {mapping}\n\n{string.Join("\n\n", warnings)}\n\nLe righe saranno aggiunte alla griglia e resteranno da salvare con ✓ o Ctrl+S.", "Aggiungi alla griglia")) return 0;
            }
            if (IsBusy) { StatusMessage = "Incolla annullato: è in corso un’altra operazione."; return 0; }
            var added = plan.Rows.Select(values =>
            {
                var row = new TableRowViewModel(new object?[Columns.Count], isNew: true);
                foreach (var (column, value) in values) row.CellAt(column)!.SetValue(value);
                return TrackRow(row);
            }).ToArray();
            // Riutilizza l'eventuale bozza appena creata con Freccia giù, senza salvare una riga vuota in più.
            if (CurrentRow is { IsNew: true } empty && !empty.Cells.Any(c => c.IsAssigned)) Rows.Remove(empty);
            foreach (var row in added) Rows.Add(row);
            CurrentRow = added[0];
            RefreshPending();
            StatusMessage = $"{added.Length:N0} righe incollate da verificare · ✓ o Ctrl+S per salvare" +
                (plan.GeneratedColumns.Count > 0 ? " · campi automatici ricalcolati dal database" : ".");
            NewRowsCreated?.Invoke(added[0], FirstWritableColumn());
            return added.Length;
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            StatusMessage = "Impossibile incollare le righe: " + error.Message;
            await _dialogs.ShowMessageAsync("Incolla righe", StatusMessage, isError: true);
            return 0;
        }
    }

    public async Task<int> PasteTextRowsAsync(string text, int startColumn = 0)
    {
        try { return await PasteRowsAsync(RowsClipboardText.Parse(text, ColumnNames, startColumn)); }
        catch (FormatException error) { StatusMessage = error.Message; await _dialogs.ShowMessageAsync("Incolla righe", error.Message, isError: true); return 0; }
    }

    private int CountPasteKeyConflicts(RowPastePlan plan)
    {
        var keys = KeyIndexes();
        if (keys.Length == 0 || keys.Any(i => Columns[i].IsAutoIncrement || Columns[i].IsGenerated) || keys.Any(i => !plan.Mappings.Any(m => m.Target == i))) return 0;
        string Key(IEnumerable<object?> values) => System.Text.Json.JsonSerializer.Serialize(values.Select(v => v is null ? null : TableRowViewModel.Format(v)));
        var known = Rows.Where(r => !r.IsDeleted && (!r.IsNew || r.Cells.Any(c => c.IsAssigned))).Select(r => Key(keys.Select(i => r.CellAt(i)?.Value))).ToHashSet();
        var conflicts = 0;
        foreach (var row in plan.Rows)
            if (keys.All(i => row[i] is not null) && !known.Add(Key(keys.Select(i => row[i])))) conflicts++;
        return conflicts;
    }
}
