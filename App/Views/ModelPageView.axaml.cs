using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Navifox.App.Services;
using Navifox.App.ViewModels;

namespace Navifox.App.Views;

public partial class ModelPageView : UserControl
{
    public ModelPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Canvas.Model = DataContext as ModelTabViewModel;
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Canvas.ZoomIn();
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Canvas.ZoomOut();
    private void OnFit(object? sender, RoutedEventArgs e) => Canvas.Fit();
    private void OnRelayout(object? sender, RoutedEventArgs e) => Canvas.Relayout();

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModelTabViewModel model) return;
        var dialogs = App.Services.GetRequiredService<IDialogService>();
        var path = await dialogs.PickSaveFileAsync("Esporta diagramma", $"modello_{model.Database}.png", "png", new FileFilter("Immagine PNG", "*.png"));
        if (path is null) return;
        try
        {
            Canvas.ExportPng(path);
            model.StatusMessage = $"Diagramma esportato in {path}";
        }
        catch (Exception exception) { await dialogs.ShowMessageAsync("Esportazione non riuscita", exception.Message, isError: true); }
    }
}
