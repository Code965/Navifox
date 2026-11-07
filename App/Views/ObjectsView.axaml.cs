using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
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
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = DataContext as ObjectsTabViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        };
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
