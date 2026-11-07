using Avalonia.Controls;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App.Views;

public partial class ServerMonitorView : UserControl
{
    private ServerMonitorViewModel? _viewModel;

    public ServerMonitorView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => _ = _viewModel?.RefreshAsync();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.CopyRequested -= OnCopy;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as ServerMonitorViewModel;
        if (_viewModel is not null) _viewModel.CopyRequested += OnCopy;
    }

    private async void OnCopy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
