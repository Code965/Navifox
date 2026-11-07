using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MosaicDB.App.Services;
using MosaicDB.Core.Models;

namespace MosaicDB.App.ViewModels;

/// <summary>Riga dell'elenco attività con riepilogo pianificazione/esito.</summary>
public partial class AutomationJobItem(AutomationJob job) : ObservableObject
{
    public AutomationJob Job { get; } = job;
    public string Name => Job.Name;
    public string Summary => Job.Schedule switch
    {
        "Ogni N minuti" => $"{Job.Kind} · ogni {Job.IntervalMinutes} min",
        "Ogni giorno" => $"{Job.Kind} · ogni giorno alle {Job.DailyTime}",
        _ => $"{Job.Kind} · manuale"
    } + (Job.Enabled ? string.Empty : " · disattivata");
    public string LastLabel => Job.LastRun is { } last ? $"Ultima: {last:dd/MM HH:mm}" : "Mai eseguita";
    public string NextLabel => Job.NextRun(DateTime.Now) is { } next ? $"Prossima: {next:dd/MM HH:mm}" : string.Empty;
    public string StateIcon => Job.LastSucceeded switch { true => "check", false => "x", _ => "clock" };
    public bool HasFailed => Job.LastSucceeded == false;
    [ObservableProperty] private bool _isRunning;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(LastLabel));
        OnPropertyChanged(nameof(NextLabel));
        OnPropertyChanged(nameof(StateIcon));
        OnPropertyChanged(nameof(HasFailed));
    }
}

/// <summary>Scheda «Automazione»: elenco attività, editor, esecuzione e registro.</summary>
public partial class AutomationTabViewModel : WorkspaceTabViewModel, MosaicDB.App.Controls.ISqlCompletionSource
{
    private readonly AutomationService _service;
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IDialogService _dialogs;
    private bool _loadingEditor;

    public AutomationTabViewModel(AutomationService service, IDatabaseAccessService databaseAccess, IDialogService dialogs, IReadOnlyList<ConnectionProfile> profiles)
    {
        _service = service;
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        Profiles = profiles;
        foreach (var job in service.Jobs) Items.Add(new AutomationJobItem(job));
        service.JobUpdated += job =>
        {
            if (Items.FirstOrDefault(i => i.Job == job) is { } item)
            {
                item.IsRunning = service.IsRunning(job);
                item.Refresh();
            }
            if (Selected?.Job == job) LastResult = job.LastResult ?? string.Empty;
        };
        Selected = Items.FirstOrDefault();
    }

    public override string TabTitle => "Automazione";
    public override string TabGlyph => "workflow";
    public IReadOnlyList<ConnectionProfile> Profiles { get; }
    public ObservableCollection<AutomationJobItem> Items { get; } = [];
    public ObservableCollection<AutomationLogEntry> Log => _service.Log;
    public ObservableCollection<string> Databases { get; } = [];
    public ObservableCollection<string> TablesList { get; } = [];
    public IReadOnlyList<string> Kinds { get; } = ["Query SQL", "Dump struttura e dati", "Dump solo struttura", "Esporta tabella (CSV)"];
    public IReadOnlyList<string> Schedules { get; } = ["Manuale", "Ogni N minuti", "Ogni giorno"];

