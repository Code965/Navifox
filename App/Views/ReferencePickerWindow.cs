using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Navifox.App.ViewModels;

namespace Navifox.App.Views;

/// <summary>Chiave esterna: elenco filtrabile dei record della tabella collegata; doppio clic o Invio sceglie il valore.</summary>
public sealed class ReferencePickerWindow : Window
{
    private readonly TableTabViewModel _table;
    private readonly ColumnReference _reference;
    private readonly DataGrid _grid = new() { IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, AutoGenerateColumns = false };
    private readonly TextBox _search = new() { Watermark = "Filtra i record…", MinWidth = 300 };
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly ObservableCollection<TableRowViewModel> _rows = [];
    private int _keyIndex = -1;
    private CancellationTokenSource? _debounce;

    public bool Picked { get; private set; }

    public ReferencePickerWindow() : this(null!, null!) { }

    public ReferencePickerWindow(TableTabViewModel table, ColumnReference reference)
    {
        _table = table;
        _reference = reference;
        Title = $"Scegli da {reference?.Table}.{reference?.ReferencedColumn}";
        Width = 820;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _grid.Classes.Add("nf-grid");
        _grid.ItemsSource = _rows;
        var choose = new Button { Content = "Scegli", Classes = { "dialog-primary" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Annulla", Classes = { "small-muted" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        choose.Click += (_, _) => Choose();
        cancel.Click += (_, _) => Close(null);
        _grid.DoubleTapped += (_, _) => Choose();
        _grid.KeyDown += (_, args) => { if (args.Key == Key.Enter) { Choose(); args.Handled = true; } };
        _search.TextChanged += (_, _) => _ = ReloadDelayedAsync();
        var top = new DockPanel { Margin = new Thickness(12, 10) };
        top.Children.Add(_search);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 8) };
        bottom.Children.Add(_status);
        Grid.SetColumn(cancel, 1);
        Grid.SetColumn(choose, 2);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        bottom.Children.Add(cancel);
        bottom.Children.Add(choose);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;
        Opened += async (_, _) =>
        {
            _search.Focus();
            await ReloadAsync();
        };
    }

    private async Task ReloadDelayedAsync()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        try { await Task.Delay(300, token); } catch (OperationCanceledException) { return; }
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _status.Text = "Caricamento…";
        try
        {
            var result = await _table.LoadReferenceRowsAsync(_reference, _search.Text);
            if (result is null) return;
            ResultGridBuilder.Rebuild(_grid, result.Columns);
            _rows.Clear();
            foreach (var row in result.Rows) _rows.Add(new TableRowViewModel(row));
            _keyIndex = result.Columns.ToList().FindIndex(c => string.Equals(c.Name, _reference.ReferencedColumn, StringComparison.OrdinalIgnoreCase));
            _status.Text = $"{_rows.Count:N0} record{(result.Rows.Count >= 500 ? " (primi 500: filtra per restringere)" : string.Empty)} · scegli quello da collegare";
            ResultGridBuilder.FitToContent(_grid, _rows);
            if (_rows.Count > 0) _grid.SelectedIndex = 0;
        }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    private void Choose()
    {
        if (_grid.SelectedItem is not TableRowViewModel row || _keyIndex < 0) return;
        Picked = true;
        Close(row.CellAt(_keyIndex)?.Value);
    }
}
