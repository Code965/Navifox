using Avalonia.Controls;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App.Views;

public partial class SchemaObjectEditorView : UserControl
{
    private SchemaObjectEditorViewModel? _viewModel;

    public SchemaObjectEditorView()
    {
        InitializeComponent();
        Editor.TextChanged += (_, _) =>
        {
            if (_viewModel is not null && _viewModel.Definition != Editor.Text) _viewModel.Definition = Editor.Text;
        };
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = DataContext as SchemaObjectEditorViewModel;
            if (_viewModel is null) return;
            _viewModel.PropertyChanged += OnViewModelChanged;
            if (Editor.Text != _viewModel.Definition) Editor.Text = _viewModel.Definition;
        };
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SchemaObjectEditorViewModel.Definition) && _viewModel is not null && Editor.Text != _viewModel.Definition)
            Editor.Text = _viewModel.Definition;
    }
}
