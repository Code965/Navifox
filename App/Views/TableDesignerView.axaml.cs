using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App.Views;

public partial class TableDesignerView : UserControl
{
    private TableDesignerViewModel? _viewModel;

    public TableDesignerView()
    {
        InitializeComponent();
        // Le celle contengono controlli modificabili: quando uno riceve il focus si seleziona la sua riga.
        AddHandler(GotFocusEvent, (_, args) =>
        {
            if (args.Source is not Control control) return;
            var grid = control.FindAncestorOfType<DataGrid>();
            if (grid is null || control.DataContext is null || ReferenceEquals(control.DataContext, DataContext)) return;
            if (!ReferenceEquals(grid.SelectedItem, control.DataContext)) grid.SelectedItem = control.DataContext;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = DataContext as TableDesignerViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
            RenderPreview();
        };
        ActualThemeVariantChanged += (_, _) => RenderPreview();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TableDesignerViewModel.SqlPreview)) RenderPreview();
    }

    private void RenderPreview()
    {
        PreviewText.Inlines?.Clear();
        var sql = _viewModel?.SqlPreview;
        if (string.IsNullOrEmpty(sql)) return;
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        IBrush Brush(string light, string darkColor) => new SolidColorBrush(Color.Parse(dark ? darkColor : light));
        PreviewText.Inlines ??= [];
        PreviewText.Inlines.AddRange(SqlHighlighter.Highlight(sql,
            keyword: Brush("#C2410C", "#FF8A47"),
            text: Brush("#1D1B19", "#E9E7E4"),
            literal: Brush("#2E7D4F", "#6FD39A"),
            number: Brush("#7B4FC8", "#B896FF"),
            comment: Brush("#9BA1A9", "#7A8089"),
            identifier: Brush("#3D3934", "#C9CDD2")));
    }

    private async void OnCopyPreview(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel?.SqlPreview is { Length: > 0 } sql && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(sql);
    }
}
