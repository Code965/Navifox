using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MosaicDB.App.Services;
using MosaicDB.Core;
using MosaicDB.Core.Models;

namespace MosaicDB.App.ViewModels;

public sealed record ImportFormatOption(string Label, string Extension, ImportFormat? Format, string Glyph)
{
    public bool IsAvailable => Format is not null;
    public string Display => IsAvailable ? Label : $"{Label}  · presto";
}

public partial class ColumnMapping(ColumnInfo target, IReadOnlyList<string> sourceFields) : ObservableObject
{
    public const string Ignore = "(ignora)";
    public string Target => target.Name;
    public string TargetType => target.DataType + (target.IsPrimaryKey ? " · PK" : string.Empty) + (target.IsNullable ? string.Empty : " · NOT NULL");
    public IReadOnlyList<string> SourceFields { get; } = sourceFields;
    [ObservableProperty] private string _source = Ignore;
    public bool IsMapped => Source != Ignore;
}

/// <summary>Import Wizard in 5 passi: formato → file e opzioni (con anteprima) → mappatura → modalità → avvio.</summary>
public partial class ImportWizardViewModel : ObservableObject
{
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IDialogService _dialogs;
    private readonly ConnectionProfile _profile;
    private readonly string _database;
    private readonly string _schema;
    private readonly string _table;
    private readonly IReadOnlyList<ColumnInfo> _columns;
    private IReadOnlyList<string> _fields = [];
    private CancellationTokenSource? _running;

    public ImportWizardViewModel(IDatabaseAccessService databaseAccess, IDialogService dialogs, ConnectionProfile profile, string database, string schema, string table, IReadOnlyList<ColumnInfo> columns)
    {
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        _profile = profile;
        _database = database;
        _schema = schema;
        _table = table;
        _columns = columns;
        _selectedFormat = Formats.First(format => format.Format == ImportFormat.Csv);
    }

    public event Action<bool>? CloseRequested;

