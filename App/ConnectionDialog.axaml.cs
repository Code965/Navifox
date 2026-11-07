using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App;

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
    }
}
