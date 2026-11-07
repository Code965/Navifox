using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Navifox.App.ViewModels;
using Navifox.Core.Models;

namespace Navifox.App.Views;

/// <summary>Costruisce le colonne dinamiche di una griglia risultati (tabelle e query).</summary>
public static class ResultGridBuilder
{
    private static readonly string[] NumericTypes = ["int", "decimal", "numeric", "float", "double", "real", "serial", "money", "bit", "year"];

    public static bool IsNumeric(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        return NumericTypes.Any(type.Contains) && !type.Contains("interval") && !type.Contains("point");
    }

    /// <summary>
    /// Allarga le colonne in base al contenuto delle prime righe (come il doppio clic sul bordo dell'intestazione):
    /// i testi brevi restano stretti, quelli lunghi arrivano al massimo a 420 px.
    /// </summary>
    public static void FitToContent(DataGrid grid, IEnumerable<TableRowViewModel> rows)
    {
        var sample = rows.Take(200).ToArray();
        if (sample.Length == 0) return;
        foreach (var column in grid.Columns)
        {
            if (column.Tag is not int index) continue;
            var longest = 0;
            foreach (var row in sample)
            {
                if (index >= row.CellCount) continue;
                var text = row.IsNull(index) ? "(NULL)" : row.TextAt(index);
                var length = text.IndexOfAny(['\r', '\n']) is var newline and >= 0 ? newline : text.Length;
                if (length > longest) longest = Math.Min(length, 70);
            }
            var header = HeaderText(column).Length + (column.Header is ColumnHeaderContent ? 2 : 0);
            var width = Math.Clamp(Math.Max(longest * 7.4 + 24, header * 8.5 + 44), 60, 420);
            if (width > column.ActualWidth + 4 || column.ActualWidth <= 0) column.Width = new DataGridLength(width);
        }
    }

    /// <summary>Indice della colonna selezionata (clic sull'intestazione): le sue celle si evidenziano. -1 = nessuna.</summary>
    public static readonly Avalonia.AttachedProperty<int> HighlightColumnProperty =
        Avalonia.AvaloniaProperty.RegisterAttached<DataGrid, int>("HighlightColumn", typeof(ResultGridBuilder), -1);

    /// <summary>Altra estremità dell'intervallo di colonne selezionate (Maiusc+frecce o Maiusc+clic); -1 = solo HighlightColumn.</summary>
    public static readonly Avalonia.AttachedProperty<int> HighlightEndProperty =
        Avalonia.AvaloniaProperty.RegisterAttached<DataGrid, int>("HighlightEnd", typeof(ResultGridBuilder), -1);

    public static int GetHighlightColumn(DataGrid grid) => grid.GetValue(HighlightColumnProperty);
    public static void SetHighlightColumn(DataGrid grid, int value)
    {
        grid.SetValue(HighlightEndProperty, value);
        grid.SetValue(HighlightColumnProperty, value);
    }

    public static int GetHighlightEnd(DataGrid grid) => grid.GetValue(HighlightEndProperty);
    public static void SetHighlightEnd(DataGrid grid, int value) => grid.SetValue(HighlightEndProperty, value);

    /// <summary>Colonne selezionate in ordine (vuoto se nessuna).</summary>
    public static IReadOnlyList<int> HighlightedColumns(DataGrid grid)
    {
        var start = GetHighlightColumn(grid);
        if (start < 0) return [];
        var end = GetHighlightEnd(grid) < 0 ? start : GetHighlightEnd(grid);
        return Enumerable.Range(Math.Min(start, end), Math.Abs(end - start) + 1).ToArray();
    }

    /// <summary>Mostra o nasconde la riga del filtro rapido sotto le intestazioni.</summary>
    public static void SetFilterRowVisible(DataGrid grid, bool visible)
    {
        foreach (var column in grid.Columns)
            if (column.Header is ColumnHeaderContent { FilterBox: { } box }) box.IsVisible = visible;
    }

    /// <summary>Svuota tutte le caselle del filtro rapido (senza notificare).</summary>
    public static void ClearFilters(DataGrid grid)
    {
        foreach (var column in grid.Columns)
            if (column.Header is ColumnHeaderContent { FilterBox: { } box }) box.SetTextSilently(string.Empty);
    }

    /// <summary>Testo dell'intestazione (con eventuali simboli PK, filtro, ordinamento).</summary>
    public static string HeaderText(DataGridColumn column) => column.Header switch
    {
        ColumnHeaderContent content => content.Text,
        string text => text,
        _ => string.Empty
    };

    public static void SetHeaderText(DataGridColumn column, string text)
    {
        if (column.Header is ColumnHeaderContent content) content.Text = text;
        else column.Header = text;
    }

