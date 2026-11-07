using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using MosaicDB.Core;
using MosaicDB.Core.Models;

namespace MosaicDB.App.Services;

/// <summary>Attività pianificata (Automazione).</summary>
public sealed class AutomationJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Nuova attività";
    public Guid ProfileId { get; set; }
    public string Database { get; set; } = string.Empty;
    /// <summary>"Query SQL", "Dump struttura e dati", "Dump solo struttura", "Esporta tabella (CSV)".</summary>
    public string Kind { get; set; } = "Query SQL";
    public string Sql { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    /// <summary>"Manuale", "Ogni N minuti", "Ogni giorno".</summary>
    public string Schedule { get; set; } = "Manuale";
    public int IntervalMinutes { get; set; } = 60;
    public string DailyTime { get; set; } = "02:00";
    public bool Enabled { get; set; } = true;
    public DateTime? LastRun { get; set; }
    public string? LastResult { get; set; }
    public bool? LastSucceeded { get; set; }
    /// <summary>Momento in cui la pianificazione è stata salvata (base della prima esecuzione).</summary>
    public DateTime? ScheduledFrom { get; set; }

    public DateTime? NextRun(DateTime now)
    {
        if (!Enabled) return null;
        switch (Schedule)
        {
            case "Ogni N minuti":
                // La prima esecuzione avviene un intervallo dopo il salvataggio, non subito.
                return (LastRun ?? ScheduledFrom ?? now).AddMinutes(Math.Max(1, IntervalMinutes));
            case "Ogni giorno" when TimeSpan.TryParse(DailyTime, out var time):
                var slot = now.Date + time;
                var reference = LastRun ?? ScheduledFrom ?? now;
                return reference >= slot ? slot.AddDays(1) : slot;
            default:
                return null;
        }
    }
}

public sealed record AutomationLogEntry(DateTime Time, string Job, bool Succeeded, string Message)
{
    public string TimeLabel => Time.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>Conserva le attività e le esegue quando sono in scadenza (finché Navifox è aperto).</summary>
public sealed class AutomationService
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MosaicDB", "automation.json");
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IConnectionProfileStore _profiles;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly HashSet<Guid> _running = [];

    public AutomationService(IDatabaseAccessService databaseAccess, IConnectionProfileStore profiles)
    {
        _databaseAccess = databaseAccess;
        _profiles = profiles;
        Load();
        _timer.Tick += async (_, _) => await RunDueJobsAsync();
        _timer.Start();
    }

    public ObservableCollection<AutomationJob> Jobs { get; } = [];
    public ObservableCollection<AutomationLogEntry> Log { get; } = [];
    public event Action<AutomationJob>? JobUpdated;

    public bool IsRunning(AutomationJob job) => _running.Contains(job.Id);

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var job in JsonSerializer.Deserialize<List<AutomationJob>>(File.ReadAllText(FilePath)) ?? []) Jobs.Add(job);
        }
        catch { /* file non valido: si riparte vuoti */ }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Jobs.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task RunDueJobsAsync()
    {
        var now = DateTime.Now;
        foreach (var job in Jobs.Where(j => j.NextRun(now) is { } next && next <= now && !_running.Contains(j.Id)).ToArray())
            await RunAsync(job);
    }

    public async Task RunAsync(AutomationJob job)
    {
        if (!_running.Add(job.Id)) return;
        JobUpdated?.Invoke(job);
        var started = DateTime.Now;
        string message;
        bool ok;
        try
        {
            var profile = (await _profiles.LoadAsync()).FirstOrDefault(p => p.Id == job.ProfileId)
                          ?? throw new InvalidOperationException("La connessione dell’attività non esiste più.");
            message = await Task.Run(() => ExecuteAsync(job, profile));
            ok = true;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            ok = false;
        }
        var elapsed = DateTime.Now - started;
        job.LastRun = started;
        job.LastSucceeded = ok;
        job.LastResult = $"{message} ({elapsed.TotalSeconds:0.0} s)";
        _running.Remove(job.Id);
        Log.Insert(0, new AutomationLogEntry(started, job.Name, ok, job.LastResult));
        while (Log.Count > 500) Log.RemoveAt(Log.Count - 1);
        Save();
        JobUpdated?.Invoke(job);
    }

    private async Task<string> ExecuteAsync(AutomationJob job, ConnectionProfile profile)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        switch (job.Kind)
        {
            case "Query SQL":
            {
                var results = await _databaseAccess.ExecuteBatchAsync(profile, string.IsNullOrEmpty(job.Database) ? null : job.Database, job.Sql, 100, CancellationToken.None);
                if (results.FirstOrDefault(r => r.Error is not null) is { } failed) throw new InvalidOperationException($"{failed.Error!.Message}");
                var affected = results.Sum(r => Math.Max(0, r.Result?.RecordsAffected ?? 0));
                return $"{results.Count} istruzioni eseguite, {affected:N0} righe interessate";
            }
            case "Dump struttura e dati" or "Dump solo struttura":
            {
                Directory.CreateDirectory(job.OutputFolder);
                var path = Path.Combine(job.OutputFolder, $"{job.Database}_{stamp}.sql");
                await using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                    await _databaseAccess.DumpDatabaseAsync(profile, job.Database, job.Kind == "Dump struttura e dati", writer, null, CancellationToken.None);
                return $"Dump salvato in {path} ({new FileInfo(path).Length / 1024d:N0} KB)";
            }
            case "Esporta tabella (CSV)":
            {
                Directory.CreateDirectory(job.OutputFolder);
                var provider = _databaseAccess.Provider(profile);
                var schema = profile.Kind == DatabaseKind.MySql ? job.Database : "public";
                var columns = await _databaseAccess.GetColumnsAsync(profile, job.Database, schema, job.Table, CancellationToken.None);
                var path = Path.Combine(job.OutputFolder, $"{job.Table}_{stamp}.csv");
                var sql = $"SELECT * FROM {provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(job.Table)}";
                var options = new ExportOptions { Format = ExportFormat.Csv, Path = path, Columns = columns.Select(c => c.Name).ToArray(), TableName = job.Table, Schema = schema };
                var rows = await DataExporter.ExportAsync(options, _databaseAccess.StreamQueryAsync(profile, job.Database, sql, _ => { }, CancellationToken.None), provider, null, CancellationToken.None);
                return $"{rows:N0} righe esportate in {path}";
            }
            default:
                throw new NotSupportedException(job.Kind);
        }
    }
}
