using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Navifox.App.ViewModels;

namespace Navifox.App.Views;

public partial class DataTransferWindow : Window
{
    private DataTransferViewModel? _viewModel;

    public DataTransferWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.Log.CollectionChanged -= OnLogChanged;
            _viewModel = DataContext as DataTransferViewModel;
            if (_viewModel is not null) _viewModel.Log.CollectionChanged += OnLogChanged;
        };
    }

    /// <summary>Il registro scorre da solo sull'ultimo comando (disattivabile con «Segui gli ultimi comandi»).</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || FollowLog.IsChecked != true || _viewModel is not { Log.Count: > 0 } vm) return;
        Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(vm.Log.Count - 1), DispatcherPriority.Background);
    }

    private async void OnCopyLog(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is not null && Clipboard is { } clipboard) await clipboard.SetTextAsync(_viewModel.LogText);
    }
}