    /// <param name="onHeaderClick">Clic sull'intestazione (seleziona la colonna); null = comportamento standard.</param>
    /// <param name="onSort">Freccina dell'intestazione: true = crescente, false = decrescente, null = rimuovi ordinamento.</param>
    /// <param name="filterText">Testo iniziale del filtro rapido della colonna.</param>
    /// <param name="onFilter">Filtro rapido modificato (dopo una breve pausa o con Invio); null = nessuna riga di filtro.</param>
    public static void Rebuild(DataGrid grid, IReadOnlyList<ColumnInfo> columns, bool editable = false, Func<int, string?>? headerGlyph = null,
        Action<int, bool>? onHeaderClick = null, Action<int, bool?>? onSort = null, Func<int, string>? filterText = null, Action<int, string>? onFilter = null)
    {
        grid.Columns.Clear();
        SetHighlightColumn(grid, -1);
        for (var index = 0; index < columns.Count; index++)
        {
            var cellIndex = index;
            var column = columns[index];
            var numeric = IsNumeric(column.DataType);
            var glyph = headerGlyph?.Invoke(index);
            var gridColumn = new DataGridTemplateColumn
            {
                Header = onHeaderClick is null && onSort is null && onFilter is null
                    ? glyph is null ? column.Name : $"{column.Name}  {glyph}"
                    : new ColumnHeaderContent(glyph is null ? column.Name : $"{column.Name}  {glyph}", cellIndex, onHeaderClick, onSort),
                Tag = index,
                SortMemberPath = column.Name,
                CanUserSort = onSort is null,
                CellTemplate = new FuncDataTemplate<TableRowViewModel>((row, _) => CreateDisplay(row?.CellAt(cellIndex), numeric, cellIndex), supportsRecycling: false),
                IsReadOnly = !editable || column.IsGenerated,
                Width = new DataGridLength(Math.Clamp((column.Name.Length + (glyph?.Length + 2 ?? 0)) * 8.5 + 44, 70, 240))
            };
            if (onFilter is not null && gridColumn.Header is ColumnHeaderContent header)
                header.AddFilter(filterText?.Invoke(cellIndex) ?? string.Empty, numeric, text => onFilter(cellIndex, text));
            if (editable && !column.IsGenerated)
                gridColumn.CellEditingTemplate = new FuncDataTemplate<TableRowViewModel>((row, _) => CreateEditor(row?.CellAt(cellIndex), numeric), supportsRecycling: false);
            grid.Columns.Add(gridColumn);
        }
    }

    private static Control CreateDisplay(CellViewModel? cell, bool numeric, int columnIndex)
    {
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Avalonia.Thickness(6, 0)
        };
        if (cell is null) return text;
        text.Bind(TextBlock.TextProperty, new Binding(nameof(CellViewModel.Text)) { Source = cell });
        var border = new Border { Child = text, Classes = { "cell" } };
        void Sync()
        {
            text.Classes.Set("null-cell", cell.IsNull);
            text.FontStyle = cell.IsNull ? FontStyle.Italic : FontStyle.Normal;
            border.Classes.Set("modified", cell.IsModified);
        }
        Sync();
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => Sync();
        IDisposable? highlight = null;
        border.AttachedToVisualTree += (_, _) =>
        {
            cell.PropertyChanged -= OnChanged;
            cell.PropertyChanged += OnChanged;
            Sync();
            // Colonna selezionata dall'intestazione: la cella segue l'indice salvato sulla griglia.
            highlight?.Dispose();
            if (border.FindAncestorOfType<DataGrid>() is { } grid)
            {
                void Refresh(int _)
                {
                    var start = GetHighlightColumn(grid);
                    var end = GetHighlightEnd(grid) < 0 ? start : GetHighlightEnd(grid);
                    var selected = start >= 0 && columnIndex >= Math.Min(start, end) && columnIndex <= Math.Max(start, end);
                    (border.FindAncestorOfType<DataGridCell>() as Avalonia.StyledElement ?? border).Classes.Set("col-selected", selected);
                }
                var first = grid.GetObservable(HighlightColumnProperty).Subscribe(new Avalonia.Reactive.AnonymousObserver<int>(Refresh));
                var second = grid.GetObservable(HighlightEndProperty).Subscribe(new Avalonia.Reactive.AnonymousObserver<int>(Refresh));
                highlight = new CompositeDisposable(first, second);
            }
        };
        border.DetachedFromVisualTree += (_, _) =>
        {
            cell.PropertyChanged -= OnChanged;
            highlight?.Dispose();
            highlight = null;
        };
        return border;
    }

    private static Control CreateEditor(CellViewModel? cell, bool numeric)
    {
        var box = new TextBox
        {
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(5, 0),
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Watermark = cell?.IsNull == true ? "(NULL)" : null
        };
        if (cell is not null) box.Bind(TextBox.TextProperty, new Binding(nameof(CellViewModel.EditText)) { Source = cell, Mode = BindingMode.TwoWay });
        return box;
    }
}


/// <summary>
/// Intestazione di colonna: clic sul nome = seleziona la colonna; freccina = menu Ordina crescente / decrescente / Rimuovi ordinamento.
/// </summary>
public sealed class ColumnHeaderContent : Grid
{
    private readonly TextBlock _label;

