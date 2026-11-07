using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DbClient.ViewModels;
using DbClient.Views;
using Microsoft.Extensions.DependencyInjection;
using MosaicDB.Core;
using MosaicDB.Dialects;
using System;

namespace DbClient;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IDialectProvider, MySqlDialectProvider>();
            services.AddSingleton<IDialectProvider, PostgreSqlDialectProvider>();
            services.AddTransient<MainViewModel>();
            services.AddTransient(provider => new MainWindow
            {
                DataContext = provider.GetRequiredService<MainViewModel>()
            });
            _services = services.BuildServiceProvider();
            desktop.MainWindow = _services.GetRequiredService<MainWindow>();
            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
