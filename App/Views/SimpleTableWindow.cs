using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Navifox.App.ViewModels;
using Navifox.Core.Models;

namespace Navifox.App.Views;

/// <summary>Finestra con una tabella di sola lettura (confronto tra righe, raggruppamento con subtotali…).</summary>
public sealed class SimpleTableWindow : Window
{
    public SimpleTableWindow() : this("", "", [], [], null) { }

    /// <param name="highlight">Righe da evidenziare (es. valori diversi); null = nessuna.</param>
    public SimpleTableWindow(string title, string subtitle, IReadOnlyList<string> headers, IReadOnlyList<object?[]> rows, Func<int, bool>? highlight, IReadOnlyList<bool>? numericColumns = null)
    {
        Title = title;
        Width = Math.Clamp(220 + headers.Count * 170, 520, 1200);
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserSortColumns = false };
        grid.Classes.Add("nf-grid");
        ResultGridBuilder.Rebuild(grid, headers.Select((h, i) => new ColumnInfo(h, numericColumns?.ElementAtOrDefault(i) == true ? "decimal" : "text", true)).ToArray());
        var items = rows.Select(r => new TableRowViewModel(r)).ToArray();
        var index = items.Select((row, n) => (row, n)).ToDictionary(p => p.row, p => p.n);
        if (highlight is not null)
            grid.LoadingRow += (_, e) => e.Row.Classes.Set("diff-row", e.Row.DataContext is TableRowViewModel row && index.TryGetValue(row, out var n) && highlight(n));
        grid.ItemsSource = items;
        var close = new Button { Content = "Chiudi", Classes = { "small-muted" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        close.Click += (_, _) => Close();
        var header = new TextBlock { Text = subtitle, Margin = new Thickness(14, 12, 14, 8), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(close);
        root.Children.Add(new Border { Margin = new Thickness(14, 0), Child = grid });
        Content = root;
        Opened += (_, _) => ResultGridBuilder.FitToContent(grid, items);
    }
}
