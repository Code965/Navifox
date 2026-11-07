using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MosaicDB.App.ViewModels;
using MosaicDB.Core.Models;

namespace MosaicDB.App.Services;

public sealed class ConnectionDialogService(IWindowLocator windowLocator, IServiceProvider services)
{
    /// <param name="driverName">Driver già scelto (es. «MySQL»): il dialogo si apre direttamente sulle impostazioni.</param>
    /// <param name="folders">Cartelle esistenti proposte nella casella «Cartella».</param>
    /// <param name="folder">Cartella preimpostata (es. «Nuova connessione qui» su una cartella).</param>
    /// <param name="editing">Connessione da modificare (null = nuova).</param>
    public async Task<ConnectionProfile?> ShowAsync(string? driverName = null, IEnumerable<string>? folders = null, string? folder = null, ConnectionProfile? editing = null)
    {
        var owner = windowLocator.MainWindow ?? throw new InvalidOperationException("La finestra principale non è pronta.");
        var viewModel = services.GetRequiredService<ConnectionDialogViewModel>();
        viewModel.InitializeCatalog();
        foreach (var name in folders ?? []) viewModel.Folders.Add(name);
        if (editing is not null) viewModel.LoadFrom(editing);
        else
        {
            if (driverName is not null) viewModel.StartWithDriver(driverName);
            if (folder is not null) viewModel.Folder = folder;
        }
        var dialog = new ConnectionDialog { DataContext = viewModel };
        viewModel.CloseRequested += profile => dialog.Close(profile);
        return await dialog.ShowDialog<ConnectionProfile?>(owner);
    }
}
