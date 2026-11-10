using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Navifox.App.ViewModels;

namespace Navifox.App.Views;

public partial class ObjectsView : UserControl
{
    private ObjectsTabViewModel? _viewModel;

    public ObjectsView()
    {
        InitializeComponent();
        ObjectRowViewModel? ImportRow(object? source) => (source as Avalonia.Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext as ObjectRowViewModel
            ?? (source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ObjectRowViewModel;
        Services.ImportFileDrop.Attach(this,
            source => _viewModel?.Source is { Category: "Tabelle", Profile.ReadOnly: false } && ImportRow(source)?.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase) != true,
            (source, path) => TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main ? main.ImportFileIntoObjectsAsync(ImportRow(source), path) : Task.CompletedTask,
            message => { if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main) main.StatusMessage = message; });
        ObjectsGrid.DoubleTapped += (_, _) => OpenSelected();
        ObjectsGrid.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Source is TextBox) return; // rinomina sul posto: Canc e Invio restano alla casella
            var main = TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;
            if (args.Key == Key.Enter)
            {
                OpenSelected();
                args.Handled = true;
            }
            else if (main is not null && args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key is Key.C or Key.V)
            {
                main.SelectedObjectRows = ObjectsGrid.SelectedItems.OfType<ObjectRowViewModel>().ToArray();
                if (args.Key == Key.C) main.CopyTablesCommand.Execute(null);
                else main.PasteTablesCommand.Execute(_viewModel?.Source);
                args.Handled = true;
            }
            else if (main is not null && args.Key == Key.Delete && args.KeyModifiers == KeyModifiers.None && _viewModel?.IsTableList == true)
            {
                main.SelectedObjectRows = ObjectsGrid.SelectedItems.OfType<ObjectRowViewModel>().ToArray();
                main.DeleteSelectedObjectsCommand.Execute(null);
                args.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ObjectsList.DoubleTapped += (_, _) => OpenSelected();
        // F2: rinomina sul posto della tabella selezionata.
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key != Key.F2 || args.KeyModifiers != KeyModifiers.None || _viewModel?.SelectedObject is not { IsRenaming: false } row) return;
            if (args.Source is TextBox) return;
            BeginRename(row.Name);
            args.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ObjectsList.ContextRequested += (_, args) =>
        {
            if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main) return;
            main.ContextOrigin = "objects";
            var row = (args.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ObjectRowViewModel;
            if (row is not null && !ObjectsList.SelectedItems!.Contains(row)) ObjectsList.SelectedItem = row;
            main.SelectedObjectRows = ObjectsList.SelectedItems!.OfType<ObjectRowViewModel>().ToArray();
            var actions = main.GetObjectListActions(row);
            if (actions.Count == 0) return;
            new ContextMenu { ItemsSource = MainWindow.BuildMenuItems(actions) }.Open(ObjectsList);
            args.Handled = true;
        };
        ObjectsList.SelectionChanged += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main)
                main.SelectedObjectRows = ObjectsList.SelectedItems!.OfType<ObjectRowViewModel>().ToArray();
        };
        ObjectsList.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Source is TextBox) return;
            if (args.Key != Key.Delete || args.KeyModifiers != KeyModifiers.None || _viewModel?.IsTableList != true || TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main) return;
            main.SelectedObjectRows = ObjectsList.SelectedItems!.OfType<ObjectRowViewModel>().ToArray();
            main.DeleteSelectedObjectsCommand.Execute(null); args.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ObjectsGrid.SelectionChanged += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main)
                main.SelectedObjectRows = ObjectsGrid.SelectedItems.OfType<ObjectRowViewModel>().ToArray();
        };
        // Tasto destro: sulla riga il menu della tabella, nello spazio vuoto quello della categoria (Nuova tabella, Import, Export…).
        ObjectsGrid.ContextRequested += (_, args) =>
        {
            if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main) return;
            main.ContextOrigin = "objects";
            var row = (args.Source as Avalonia.Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext as ObjectRowViewModel;
            if (row is not null && !ObjectsGrid.SelectedItems.Contains(row)) ObjectsGrid.SelectedItem = row;
            main.SelectedObjectRows = ObjectsGrid.SelectedItems.OfType<ObjectRowViewModel>().ToArray();
            var actions = main.GetObjectListActions(row);
            if (actions.Count == 0) return;
            new ContextMenu { ItemsSource = MainWindow.BuildMenuItems(actions) }.Open(ObjectsGrid);
            args.Handled = true;
        };
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.ColumnsChanged -= ApplyColumnVisibility;
            }
            _viewModel = DataContext as ObjectsTabViewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                _viewModel.ColumnsChanged += ApplyColumnVisibility;
                ApplyColumnVisibility();
            }
        };
    }

    private void ApplyColumnVisibility()
    {
        if (_viewModel is null) return;
        // Funzioni, trigger, eventi e sequenze: niente righe/dimensione/engine, al loro posto «Dettagli».
        var tableList = _viewModel.IsTableList;
        foreach (var column in ObjectsGrid.Columns)
        {
            if (column.Header is not string header) continue;
            if (header == "Dettagli") column.IsVisible = !tableList;
            else if (!tableList && header is "Auto Increment" or "Dimensione dati" or "Engine" or "Righe") column.IsVisible = false;
            else if (_viewModel.ColumnVisibility.TryGetValue(header, out var visible)) column.IsVisible = visible;
        }
    }

    private void OpenSelected()
    {
        if (_viewModel?.OpenSelectedCommand.CanExecute(null) == true) _viewModel.OpenSelectedCommand.Execute(null);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ObjectsTabViewModel.IsSearchOpen)) return;
        if (_viewModel?.IsSearchOpen == true) Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Rinomina sul posto della riga con questo nome (tabelle e viste). False se la riga non è nell'elenco.</summary>
    public bool BeginRename(string name)
    {
        if (_viewModel is null || !_viewModel.IsTableList) return false;
        var row = _viewModel.VisibleObjects.FirstOrDefault(r => r.Name == name);
        if (row is null) return false;
        _viewModel.SelectedObject = row;
        if (ObjectsGrid.IsVisible) ObjectsGrid.ScrollIntoView(row, null);
        else ObjectsList.ScrollIntoView(row);
        row.EditName = row.Name;
        row.IsRenaming = true;
        return true;
    }

    private async void CommitRename(TextBox box, bool cancel)
    {
        if (box.DataContext is not ObjectRowViewModel { IsRenaming: true } row) return;
        row.IsRenaming = false;
        if (ObjectsGrid.IsVisible) ObjectsGrid.Focus(); else ObjectsList.Focus();
        if (cancel || string.IsNullOrWhiteSpace(row.EditName) || row.EditName.Trim() == row.Name) return;
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main || _viewModel?.Source is not { } category) return;
        if (category.SelfAndDescendants().FirstOrDefault(n => n.Kind == TreeNodeKind.Table && n.Name == row.Name) is { } node)
            await main.RenameTableAsync(node, row.EditName);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter) { CommitRename(box, cancel: false); e.Handled = true; }
        else if (e.Key == Key.Escape) { CommitRename(box, cancel: true); e.Handled = true; }
    }

    private void OnRenameLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is TextBox box) CommitRename(box, cancel: false);
    }
}