    public ColumnHeaderContent(string text, int index, Action<int>? onClick, Action<int, bool?>? onSort) : this(text, index, onClick is null ? null : (i, _) => onClick(i), onSort) { }

    public ColumnHeaderContent(string text, int index, Action<int, bool>? onClick, Action<int, bool?>? onSort)
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Background = Avalonia.Media.Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        _label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Children.Add(_label);
        if (onClick is not null)
            PointerReleased += (_, args) =>
            {
                if (args.InitialPressMouseButton != Avalonia.Input.MouseButton.Left || args.Source is Visual source
                    && (source.FindAncestorOfType<Button>(includeSelf: true) is not null || source.FindAncestorOfType<QuickFilterBox>(includeSelf: true) is not null)) return;
                onClick(index, args.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift));
                args.Handled = true;
            };
        if (onSort is null) return;
        var arrow = new Button
        {
            Content = new Controls.NfIcon { Kind = "chevron-down", Width = 11, Height = 11 },
            Padding = new Avalonia.Thickness(0),
            Width = 18,
            Height = 18,
            MinHeight = 0,
            MinWidth = 0,
            CornerRadius = new Avalonia.CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(6, 0, 0, 0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.55,
            [ToolTip.TipProperty] = "Ordina",
        };
        arrow.Classes.Add("header-sort");
        var flyout = new MenuFlyout { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft };
        MenuItem Item(string header, string icon, bool? descending)
        {
            var item = new MenuItem { Header = header, Icon = new Controls.NfIcon { Kind = icon, Width = 14, Height = 14 } };
            item.Click += (_, _) => onSort(index, descending);
            return item;
        }
        flyout.Items.Add(Item("Ordina crescente", "arrow-up", false));
        flyout.Items.Add(Item("Ordina decrescente", "arrow-down", true));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("Rimuovi ordinamento", "x", null));
        arrow.Flyout = flyout;
        PointerEntered += (_, _) => arrow.Opacity = 1;
        PointerExited += (_, _) => arrow.Opacity = 0.55;
        Grid.SetColumn(arrow, 1);
        Children.Add(arrow);
    }

    public string Text
    {
        get => _label.Text ?? string.Empty;
        set => _label.Text = value;
    }

    public QuickFilterBox? FilterBox { get; private set; }

    /// <summary>Seconda riga dell'intestazione: casella del filtro rapido.</summary>
    public void AddFilter(string text, bool numeric, Action<string> changed)
    {
        RowDefinitions = new RowDefinitions("Auto,Auto");
        FilterBox = new QuickFilterBox(text, numeric, changed) { IsVisible = Services.AppSettings.Current.QuickFilterRow };
        Grid.SetRow(FilterBox, 1);
        Grid.SetColumnSpan(FilterBox, 2);
        Children.Add(FilterBox);
    }
}

/// <summary>
/// Casella del filtro rapido: applica dopo una breve pausa di digitazione o con Invio; Esc svuota.
/// Sintassi: testo (contiene), Roma*, =x, !x, &gt;100, &lt;=5, 10..20, null, !null.
/// </summary>
public sealed class QuickFilterBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    private readonly Avalonia.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Action<string> _changed;
    private string _applied;
    private bool _silent;

    public QuickFilterBox(string text, bool numeric, Action<string> changed)
    {
        _changed = changed;
        _applied = text;
        Text = text;
        Classes.Add("quick-filter");
        Watermark = numeric ? "> < =" : "filtra…";
        ToolTip.SetTip(this, "Filtro rapido: testo (contiene), Roma*, =valore, !valore, >100, <=5, 10..20, null, !null\nInvio applica subito · Esc svuota");
        _timer.Tick += (_, _) => Apply();
        PropertyChanged += (_, args) =>
        {
            if (args.Property != TextProperty || _silent) return;
            Classes.Set("active", !string.IsNullOrWhiteSpace(Text));
            _timer.Stop();
            _timer.Start();
        };
        Classes.Set("active", !string.IsNullOrWhiteSpace(text));
        KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter) { Apply(); args.Handled = true; }
            else if (args.Key == Avalonia.Input.Key.Escape && !string.IsNullOrEmpty(Text)) { Text = string.Empty; Apply(); args.Handled = true; }
        };
        LostFocus += (_, _) => Apply();
    }

    public void SetTextSilently(string text)
    {
        _silent = true;
        Text = text;
        _applied = text;
        Classes.Set("active", !string.IsNullOrWhiteSpace(text));
        _silent = false;
    }

    private void Apply()
    {
        _timer.Stop();
        var text = (Text ?? string.Empty).Trim();
        if (text == _applied.Trim()) return;
        _applied = text;
        _changed(text);
    }
}


internal sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
{
    public void Dispose()
    {
        foreach (var item in items) item.Dispose();
    }
}