    public SchemaModel? Schema { get; private set; }
    public IReadOnlyList<string> OtherNames => Databases;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private AutomationJobItem? _selected;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private ConnectionProfile? _profile;
    [ObservableProperty] private string? _database;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuery), nameof(IsExport), nameof(NeedsFolder))]
    private string _kind = "Query SQL";
    [ObservableProperty] private string _sql = string.Empty;
    [ObservableProperty] private string? _table;
    [ObservableProperty] private string _outputFolder = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInterval), nameof(IsDaily))]
    private string _schedule = "Manuale";
    [ObservableProperty] private decimal? _intervalMinutes = 60;
    [ObservableProperty] private string _dailyTime = "02:00";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _lastResult = string.Empty;
    [ObservableProperty] private string _message = string.Empty;

    public bool HasSelection => Selected is not null;
    public bool IsQuery => Kind == "Query SQL";
    public bool IsExport => Kind == "Esporta tabella (CSV)";
    public bool NeedsFolder => !IsQuery;
    public bool IsInterval => Schedule == "Ogni N minuti";
    public bool IsDaily => Schedule == "Ogni giorno";

    partial void OnSelectedChanged(AutomationJobItem? value)
    {
        _loadingEditor = true;
        var job = value?.Job;
        Name = job?.Name ?? string.Empty;
        Profile = Profiles.FirstOrDefault(p => p.Id == job?.ProfileId) ?? Profiles.FirstOrDefault();
        Kind = job?.Kind ?? "Query SQL";
        Sql = job?.Sql ?? string.Empty;
        OutputFolder = job?.OutputFolder ?? string.Empty;
        Schedule = job?.Schedule ?? "Manuale";
        IntervalMinutes = job?.IntervalMinutes ?? 60;
        DailyTime = job?.DailyTime ?? "02:00";
        Enabled = job?.Enabled ?? true;
        LastResult = job?.LastResult ?? string.Empty;
        _loadingEditor = false;
        _ = LoadDatabasesAsync(job?.Database, job?.Table);
    }

    partial void OnProfileChanged(ConnectionProfile? value)
    {
        if (!_loadingEditor) _ = LoadDatabasesAsync(null, null);
    }

    partial void OnDatabaseChanged(string? value)
    {
        if (!_loadingEditor) _ = LoadTablesAsync(null);
    }

    private async Task LoadDatabasesAsync(string? database, string? table)
    {
        Databases.Clear();
        if (Profile is null) return;
        try
        {
            foreach (var name in await _databaseAccess.ListDatabasesAsync(Profile, CancellationToken.None)) Databases.Add(name);
            _loadingEditor = true;
            Database = database is not null && Databases.Contains(database) ? database : Databases.FirstOrDefault();
            _loadingEditor = false;
            await LoadTablesAsync(table);
        }
        catch (Exception exception) { Message = exception.Message; }
    }

    private async Task LoadTablesAsync(string? table)
    {
        TablesList.Clear();
        Schema = null;
        if (Profile is null || Database is null) return;
        try
        {
            var schema = Profile.Kind == DatabaseKind.MySql ? Database : "public";
            Schema = await _databaseAccess.GetSchemaModelAsync(Profile, Database, schema, CancellationToken.None);
            foreach (var name in Schema.Tables.Keys.OrderBy(n => n)) TablesList.Add(name);
            Table = table is not null && TablesList.Contains(table) ? table : TablesList.FirstOrDefault();
        }
        catch (Exception exception) { Message = exception.Message; }
    }

    [RelayCommand]
    private void New()
    {
        var job = new AutomationJob { Name = $"Attività {Items.Count + 1}", ProfileId = Profiles.FirstOrDefault()?.Id ?? Guid.Empty };
        _service.Jobs.Add(job);
        var item = new AutomationJobItem(job);
        Items.Add(item);
        Selected = item;
        _service.Save();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } item) return;
        if (!await _dialogs.ConfirmAsync("Elimina attività", $"Eliminare l’attività «{item.Name}»?", "Elimina", danger: true)) return;
        _service.Jobs.Remove(item.Job);
        Items.Remove(item);
        _service.Save();
        Selected = Items.FirstOrDefault();
    }

    [RelayCommand]
    private void Save()
    {
        if (Selected is not { } item) return;
        if (Profile is null || string.IsNullOrEmpty(Database))
        {
            Message = "Scegli connessione e database.";
            return;
        }
        if (IsDaily && !TimeSpan.TryParse(DailyTime, out _))
        {
            Message = "Orario non valido (usa HH:mm).";
            return;
        }
        var job = item.Job;
        job.Name = string.IsNullOrWhiteSpace(Name) ? "Attività" : Name.Trim();
        job.ProfileId = Profile.Id;
        job.Database = Database;
        job.Kind = Kind;
        job.Sql = Sql;
        job.Table = Table ?? string.Empty;
        job.OutputFolder = OutputFolder;
        job.Schedule = Schedule;
        job.IntervalMinutes = (int)Math.Clamp(IntervalMinutes ?? 60, 1, 100_000);
        job.DailyTime = DailyTime;
        job.Enabled = Enabled;
        job.ScheduledFrom = DateTime.Now;
        _service.Save();
        item.Refresh();
        Message = $"Attività salvata. {item.NextLabel}";
    }

    [RelayCommand]
    private async Task RunNowAsync()
    {
        if (Selected is not { } item) return;
        Save();
        Message = "Esecuzione in corso…";
        await _service.RunAsync(item.Job);
        Message = item.Job.LastSucceeded == true ? "Eseguita correttamente." : "Esecuzione non riuscita: vedi il registro.";
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Cartella di destinazione");
        if (folder is not null) OutputFolder = folder;
    }
}
