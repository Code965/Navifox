using Avalonia.Data.Converters;

namespace MosaicDB.App.ViewModels;

public static class WizardConverters
{
    public static readonly IValueConverter AvailableOpacity = new FuncValueConverter<bool, double>(available => available ? 1 : 0.5);
    public static readonly IValueConverter OkIcon = new FuncValueConverter<bool, string>(ok => ok ? "check" : "x");
    public static readonly IValueConverter FinishedProgress = new FuncValueConverter<bool, double>(finished => finished ? 100 : 0);
}
