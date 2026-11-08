using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.DataGen;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public sealed record GeneratorOption(FakeKind Kind, string Label);
public partial class GeneratorColumnChoice : ObservableObject
{
    public static IReadOnlyList<GeneratorOption> Options { get; } = FakeKinds.All.Select(k => new GeneratorOption(k.Kind, k.Label)).ToArray();
    public GeneratorColumn Config { get; }
    public string Name => Config.Column.Name;
    public string Type => Config.Column.Type + (Config.Column.Length is { } n ? Config.Column.Decimals is { } d ? $"({n},{d})" : $"({n})" : "");
    public string Hint => IsAutomatic ? "Automatico/calcolato" : Config.IsUnique ? "Univoco" : Config.Column.NotNull ? "Obbligatorio" : "Facoltativo";
    public bool IsAutomatic => Config.Column.AutoIncrement || Config.Column.IsVirtual || Config.Column.Expression is not null;
    [ObservableProperty] private GeneratorOption _selectedKind;
    public event Action? Changed;
    public GeneratorColumnChoice(GeneratorColumn config) { Config = config; _selectedKind = Options.First(o => o.Kind == config.Kind); }
    partial void OnSelectedKindChanged(GeneratorOption value) { Config.Kind = value.Kind; Changed?.Invoke(); }
}

public partial class DataGeneratorViewModel : ObservableObject
{
    private readonly DataGenerationService _service;
    private readonly IDatabaseAccessService _access;
    private readonly IDialogService _dialogs;
    private readonly ConnectionProfile _profile;
    private readonly string _database;
    private readonly string _schema;
    private readonly string _table;
    private CancellationTokenSource? _running;
    public string Context => $"{_profile.Name} / {_database} / {_schema}.{_table}";
    public ObservableCollection<GeneratorColumnChoice> Columns { get; } = [];
    public ObservableCollection<ColumnInfo> PreviewColumns { get; } = [];
    public ObservableCollection<TableRowViewModel> PreviewRows { get; } = [];
    [ObservableProperty] private decimal? _rowCount = 100;
    [ObservableProperty] private decimal? _seed = 42;
    [ObservableProperty] private bool _randomNulls = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(GenerateCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(GenerateCommand))]
    private bool _isReady;
    [ObservableProperty] private string _statusMessage = "Caricamento della struttura…";
    [ObservableProperty] private string _sqlPreview = "";
    [ObservableProperty] private double _progress;
    public bool CanRun => IsReady && !IsBusy && !_profile.ReadOnly;
    public int GeneratedRows { get; private set; }
    public event Action? CloseRequested;

    public DataGeneratorViewModel(IDatabaseAccessService access, IDialogService dialogs, ConnectionProfile profile, string database, string schema, string table)
    {
        _access = access; _service = new(access); _dialogs = dialogs; _profile = profile; _database = database; _schema = schema; _table = table;
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var columns = await _service.PrepareAsync(_profile, _database, _schema, _table, CancellationToken.None);
            Columns.Clear();
            foreach (var config in columns)
            {
                var choice = new GeneratorColumnChoice(config);
                choice.Changed += InvalidatePreview;
                Columns.Add(choice);
            }
            IsReady = columns.Count > 0;
            StatusMessage = _profile.ReadOnly ? "Connessione in sola lettura: generazione disattivata." : "Scegli le regole, controlla l’anteprima e premi Genera.";
        }
        catch (Exception error) { StatusMessage = "Impossibile preparare i dati: " + error.Message; }
        finally { IsBusy = false; }
    }

    partial void OnRowCountChanged(decimal? value) => InvalidatePreview();
    partial void OnSeedChanged(decimal? value) => InvalidatePreview();
    partial void OnRandomNullsChanged(bool value) => InvalidatePreview();
    private void InvalidatePreview()
    {
        SqlPreview = "";
        PreviewRows.Clear();
        if (IsReady) StatusMessage = "Opzioni modificate: aggiorna l’anteprima.";
    }

    private (GeneratorColumn[] Columns, int Count, int Seed) Settings()
    {
        if (RowCount is not { } count || count != decimal.Truncate(count) || count is < 1 or > 1_000_000)
            throw new InvalidOperationException("Inserisci un numero intero da 1 a 1.000.000 righe.");
        if (Seed is not { } seed || seed != decimal.Truncate(seed) || seed < int.MinValue || seed > int.MaxValue)
            throw new InvalidOperationException("Inserisci un seme intero valido.");
        return (Columns.Select(c => c.Config).ToArray(), (int)count, (int)seed);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task PreviewAsync()
    {
        IsBusy = true;
        try
        {
            var settings = Settings();
            await _service.RefreshAsync(_profile, _database, _schema, _table, settings.Columns, CancellationToken.None);
            DataGenerationService.Validate(settings.Columns, settings.Count);
            var generator = new DataGenerator(settings.Columns, settings.Seed) { RandomNulls = RandomNulls };
            var rows = generator.Generate(Math.Min(settings.Count, 10)).ToArray();
            PreviewColumns.Clear();
            foreach (var c in generator.InsertedColumns) PreviewColumns.Add(new(c.Column.Name, c.Column.Type, !c.Column.NotNull));
            PreviewRows.Clear();
            foreach (var row in rows) PreviewRows.Add(new(row));
            var provider = _access.Provider(_profile);
            SqlPreview = generator.InsertedColumns.Count > 0
                ? provider.BuildInsert(_schema, _table, generator.InsertedColumns.Select(c => c.Column.Name).ToArray(), rows)
                : $"INSERT INTO {provider.QuoteIdentifier(_schema)}.{provider.QuoteIdentifier(_table)} " + (provider.UsesMySqlScriptSyntax ? "() VALUES ();" : "DEFAULT VALUES;");
            StatusMessage = $"Anteprima di {rows.Length} righe · nessun dato inserito.";
        }
        catch (Exception error) { StatusMessage = error.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task GenerateAsync()
    {
        IsBusy = true;
        _running = new();
        try
        {
            var settings = Settings();
            if (!await SqlGuard.ConfirmProductionAsync(_dialogs, _profile, $"Inserimento di {settings.Count:N0} righe di prova in «{_schema}.{_table}». I dati già presenti non vengono eliminati.")) return;
            await _service.RefreshAsync(_profile, _database, _schema, _table, settings.Columns, _running.Token);
            Progress = 0;
            var progress = new Progress<int>(done => Progress = 100d * done / settings.Count);
            var inserted = await _service.InsertAsync(_profile, _database, _schema, _table, settings.Columns, settings.Count, settings.Seed, RandomNulls, progress, _running.Token);
            GeneratedRows += inserted;
            Progress = 100;
            StatusMessage = $"Inserite {inserted:N0} righe. Puoi chiudere per aggiornare la tabella.";
        }
        catch (OperationCanceledException) { StatusMessage = "Generazione interrotta. Controlla l’esito della transazione nel registro attività."; }
        catch (Exception error) { StatusMessage = "Generazione non riuscita: " + error.Message; await _dialogs.ShowMessageAsync("Generatore dati", StatusMessage, isError: true); }
        finally { _running?.Dispose(); _running = null; IsBusy = false; }
    }

    [RelayCommand] private void Stop() => _running?.Cancel();
    [RelayCommand] private void Close() { if (IsBusy) Stop(); else CloseRequested?.Invoke(); }
}
