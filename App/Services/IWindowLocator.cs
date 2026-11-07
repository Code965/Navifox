using Avalonia.Controls;

namespace MosaicDB.App.Services;

public interface IWindowLocator
{
    Window? MainWindow { get; set; }
}

public sealed class WindowLocator : IWindowLocator
{
    public Window? MainWindow { get; set; }
}
