using Avalonia.Controls;

namespace Navifox.App.Views;

/// <summary>
/// Colonne bloccate a sinistra (FrozenColumnCount della DataGrid). Se la griglia è già scorsa in orizzontale
/// quando si bloccano le colonne, la DataGrid le disegna male: prima si torna alla prima colonna.
/// </summary>
public static class FrozenColumns
{
    public static int Get(DataGrid grid) => grid.FrozenColumnCount;

    /// <summary>Blocca le prime <paramref name="count"/> colonne (in ordine di visualizzazione); 0 = nessuna.</summary>
    public static void Set(DataGrid grid, int count)
    {
        count = Math.Clamp(count, 0, grid.Columns.Count);
        if (grid.FrozenColumnCount == count) return;
        var row = grid.SelectedItem ?? (grid.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().FirstOrDefault();
        var first = grid.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex).FirstOrDefault();
        if (row is not null && first is not null) grid.ScrollIntoView(row, first);
        grid.FrozenColumnCount = count;
    }
}
