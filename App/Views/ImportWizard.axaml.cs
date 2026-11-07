using System.Collections.Specialized;
using Avalonia.Controls;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App.Views;

public partial class ImportWizard : Window
{
    private ImportWizardViewModel? _viewModel;

    public ImportWizard() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.PreviewColumns.CollectionChanged -= OnColumnsChanged;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as ImportWizardViewModel;
        if (_viewModel is null) return;
        _viewModel.PreviewColumns.CollectionChanged += OnColumnsChanged;
        ResultGridBuilder.Rebuild(PreviewGrid, _viewModel.PreviewColumns);
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is not null) ResultGridBuilder.Rebuild(PreviewGrid, _viewModel.PreviewColumns);
    }
}