    public IReadOnlyList<ImportFormatOption> Formats { get; } =
    [
        new("File CSV (*.csv)", "csv", ImportFormat.Csv, "▦"),
        new("File di testo delimitato (*.txt)", "txt", ImportFormat.Text, "▤"),
        new("File Excel (*.xlsx)", "xlsx", ImportFormat.Excel, "▥"),
        new("File JSON (*.json)", "json", ImportFormat.Json, "{}"),
        new("File XML (*.xml)", "xml", null, "‹›"),
        new("DBase (*.dbf)", "dbf", null, "▤"),
        new("Database MS Access (*.accdb)", "accdb", null, "▣"),
    ];
    public IReadOnlyList<string> Encodings { get; } = ["UTF-8", "UTF-16 LE", "Windows-1252"];
    public IReadOnlyList<string> Delimiters { get; } = ["Virgola (,)", "Punto e virgola (;)", "Tabulazione", "Barra verticale (|)"];
    public IReadOnlyList<string> Qualifiers { get; } = ["\"", "'", "(nessuno)"];
    public ObservableCollection<string> Sheets { get; } = [];
    public ObservableCollection<ColumnInfo> PreviewColumns { get; } = [];
    public ObservableCollection<TableRowViewModel> PreviewRows { get; } = [];
    public ObservableCollection<ColumnMapping> Mappings { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public long ImportedRows { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepTitle), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(IsStep5), nameof(CanGoBack), nameof(CanGoNext), nameof(IsLastStep))]
    private int _step = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDelimited), nameof(IsExcel), nameof(CanGoNext), nameof(HasHeaderOption))]
    private ImportFormatOption _selectedFormat;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private string _selectedEncoding = "UTF-8";
    [ObservableProperty] private string _selectedDelimiter = "Virgola (,)";
    [ObservableProperty] private string _selectedQualifier = "\"";
    [ObservableProperty] private bool _firstRowIsHeader = true;
    [ObservableProperty] private decimal? _skipRows = 0;
    [ObservableProperty] private string? _selectedSheet;
    [ObservableProperty] private bool _replaceExisting;
    [ObservableProperty] private bool _emptyAsNull = true;
    [ObservableProperty] private bool _continueOnError;
    [ObservableProperty] private bool _useTransaction = true;
    [ObservableProperty] private decimal? _batchSize = 200;
    [ObservableProperty] private string _previewInfo = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public string StepTitle => Step switch
    {
        1 => $"Questa procedura guidata importa dati nella tabella «{_table}». Da quale formato vuoi importare? (1/5)",
        2 => "Scegli il file e come leggerlo. L’anteprima mostra le prime righe. (2/5)",
        3 => "Associa i campi del file alle colonne della tabella. (3/5)",
        4 => "Scegli la modalità di importazione. (4/5)",
        _ => "Tutto pronto: premi «Avvia» per importare. (5/5)"
    };
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsStep4 => Step == 4;
    public bool IsStep5 => Step == 5;
    public bool IsLastStep => Step == 5;
    public bool CanGoBack => Step > 1 && !IsRunning;
    public bool CanGoNext => Step < 5 && SelectedFormat.IsAvailable && !IsRunning;
    public bool IsDelimited => SelectedFormat.Format is ImportFormat.Csv or ImportFormat.Text;
    public bool IsExcel => SelectedFormat.Format == ImportFormat.Excel;
    public bool HasHeaderOption => SelectedFormat.Format != ImportFormat.Json;
    public string Summary => $"""
        Destinazione:  {_table} @{_database} ({_profile.Name})
        File:          {Path}
        Formato:       {SelectedFormat.Label}
        Colonne:       {Mappings.Count(mapping => mapping.IsMapped)} associate su {Mappings.Count}
        Modalità:      {(ReplaceExisting ? "svuota la tabella e aggiungi" : "aggiungi ai record esistenti")}
        Errori:        {(ContinueOnError ? "salta le righe con errori" : "interrompi al primo errore")}
        Transazione:   {(UseTransaction ? "sì (annullata in caso di errore)" : "no")}
        """;

    partial void OnSelectedFormatChanged(ImportFormatOption value)
    {
        if (value.Format == ImportFormat.Text) SelectedDelimiter = "Tabulazione";
        else if (value.Format == ImportFormat.Csv && SelectedDelimiter == "Tabulazione") SelectedDelimiter = "Virgola (,)";
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
    }

    partial void OnPathChanged(string value)
    {
        LoadSheets();
        RefreshPreview();
    }
    partial void OnSelectedDelimiterChanged(string value) => RefreshPreview();
    partial void OnSelectedQualifierChanged(string value) => RefreshPreview();
    partial void OnFirstRowIsHeaderChanged(bool value) => RefreshPreview();
    partial void OnSkipRowsChanged(decimal? value) => RefreshPreview();
    partial void OnSelectedSheetChanged(string? value) => RefreshPreview();
    partial void OnSelectedEncodingChanged(string value) => RefreshPreview();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Importa da…", new FileFilter(SelectedFormat.Label, $"*.{SelectedFormat.Extension}"), new FileFilter("Tutti i file", "*.*"));
        if (path is null) return;
        Path = path;
        RefreshPreview();
    }

    [RelayCommand]
    private void Next()
    {
        if (!ValidateStep()) return;
        if (Step == 2) BuildMappings();
        Step = Math.Min(5, Step + 1);
        if (Step == 2) RefreshPreview();
        if (Step == 5) OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void Back() => Step = Math.Max(1, Step - 1);

    [RelayCommand]
    private void AutoMap()
    {
        foreach (var mapping in Mappings)
        {
            var match = _fields.FirstOrDefault(field => string.Equals(Normalize(field), Normalize(mapping.Target), StringComparison.OrdinalIgnoreCase));
            mapping.Source = match ?? ColumnMapping.Ignore;
        }
    }

    [RelayCommand]
    private void ClearMapping()
    {
        foreach (var mapping in Mappings) mapping.Source = ColumnMapping.Ignore;
    }

    [RelayCommand]
    private void Close()
    {
        _running?.Cancel();
        CloseRequested?.Invoke(ImportedRows > 0);
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        var mapped = Mappings.Where(mapping => mapping.IsMapped).ToArray();
        if (mapped.Length == 0)
        {
            ErrorMessage = "Associa almeno una colonna.";
            return;
        }
        if (ReplaceExisting && !await _dialogs.ConfirmAsync("Svuota tabella", $"Tutti i record esistenti di «{_table}» verranno eliminati prima dell’importazione. Continuare?", "Svuota e importa", danger: true))
            return;

        IsRunning = true;
        IsFinished = false;
        ErrorMessage = string.Empty;
        Log.Clear();
        ImportedRows = 0;
        _running = new CancellationTokenSource();
        var token = _running.Token;
        var timer = Stopwatch.StartNew();
        AddLog($"Importazione di «{System.IO.Path.GetFileName(Path)}» in {_table}");
        long errors = 0;
        long read = 0;
        try
        {
            var provider = _databaseAccess.Provider(_profile);
            var sourceIndexes = mapped.Select(mapping => _fields.ToList().IndexOf(mapping.Source)).ToArray();
            var targetColumns = mapped.Select(mapping => mapping.Target).ToArray();
            var options = ReadOptions();
            var batchSize = (int)Math.Clamp(BatchSize ?? 200, 1, 5000);
            var emptyAsNull = EmptyAsNull;
            var continueOnError = ContinueOnError;
            var useTransaction = UseTransaction;
            var replace = ReplaceExisting;
            var progress = new Progress<(long Read, long Imported, long Errors)>(value =>
                ProgressText = $"{value.Read:N0} letti · {value.Imported:N0} importati · {value.Errors:N0} errori");
            var logger = new Progress<string>(AddLog);

            (ImportedRows, errors, read) = await Task.Run(async () =>
            {
                await using var connection = await _databaseAccess.OpenAsync(_profile, _database, token);
                var target = $"{provider.QuoteIdentifier(_schema)}.{provider.QuoteIdentifier(_table)}";
                long imported = 0, failed = 0, total = 0;
                var logged = 0;
                if (useTransaction) await provider.ExecuteAsync(connection, provider.UsesMySqlScriptSyntax ? "START TRANSACTION" : "BEGIN", 0, token);
                try
                {
                    if (replace) await provider.ExecuteAsync(connection, $"DELETE FROM {target}", 0, token);
                    var source = DataImporter.Open(options);
                    var batch = new List<IReadOnlyList<object?>>(batchSize);
                    async Task Flush()
                    {
                        if (batch.Count == 0) return;
                        var (ok, ko, message) = await InsertBatchAsync(provider, connection, targetColumns, batch, useTransaction, continueOnError, token);
                        imported += ok;
                        failed += ko;
                        if (message is not null && logged++ < 50) ((IProgress<string>)logger).Report(message);
                        batch.Clear();
                        ((IProgress<(long, long, long)>)progress).Report((total, imported, failed));
                    }
                    foreach (var row in source.Rows)
                    {
                        token.ThrowIfCancellationRequested();
                        total++;
                        batch.Add(sourceIndexes.Select(index =>
                        {
                            var value = index >= 0 && index < row.Length ? row[index] : null;
                            return (object?)(emptyAsNull && string.IsNullOrEmpty(value) ? null : value);
                        }).ToArray());
                        if (batch.Count >= batchSize) await Flush();
                    }
                    await Flush();
                    if (useTransaction) await provider.ExecuteAsync(connection, "COMMIT", 0, token);
                }
                catch
                {
                    if (useTransaction)
                    {
                        try { await provider.ExecuteAsync(connection, "ROLLBACK", 0, CancellationToken.None); }
                        catch { /* connessione già chiusa */ }
                        imported = 0;
                    }
                    throw;
                }
                return (imported, failed, total);
            }, token);
            ProgressText = $"{read:N0} letti · {ImportedRows:N0} importati · {errors:N0} errori · {timer.Elapsed.TotalSeconds:0.0} s";
            AddLog($"Completato: {ImportedRows:N0} record importati{(errors > 0 ? $", {errors:N0} righe scartate" : string.Empty)}.");
            IsFinished = true;
        }
        catch (OperationCanceledException)
        {
            AddLog(UseTransaction ? "Importazione interrotta: nessuna modifica salvata." : "Importazione interrotta.");
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            AddLog($"ERRORE: {exception.Message}");
            if (UseTransaction) AddLog("Transazione annullata: nessun record importato.");
        }
        finally { IsRunning = false; }
    }

    /// <summary>Inserisce un blocco; in modalità «continua» riprova riga per riga isolando quelle errate.</summary>
    private async Task<(long Ok, long Failed, string? Message)> InsertBatchAsync(IDialectProvider provider, DbConnection connection, string[] columns,
        List<IReadOnlyList<object?>> batch, bool useTransaction, bool continueOnError, CancellationToken token)
    {
        if (!continueOnError)
        {
            await provider.ExecuteAsync(connection, provider.BuildInsert(_schema, _table, columns, batch), 0, token);
            return (batch.Count, 0, null);
        }
        if (await TryExecuteAsync(provider, connection, provider.BuildInsert(_schema, _table, columns, batch), useTransaction, token) is null) return (batch.Count, 0, null);
        long ok = 0, failed = 0;
        string? first = null;
        foreach (var row in batch)
        {
            var error = await TryExecuteAsync(provider, connection, provider.BuildInsert(_schema, _table, columns, [row]), useTransaction, token);
            if (error is null) ok++;
            else
            {
                failed++;
                first ??= $"Riga scartata: {error}";
            }
        }
        return (ok, failed, first);
    }

    private static async Task<string?> TryExecuteAsync(IDialectProvider provider, DbConnection connection, string sql, bool useSavepoint, CancellationToken token)
    {
        // In PostgreSQL un errore invalida la transazione: un SAVEPOINT permette di proseguire.
        if (useSavepoint) await provider.ExecuteAsync(connection, "SAVEPOINT nf_import", 0, token);
        try
        {
            await provider.ExecuteAsync(connection, sql, 0, token);
            if (useSavepoint) await provider.ExecuteAsync(connection, "RELEASE SAVEPOINT nf_import", 0, token);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (useSavepoint) await provider.ExecuteAsync(connection, "ROLLBACK TO SAVEPOINT nf_import", 0, token);
            return exception.Message;
        }
    }

    // ───────────── Anteprima e mappatura ─────────────

    private void LoadSheets()
    {
        Sheets.Clear();
        if (!IsExcel || !File.Exists(Path)) return;
        try
        {
            foreach (var sheet in DataImporter.ListSheets(Path)) Sheets.Add(sheet);
            SelectedSheet = Sheets.FirstOrDefault();
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }

    private void RefreshPreview()
    {
        PreviewColumns.Clear();
        PreviewRows.Clear();
        _fields = [];
        PreviewInfo = string.Empty;
        if (string.IsNullOrWhiteSpace(Path) || !File.Exists(Path)) return;
        try
        {
            var source = DataImporter.Open(ReadOptions());
            _fields = source.Fields;
            foreach (var field in source.Fields) PreviewColumns.Add(new ColumnInfo(field, "text", true));
            foreach (var row in source.Rows.Take(20)) PreviewRows.Add(new TableRowViewModel(row.Take(source.Fields.Count).Cast<object?>()));
            PreviewInfo = $"{source.Fields.Count} campi rilevati · anteprima delle prime {PreviewRows.Count} righe";
            ErrorMessage = string.Empty;
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Impossibile leggere il file: {exception.Message}";
        }
    }

    private void BuildMappings()
    {
        var fields = new[] { ColumnMapping.Ignore }.Concat(_fields).ToArray();
        Mappings.Clear();
        foreach (var column in _columns) Mappings.Add(new ColumnMapping(column, fields));
        AutoMap();
        if (!Mappings.Any(mapping => mapping.IsMapped) && !FirstRowIsHeader)
            for (var index = 0; index < Math.Min(Mappings.Count, _fields.Count); index++) Mappings[index].Source = _fields[index];
    }

    private ImportReadOptions ReadOptions()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new ImportReadOptions
        {
            Format = SelectedFormat.Format ?? ImportFormat.Csv,
            Path = Path,
            Encoding = SelectedEncoding switch { "UTF-16 LE" => Encoding.Unicode, "Windows-1252" => Encoding.GetEncoding(1252), _ => new UTF8Encoding(false) },
            Delimiter = SelectedDelimiter switch { "Punto e virgola (;)" => ';', "Tabulazione" => '\t', "Barra verticale (|)" => '|', _ => ',' },
            Qualifier = SelectedQualifier == "(nessuno)" ? null : SelectedQualifier[0],
            FirstRowIsHeader = FirstRowIsHeader,
            SkipRows = (int)Math.Max(0, SkipRows ?? 0),
            Sheet = SelectedSheet
        };
    }

    private bool ValidateStep()
    {
        ErrorMessage = string.Empty;
        switch (Step)
        {
            case 1 when !SelectedFormat.IsAvailable:
                ErrorMessage = "Scegli un formato disponibile.";
                return false;
            case 2 when !File.Exists(Path):
                ErrorMessage = "Seleziona un file esistente.";
                return false;
            case 2:
                RefreshPreview();
                if (_fields.Count == 0)
                {
                    ErrorMessage = ErrorMessage.Length > 0 ? ErrorMessage : "Il file non contiene campi leggibili.";
                    return false;
                }
                return true;
            case 3 when !Mappings.Any(mapping => mapping.IsMapped):
                ErrorMessage = "Associa almeno una colonna.";
                return false;
        }
        return true;
    }

    private static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());

    private void AddLog(string message) => Log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
}
