using Avalonia;
using Avalonia.Styling;

namespace Navifox.App.Services;

public sealed class ThemeService : IThemeService
{
    public bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public void Toggle() => Apply(IsDark ? "Chiaro" : "Scuro");

    public void Apply(string theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            "Chiaro" => ThemeVariant.Light,
            "Scuro" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
