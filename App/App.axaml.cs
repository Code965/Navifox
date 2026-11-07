using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using MosaicDB.App.Services;
using MosaicDB.App.ViewModels;
using MosaicDB.Dialects;
using MosaicDB.Core;

namespace MosaicDB.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var registrations = new ServiceCollection();
            registrations.AddSingleton<IDialectProvider, MySqlDialectProvider>();
            registrations.AddSingleton<IDialectProvider, PostgreSqlDialectProvider>();
            registrations.AddSingleton<IConnectionProfileStore, ConnectionProfileStore>();
            registrations.AddSingleton<IDatabaseAccessService, DatabaseAccessService>();
            registrations.AddSingleton<IWindowLocator, WindowLocator>();
            registrations.AddSingleton<IThemeService, ThemeService>();
            registrations.AddSingleton<ConnectionDialogService>();
            registrations.AddSingleton<IDialogService, DialogService>();
            registrations.AddSingleton<AutomationService>();
            registrations.AddTransient<ConnectionDialogViewModel>();
            registrations.AddTransient<MainWindowViewModel>();
            registrations.AddTransient<MainWindow>();
            _services = registrations.BuildServiceProvider();
            Services = _services;
            AppSettings.Load();
            _services.GetRequiredService<IThemeService>().Apply(AppSettings.Current.Theme);

            // Modalità screenshot/automazione: niente schermata di avvio.
            if (desktop.Args?.Contains("--screenshot") == true || Environment.GetEnvironmentVariable("NAVIFOX_NO_SPLASH") is not null)
            {
                SqlHistory.Load();
                desktop.MainWindow = CreateMainWindow();
            }
            else
            {
                var splash = new Views.SplashWindow();
                desktop.MainWindow = splash;
                splash.Opened += async (_, _) => await StartAsync(desktop, splash);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow CreateMainWindow()
    {
        var window = _services!.GetRequiredService<MainWindow>();
        _services.GetRequiredService<IWindowLocator>().MainWindow = window;
        if (window.DataContext is MainWindowViewModel viewModel) viewModel.ApplySettings();
        return window;
    }

    /// <summary>Mostra la schermata di avvio mentre carica preferenze, cronologia e attività, poi apre la finestra principale.</summary>
    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, Views.SplashWindow splash)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        splash.SetStatus("Caricamento preferenze…");
        await Task.Delay(150);
        splash.SetStatus("Caricamento cronologia…");
        await Task.Run(SqlHistory.Load);
        splash.SetStatus("Preparazione dell’area di lavoro…");
        var window = CreateMainWindow();
        _services!.GetRequiredService<AutomationService>();
        splash.SetStatus("Pronto");
        var remaining = TimeSpan.FromMilliseconds(1600) - started.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        desktop.MainWindow = window;
        window.Show();
        splash.Close();
    }
}
