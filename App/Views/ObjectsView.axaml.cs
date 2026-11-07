using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App.Views;

public partial class ObjectsView : UserControl
{
    private ObjectsTabViewModel? _viewModel;

    public ObjectsView()
    {
        InitializeComponent();
        ObjectsGrid.DoubleTapped += (_, _) => OpenSelected();
        ObjectsGrid.AddHandler(KeyDownEvent, (_, args) =>
        {
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
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ObjectsList.DoubleTapped += (_, _) => OpenSelected();
        ObjectsList.ContextRequested += (_, args) =>
        {
            if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main) return;
            var row = (args.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ObjectRowViewModel;
            if (row is not null) ObjectsList.SelectedItem = row;
            var actions = main.GetObjectListActions(row);
            if (actions.Count == 0) return;
            new ContextMenu { ItemsSource = MainWindow.BuildMenuItems(actions) }.Open(ObjectsList);
            args.Handled = true;
        };
        ObjectsGrid.SelectionChanged += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main)
                main.SelectedObjectRows = ObjectsGrid.SelectedItems.OfType<ObjectRowViewModel>().ToArray();
        };
        // Tasto destro: sulla riga il menu della tabella, nello spazio vuoto quello della categoria (Nuova tabella, Import, Export…).
        ObjectsGrid.ContextRequested += (_, args) =>
        {
            if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main) return;
            var row = (args.Source as Avalonia.Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext as ObjectRowViewModel;
            if (row is not null && !ObjectsGrid.SelectedItems.Contains(row)) ObjectsGrid.SelectedItem = row;
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
}
