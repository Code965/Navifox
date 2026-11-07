using Avalonia.Controls;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App.Views;

public partial class HistoryView : UserControl
{
    private HistoryTabViewModel? _viewModel;

    public HistoryView()
    {
        InitializeComponent();
        HistoryGrid.DoubleTapped += (_, _) => _viewModel?.OpenInQueryCommand.Execute(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.CopyRequested -= OnCopy;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as HistoryTabViewModel;
        if (_viewModel is not null) _viewModel.CopyRequested += OnCopy;
    }

    private async void OnCopy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
