using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Navifox.App.ViewModels;

namespace Navifox.App.Views;

public partial class InfoPanelView : UserControl
{
    private InfoPanelViewModel? _viewModel;

    public InfoPanelView()
    {
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) => RenderDdl();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as InfoPanelViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RenderDdl();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InfoPanelViewModel.Ddl)) RenderDdl();
    }

    private void RenderDdl()
    {
        DdlText.Inlines?.Clear();
        var ddl = _viewModel?.Ddl;
        if (string.IsNullOrEmpty(ddl)) return;
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        IBrush Brush(string light, string darkColor) => new SolidColorBrush(Color.Parse(dark ? darkColor : light));
        DdlText.Inlines ??= [];
        DdlText.Inlines.AddRange(SqlHighlighter.Highlight(ddl,
            keyword: Brush("#C2410C", "#FF8A47"),
            text: Brush("#1D1B19", "#E9E7E4"),
            literal: Brush("#2E7D4F", "#6FD39A"),
            number: Brush("#7B4FC8", "#B896FF"),
            comment: Brush("#9BA1A9", "#7A8089"),
            identifier: Brush("#3D3934", "#C9CDD2")));
    }

    private async void OnCopyDdl(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.Ddl is { Length: > 0 } ddl && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(ddl);
    }
}
