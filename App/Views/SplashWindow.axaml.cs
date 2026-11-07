using Avalonia.Controls;

namespace MosaicDB.App.Views;

/// <summary>Schermata di avvio NaviFox.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        var version = typeof(SplashWindow).Assembly.GetName().Version;
        VersionText.Text = version is null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    public void SetStatus(string text) => StatusText.Text = text;
}
