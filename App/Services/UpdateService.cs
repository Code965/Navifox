using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Navifox.App.Services;

/// <summary>
/// Aggiornamenti automatici tramite Velopack. La sorgente (cartella, condivisione di rete, URL web o repository GitHub)
/// si imposta in Strumenti › Opzioni; se vuota si usa quella incorporata in fase di release.
/// </summary>
public sealed class UpdateService
{
    private UpdateInfo? _pending;

    /// <summary>Sorgente predefinita incorporata dallo script di release (proprietà MSBuild NavifoxUpdateSource).</summary>
    public static string DefaultSource { get; } =
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "NavifoxUpdateSource")?.Value ?? string.Empty;

    public static string EffectiveSource => string.IsNullOrWhiteSpace(AppSettings.Current.UpdateSource) ? DefaultSource : AppSettings.Current.UpdateSource.Trim();

    public static string CurrentVersion
    {
        get
        {
            var info = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return info?.Split('+')[0] ?? "0.0.0";
        }
    }

    /// <summary>True se Navifox è stato installato con il setup (e non avviato dalla cartella di compilazione).</summary>
    public bool IsInstalled => CreateManager() is { IsInstalled: true };

    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    private static UpdateManager? CreateManager()
    {
        var source = EffectiveSource;
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            IUpdateSource updateSource;
            if (source.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                updateSource = new GithubSource(source, Environment.GetEnvironmentVariable("NAVIFOX_GITHUB_TOKEN"), prerelease: false);
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                updateSource = new SimpleWebSource(source);
            else
                updateSource = new SimpleFileSource(new DirectoryInfo(source)); // cartella locale o \\server\condivisione
            return new UpdateManager(updateSource);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Cerca una nuova versione e, se c'è, la scarica. Restituisce la versione pronta da installare.</summary>
    public async Task<string?> CheckAndDownloadAsync(Action<int>? progress = null)
    {
        var manager = CreateManager();
        if (manager is not { IsInstalled: true }) return null;
        var update = await manager.CheckForUpdatesAsync();
        if (update is null) return null;
        await manager.DownloadUpdatesAsync(update, progress);
        _pending = update;
        return PendingVersion;
    }

    /// <summary>Chiude Navifox, applica l'aggiornamento scaricato e riavvia.</summary>
    public void ApplyAndRestart()
    {
        if (_pending is null || CreateManager() is not { } manager) return;
        manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
