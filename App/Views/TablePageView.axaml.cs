using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Navifox.App.ViewModels;

namespace Navifox.App.Views;

public partial class TablePageView : UserControl
{
    private TableTabViewModel? _viewModel;

    public TablePageView()
    {
        InitializeComponent();
        ResultGrid.Sorting += OnSorting;
        ResultGrid.CurrentCellChanged += (_, _) => SyncCurrent();
        ResultGrid.SelectionChanged += (_, _) =>
        {
            if (_viewModel is null) return;
            _viewModel.SelectedRows = ResultGrid.SelectedItems.OfType<TableRowViewModel>().ToArray();
            SyncCurrent();
        };
        ResultGrid.LoadingRow += OnLoadingRow;
        ResultGrid.BeginningEdit += (_, args) =>
        {
            if (_viewModel?.CanEdit != true) args.Cancel = true;
        };
        ResultGrid.CellEditEnded += (_, _) => _viewModel?.SetCurrent(ResultGrid.SelectedItem as TableRowViewModel, CurrentIndex());
        ResultGrid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        // Clic sull'indicatore a sinistra = riga intera; clic su una cella = torna alla selezione di cella.
        ResultGrid.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (args.Source is not Avalonia.Visual source) return;
            if (source.FindAncestorOfType<DataGridRowHeader>(includeSelf: true) is not null) SetSelectionKind(SelectionKind.Row);
            else if (source.FindAncestorOfType<DataGridCell>(includeSelf: true) is not null) SetSelectionKind(SelectionKind.Cell);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Columns.CollectionChanged -= OnColumnsChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.SearchHit -= OnSearchHit;
        }
        base.OnDataContextChanged(e);
        _viewModel = DataContext as TableTabViewModel;
        if (_viewModel is null) return;
        _viewModel.Columns.CollectionChanged += OnColumnsChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.SearchHit += OnSearchHit;
        RebuildColumns();
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildColumns();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TableTabViewModel.IsSortActive) or nameof(TableTabViewModel.IsFilterActive) or nameof(TableTabViewModel.SqlPreview)) RefreshHeaders();
        if (e.PropertyName == nameof(TableTabViewModel.CanEdit)) RebuildColumns();
        if (e.PropertyName == nameof(TableTabViewModel.IsSearchOpen) && _viewModel?.IsSearchOpen == true)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SearchBox.Focus(), Avalonia.Threading.DispatcherPriority.Input);
        if (e.PropertyName == nameof(TableTabViewModel.CurrentRow) && _viewModel?.IsFormView == true && !ReferenceEquals(ResultGrid.SelectedItem, _viewModel.CurrentRow))
            ResultGrid.SelectedItem = _viewModel.CurrentRow;
        // Nuova pagina di righe: le colonne si adattano al contenuto.
        if (e.PropertyName == nameof(TableTabViewModel.Rows))
        {
            _fitted = false;
            ScheduleFit();
        }
    }

    private void OnSearchHit(TableRowViewModel row, int column)
    {
        ResultGrid.SelectedItem = row;
        if (ResultGrid.Columns.FirstOrDefault(c => c.Tag is int i && i == column) is { } gridColumn)
        {
            ResultGrid.CurrentColumn = gridColumn;
            ResultGrid.ScrollIntoView(row, gridColumn);
        }
    }

    private bool _fitted;

    private void RebuildColumns()
    {
        if (_viewModel is null) return;
        ResultGridBuilder.Rebuild(ResultGrid, _viewModel.Columns, _viewModel.CanEdit, HeaderGlyph, SelectColumn, SortColumn);
        _selectionMode = SelectionKind.Cell;
        ResultGrid.Classes.Remove("row-mode");
        ResultGrid.Classes.Remove("col-mode");
        _fitted = false;
        ScheduleFit();
    }


    private void ScheduleFit()
    {
        if (_fitted || _viewModel is not { Rows.Count: > 0 }) return;
        _fitted = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel is not null) ResultGridBuilder.FitToContent(ResultGrid, _viewModel.Rows);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void RefreshHeaders()
    {
        if (_viewModel is null) return;
        foreach (var column in ResultGrid.Columns)
        {
            if (column.Tag is not int index || index >= _viewModel.Columns.Count) continue;
            var name = _viewModel.Columns[index].Name;
            var glyph = HeaderGlyph(index);
            ResultGridBuilder.SetHeaderText(column, glyph is null ? name : $"{name}  {glyph}");
        }
    }

    private string? HeaderGlyph(int index)
    {
        if (_viewModel is null || index >= _viewModel.Columns.Count) return null;
        var column = _viewModel.Columns[index];
        var parts = new List<string>();
        if (column.IsPrimaryKey) parts.Add("PK");
        if (_viewModel.IsFiltered(column.Name)) parts.Add("⛛");
        if (_viewModel.SortGlyphFor(column.Name) is { } sort) parts.Add(sort);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (_viewModel is null || e.Column.Tag is not int index || index >= _viewModel.Columns.Count) return;
        _ = _viewModel.SortByColumnAsync(_viewModel.Columns[index].Name);
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not TableRowViewModel row) return;
        var glyph = new TextBlock { FontSize = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        glyph.Classes.Add("row-state");
        glyph.Bind(TextBlock.TextProperty, new Binding(nameof(TableRowViewModel.StateGlyph)) { Source = row });
        e.Row.Header = glyph;
        e.Row.Bind(OpacityProperty, new Binding(nameof(TableRowViewModel.IsDeleted)) { Source = row, Converter = DeletedOpacity });
    }

    private static readonly Avalonia.Data.Converters.IValueConverter DeletedOpacity =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(deleted => deleted ? 0.4 : 1);

    private enum SelectionKind { Cell, Row, Column }
    private SelectionKind _selectionMode;

    private void SetSelectionKind(SelectionKind kind)
    {
        _selectionMode = kind;
        ResultGrid.Classes.Set("row-mode", kind == SelectionKind.Row);
        ResultGrid.Classes.Set("col-mode", kind == SelectionKind.Column);
        if (kind != SelectionKind.Column) ResultGridBuilder.SetHighlightColumn(ResultGrid, -1);
    }

    /// <summary>
    /// Clic sull'intestazione: seleziona l'intera colonna; Maiusc+clic o Maiusc+←/→ estendono la selezione ad altre colonne
    /// (Ctrl+C copia tutti i valori). ←/→ senza Maiusc spostano la colonna selezionata.
    /// </summary>
    private void SelectColumn(int index, bool extend = false)
    {
        var extending = extend && _selectionMode == SelectionKind.Column && ResultGridBuilder.GetHighlightColumn(ResultGrid) >= 0;
        SetSelectionKind(SelectionKind.Column);
        ResultGrid.SelectedItem = null;
        if (extending) ResultGridBuilder.SetHighlightEnd(ResultGrid, index);
        else ResultGridBuilder.SetHighlightColumn(ResultGrid, index);
        ReportColumnSelection();
        ResultGrid.Focus();
    }

    private void ReportColumnSelection()
    {
        if (_viewModel is null) return;
        var columns = ResultGridBuilder.HighlightedColumns(ResultGrid).Where(i => i < _viewModel.Columns.Count).ToArray();
        if (columns.Length == 0) return;
        var label = columns.Length == 1 ? $"Colonna «{_viewModel.Columns[columns[0]].Name}»" : $"{columns.Length} colonne ({_viewModel.Columns[columns[0]].Name} … {_viewModel.Columns[columns[^1]].Name})";
        _viewModel.StatusMessage = $"{label} selezionat{(columns.Length == 1 ? "a" : "e")} · {_viewModel.Rows.Count:N0} righe · Ctrl+C per copiare · Maiusc+←/→ per estendere";
    }

    /// <summary>Frecce in modalità colonna: ←/→ sposta, Maiusc+←/→ estende l'intervallo.</summary>
    private bool HandleColumnKeys(KeyEventArgs e)
    {
        if (_selectionMode != SelectionKind.Column || e.Key is not (Key.Left or Key.Right) || _viewModel is null) return false;
        var count = _viewModel.Columns.Count;
        if (count == 0) return false;
        var delta = e.Key == Key.Left ? -1 : 1;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var end = ResultGridBuilder.GetHighlightEnd(ResultGrid);
            ResultGridBuilder.SetHighlightEnd(ResultGrid, Math.Clamp((end < 0 ? ResultGridBuilder.GetHighlightColumn(ResultGrid) : end) + delta, 0, count - 1));
        }
        else
        {
            var columns = ResultGridBuilder.HighlightedColumns(ResultGrid);
            var next = Math.Clamp((delta < 0 ? columns.FirstOrDefault() : columns.LastOrDefault()) + delta, 0, count - 1);
            ResultGridBuilder.SetHighlightColumn(ResultGrid, next);
        }
        ReportColumnSelection();
        if (ResultGrid.Columns.FirstOrDefault(c => c.Tag is int i && i == ResultGridBuilder.GetHighlightEnd(ResultGrid)) is { } column && _viewModel.Rows.Count > 0)
            ResultGrid.ScrollIntoView(_viewModel.Rows[0], column);
        e.Handled = true;
        return true;
    }

    private void SortColumn(int index, bool? descending)
    {
        if (_viewModel is null || index >= _viewModel.Columns.Count) return;
        _ = _viewModel.SortColumnAsync(_viewModel.Columns[index].Name, descending);
    }

    private int CurrentIndex() => ResultGrid.CurrentColumn?.Tag is int index ? index : -1;

    private void SyncCurrent() => _viewModel?.SetCurrent(ResultGrid.SelectedItem as TableRowViewModel, CurrentIndex());

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || HandleColumnKeys(e)) return;
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopyCurrentAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _viewModel.DeleteRowsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCopyCell(object? sender, RoutedEventArgs e) => _ = CopyCurrentAsync();

    /// <summary>Copia come INSERT / UPDATE / testo con intestazioni delle righe selezionate.</summary>
    private async void OnCopyAs(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not MenuItem { Tag: string kind } || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var text = kind switch
        {
            "insert" => _viewModel.BuildInsertForSelection(),
            "update" => _viewModel.BuildUpdateForSelection(),
            _ => _viewModel.BuildTsvForSelection(),
        };
        if (text.Length == 0) return;
        await clipboard.SetTextAsync(text);
        _viewModel.StatusMessage = $"Copiato come {(kind == "tsv" ? "testo" : kind.ToUpperInvariant())}.";
    }

    /// <summary>Chiave esterna: sceglie il valore da un elenco dei record della tabella collegata.</summary>
    private async void OnPickReference(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.CurrentReference is not { } reference || TopLevel.GetTopLevel(this) is not Window owner) return;
        var picker = new ReferencePickerWindow(_viewModel, reference);
        var value = await picker.ShowDialog<object?>(owner);
        if (picker.Picked) _viewModel.SetCurrentValue(value);
    }

    private async void OnSaveCellToFile(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.CurrentCell?.Value is not byte[] bytes || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions { Title = "Salva il contenuto del campo", SuggestedFileName = _viewModel.CurrentColumnName });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(bytes);
    }

    private async Task CopyCurrentAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || _viewModel is null) return;
        // Colonne selezionate: intestazioni + tutti i valori caricati, separati da tabulazione (incollabile in Excel).
        if (_selectionMode == SelectionKind.Column && ResultGridBuilder.HighlightedColumns(ResultGrid).Where(i => i < _viewModel.Columns.Count).ToArray() is { Length: > 0 } selected)
        {
            var lines = new[] { string.Join('\t', selected.Select(i => _viewModel.Columns[i].Name)) }
                .Concat(_viewModel.Rows.Select(row => string.Join('\t', selected.Select(i => row.IsNull(i) ? string.Empty : row.TextAt(i)))));
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, lines));
            _viewModel.StatusMessage = $"Copiate {selected.Length} colonne × {_viewModel.Rows.Count:N0} righe.";
            return;
        }
        // Righe intere (indicatore a sinistra) o più righe: testo separato da tabulazioni (incollabile in Excel).
        if (_viewModel.SelectedRows.Count > 1 || _selectionMode == SelectionKind.Row && _viewModel.SelectedRows.Count > 0)
        {
            var lines = _viewModel.SelectedRows.Select(row => string.Join('\t', Enumerable.Range(0, row.CellCount).Select(index => row.IsNull(index) ? string.Empty : row.TextAt(index))));
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, lines));
            return;
        }
        if (_viewModel.CurrentCell is { } current) await clipboard.SetTextAsync(current.IsNull ? string.Empty : current.Text);
    }

    /// <summary>Colonna, operatore e connettore si scelgono da un menu, come link nel testo del criterio.</summary>
    private void OnCriteriaLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FilterConditionViewModel condition, Tag: string part } button) return;
        _viewModel?.SelectFilterCommand.Execute(condition);
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        IEnumerable<(string Label, Action Apply, bool Current)> options = part switch
        {
            "column" => condition.Columns.Select(name => (name, (Action)(() => condition.Column = name), name == condition.Column)),
            "operator" => condition.Operators.Select(op => (op.Label, (Action)(() => condition.Operator = op), op == condition.Operator)),
            _ => condition.Connectors.Select(value => (value, (Action)(() => condition.Connector = value), value == condition.Connector)),
        };
        foreach (var (label, apply, current) in options)
        {
            var item = new MenuItem { Header = label, FontWeight = current ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal };
            item.Click += (_, _) => apply();
            flyout.Items.Add(item);
        }
        flyout.ShowAt(button);
    }

    private void OnFilterRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: FilterConditionViewModel condition }) _viewModel?.SelectFilterCommand.Execute(condition);
    }
}
