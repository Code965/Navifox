using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Navifox.App.ViewModels;

namespace Navifox.App;

public partial class ConnectionDialog : Window
{
    public ConnectionDialog()
    {
        AvaloniaXamlLoader.Load(this);
        var driverList = this.FindControl<ListBox>("DriverList");
        // Doppio clic su un driver = Avanti.
        if (driverList is not null)
            driverList.DoubleTapped += (_, _) =>
            {
                if (DataContext is ConnectionDialogViewModel viewModel && viewModel.NextCommand.CanExecute(null)) viewModel.NextCommand.Execute(null);
            };
        Opened += (_, _) => Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("DriverSearchBox")?.Focus(), DispatcherPriority.Input);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConnectionDialogViewModel viewModel) viewModel.BrowseFile = PickDatabaseFileAsync;
        };
    }

    private async System.Threading.Tasks.Task<string?> PickDatabaseFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Scegli il file del database SQLite",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Database SQLite") { Patterns = ["*.db", "*.sqlite", "*.sqlite3", "*.db3"] },
                new Avalonia.Platform.Storage.FilePickerFileType("Tutti i file") { Patterns = ["*"] },
            ],
        });
        return files.Count == 0 ? null : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(files[0]);
    }
}
