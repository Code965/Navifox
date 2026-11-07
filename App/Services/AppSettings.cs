using System.Text.Json;

namespace MosaicDB.App.Services;

/// <summary>Preferenze dell'applicazione (Strumenti › Opzioni), salvate in %APPDATA%\MosaicDB\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MosaicDB", "settings.json");

    public static AppSettings Current { get; private set; } = new();

    /// <summary>"Sistema", "Chiaro" o "Scuro".</summary>
    public string Theme { get; set; } = "Sistema";
    public bool LimitRecords { get; set; } = true;
    public int PageSize { get; set; } = 1000;
    public string MonitorInterval { get; set; } = "5 s";
    public bool RecordHistory { get; set; } = true;
    public int QueryMaxRows { get; set; } = 1000;
    public bool ShowTreeOnStartup { get; set; } = true;
    public bool ShowInfoOnStartup { get; set; } = true;
    /// <summary>Controlla gli aggiornamenti all'avvio.</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;
    /// <summary>Sorgente aggiornamenti; vuota = quella incorporata nella release.</summary>
    public string UpdateSource { get; set; } = string.Empty;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath)) Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
