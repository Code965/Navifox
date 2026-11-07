using System.Text.Json;

namespace Navifox.App.Services;

/// <summary>Preferenze dell'applicazione (Strumenti › Opzioni), salvate in %APPDATA%\Navifox\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Navifox.Core.AppPaths.File("settings.json");

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
    /// <summary>Modalità sicura: conferma prima di UPDATE/DELETE senza WHERE, DROP e TRUNCATE.</summary>
    public bool SafeMode { get; set; } = true;
    /// <summary>Sorgente aggiornamenti; vuota = quella incorporata nella release.</summary>
    public string UpdateSource { get; set; } = string.Empty;
    /// <summary>File SQL aperti di recente (File › Apri recenti).</summary>
    public List<string> RecentFiles { get; set; } = [];
    /// <summary>Tabelle aperte di recente («idConnessione|database|schema|tabella») per la pagina iniziale.</summary>
    public List<string> RecentTables { get; set; } = [];
    /// <summary>Connessioni aperte di recente (id).</summary>
    public List<string> RecentConnections { get; set; } = [];
    /// <summary>Preferiti: «table|id|database|schema|tabella» oppure «query|id».</summary>
    public List<string> Favorites { get; set; } = [];
    /// <summary>Gruppi di tabelle (Gestisci gruppo).</summary>
    public List<TableGroupEntry> TableGroups { get; set; } = [];
    /// <summary>Riapre all'avvio le schede dell'ultima sessione.</summary>
    public bool RestoreSession { get; set; } = true;
    public string? LastSession { get; set; }
    /// <summary>Cartelle di connessioni create dall'utente (anche vuote).</summary>
    public List<string> ConnectionFolders { get; set; } = [];
    /// <summary>Ordine scelto dall'utente per cartelle («f:percorso») e connessioni («c:id») nell'albero.</summary>
    public List<string> TreeOrder { get; set; } = [];
    /// <summary>Cartelle chiuse dall'utente (percorsi).</summary>
    public List<string> CollapsedFolders { get; set; } = [];

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


/// <summary>Tabella assegnata a un gruppo (Table null = gruppo vuoto). Key = «idConnessione|database|schema».</summary>
public sealed class TableGroupEntry
{
    public string Key { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string? Table { get; set; }
}
