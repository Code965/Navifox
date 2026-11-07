using Avalonia.Controls;
using Avalonia.Threading;

namespace Navifox.App.Views;

public partial class DatabaseDialog : Window
{
    public DatabaseDialog()
    {
        InitializeComponent();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => NameBox.Focus(), DispatcherPriority.Input);
    }
}
