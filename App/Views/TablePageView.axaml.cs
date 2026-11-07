using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App.Views;

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
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Columns.CollectionChanged -= OnColumnsChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        base.OnDataContextChanged(e);
        _viewModel = DataContext as TableTabViewModel;
        if (_viewModel is null) return;
        _viewModel.Columns.CollectionChanged += OnColumnsChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RebuildColumns();
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildColumns();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TableTabViewModel.IsSortActive) or nameof(TableTabViewModel.IsFilterActive) or nameof(TableTabViewModel.SqlPreview)) RefreshHeaders();
        if (e.PropertyName == nameof(TableTabViewModel.CanEdit)) RebuildColumns();
    }

    private void RebuildColumns()
    {
        if (_viewModel is null) return;
        ResultGridBuilder.Rebuild(ResultGrid, _viewModel.Columns, _viewModel.CanEdit, HeaderGlyph);
    }

    private void RefreshHeaders()
    {
        if (_viewModel is null) return;
        foreach (var column in ResultGrid.Columns)
        {
            if (column.Tag is not int index || index >= _viewModel.Columns.Count) continue;
            var name = _viewModel.Columns[index].Name;
            var glyph = HeaderGlyph(index);
            column.Header = glyph is null ? name : $"{name}  {glyph}";
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

    private int CurrentIndex() => ResultGrid.CurrentColumn?.Tag is int index ? index : -1;

    private void SyncCurrent() => _viewModel?.SetCurrent(ResultGrid.SelectedItem as TableRowViewModel, CurrentIndex());

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;
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

    private async Task CopyCurrentAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || _viewModel is null) return;
        // Più righe selezionate: copia come testo separato da tabulazioni (incollabile in Excel).
        if (_viewModel.SelectedRows.Count > 1)
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
