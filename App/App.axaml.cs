using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Navifox.App.Services;
using Navifox.App.ViewModels;
using Navifox.Dialects;
using Navifox.Core;

namespace Navifox.App;

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
            registrations.AddSingleton<IDialectProvider, SqlServerDialectProvider>();
            registrations.AddSingleton<IDialectProvider, SqliteDialectProvider>();
            registrations.AddSingleton<IConnectionProfileStore, ConnectionProfileStore>();
            registrations.AddSingleton<IDatabaseAccessService, DatabaseAccessService>();
            registrations.AddSingleton<IWindowLocator, WindowLocator>();
            registrations.AddSingleton<IThemeService, ThemeService>();
            registrations.AddSingleton<ConnectionDialogService>();
            registrations.AddSingleton<IDialogService, DialogService>();
            registrations.AddSingleton<AutomationService>();
            registrations.AddSingleton<UpdateService>();
            registrations.AddTransient<ConnectionDialogViewModel>();
            registrations.AddTransient<MainWindowViewModel>();
            registrations.AddTransient<MainWindow>();
            _services = registrations.BuildServiceProvider();
            Services = _services;
            AppSettings.Load();
            CrashReporter.InstallUiHandler();
            ActivityRecording.Journal = new ActivityJournal(AppPaths.File("activities.db"));
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
        if (window.DataContext is MainWindowViewModel viewModel)
        {
            // Collegamento rapido: Navifox.exe --open "table|…" apre la tabella all'avvio.
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { Args: { } args } && Array.IndexOf(args, "--open") is var at and >= 0 && at + 1 < args.Length)
                viewModel.PendingOpen = args[at + 1];
            viewModel.ApplySettings();
            window.Opened += (_, _) => _ = viewModel.CheckUpdatesOnStartupAsync();
            window.Opened += async (_, _) => { await Task.Delay(1500); await CrashReporter.OfferPendingAsync(); };
            window.Closed += (_, _) => Navifox.Dialects.SshTunnelLease.CloseAll();
        }
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
