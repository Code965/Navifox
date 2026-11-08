using Avalonia.Controls;
using Navifox.App.ViewModels;

namespace Navifox.App.Views;

public partial class DataGeneratorWindow : Window
{
    private DataGeneratorViewModel? _model;
    public DataGeneratorWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { if (_model?.IsBusy == true) { e.Cancel = true; _model.StopCommand.Execute(null); } };
    }
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_model is not null) { _model.CloseRequested -= CloseWindow; _model.PreviewColumns.CollectionChanged -= RebuildPreview; }
        base.OnDataContextChanged(e);
        _model = DataContext as DataGeneratorViewModel;
        if (_model is null) return;
        _model.CloseRequested += CloseWindow;
        _model.PreviewColumns.CollectionChanged += RebuildPreview;
    }
    private void RebuildPreview(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    { if (_model is not null) ResultGridBuilder.Rebuild(PreviewGrid, _model.PreviewColumns, editable: false); }
    private void CloseWindow() => Close(_model?.GeneratedRows > 0);
}
