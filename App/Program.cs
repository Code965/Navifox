using Avalonia;
using Velopack;

namespace Navifox.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Deve essere la prima istruzione: gestisce installazione, disinstallazione e riavvio dopo un aggiornamento.
        VelopackApp.Build().Run();
        Services.CrashReporter.Install();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
