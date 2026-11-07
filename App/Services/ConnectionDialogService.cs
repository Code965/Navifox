using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MosaicDB.App.ViewModels;
using MosaicDB.Core.Models;

namespace MosaicDB.App.Services;

public sealed class ConnectionDialogService(IWindowLocator windowLocator, IServiceProvider services)
{
    public async Task<ConnectionProfile?> ShowAsync()
    {
        var owner = windowLocator.MainWindow ?? throw new InvalidOperationException("La finestra principale non è pronta.");
        var viewModel = services.GetRequiredService<ConnectionDialogViewModel>();
        viewModel.InitializeCatalog();
        var dialog = new ConnectionDialog { DataContext = viewModel };
        viewModel.CloseRequested += profile => dialog.Close(profile);
        return await dialog.ShowDialog<ConnectionProfile?>(owner);
    }
}
