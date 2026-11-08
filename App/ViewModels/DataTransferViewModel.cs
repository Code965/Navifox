using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core;
using Navifox.Core.Models;
using Navifox.Core.DataGen;
using Navifox.Dialects;

namespace Navifox.App.ViewModels;

/// <summary>Riga del registro del trasferimento: info, sql (comando eseguito), ok, error.</summary>
public sealed record TransferLogEntry(DateTime Time, string Kind, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public bool IsSql => Kind == "sql";
    public bool IsOk => Kind == "ok";
    public bool IsError => Kind == "error";
    public bool IsInfo => Kind == "info";
    public override string ToString() => $"[{TimeText}] {(IsSql ? "SQL  " : IsError ? "ERR  " : IsOk ? "OK   " : "     ")}{Text}";
}

/// <summary>Avanzamento del trasferimento: percentuale per righe (o per tabelle se il conteggio non è disponibile), velocità e tempo stimato.</summary>
public sealed class TransferProgress(long?[] expected, bool byRows)
{
    private readonly long[] _done = new long[expected.Length];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _tablesDone;
    private int _current;

    private long TotalRows => expected.Sum(e => e ?? 0);
    public long DoneRows => _done.Sum();
    public long? Expected(int table) => byRows ? expected[table] : null;
    public string Elapsed => Format(_clock.Elapsed);

    public void StartTable(int table) => _current = table;
    public void Advance(int table, long rows) => _done[table] = rows;
    public void FinishTable(int table, long rows)
    {
        _done[table] = Math.Max(rows, _done[table]);
        if (expected[table] is { } n && rows < n) expected[table] = rows; // tabella cambiata o in errore: il totale si adegua
        _tablesDone = table + 1;
    }

    public double Percent
    {
        get
        {
            if (byRows && TotalRows > 0) return Math.Min(100, DoneRows * 100d / TotalRows);
            return expected.Length == 0 ? 100 : _tablesDone * 100d / expected.Length;
        }
    }

    public string TableLabel(int table, long rows) =>
        expected[table] is { } n && n > 0 ? $"{rows:N0} / {n:N0} ({Math.Min(100, rows * 100d / n):0}%)" : $"{rows:N0} righe";

    public string Describe()
    {
        var parts = new List<string> { $"{Percent:0.0}%" };
        if (byRows && TotalRows > 0) parts.Add($"{DoneRows:N0} / {TotalRows:N0} righe");
        parts.Add($"tabella {Math.Min(_current + 1, expected.Length)}/{expected.Length}");
        var seconds = _clock.Elapsed.TotalSeconds;
        if (seconds > 1 && DoneRows > 0)
        {
            var rate = DoneRows / seconds;
            parts.Add($"{rate:N0} righe/s");
            if (byRows && TotalRows > DoneRows) parts.Add($"mancano ~{Format(TimeSpan.FromSeconds((TotalRows - DoneRows) / rate))}");
        }
        parts.Add($"trascorsi {Elapsed}");
        return string.Join("  ·  ", parts);
    }

    private static string Format(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes:00}m" : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes}m {time.Seconds:00}s" : $"{time.Seconds}s";
}

public partial class TransferTable(string name, bool isView) : ObservableObject
{
    public string Name { get; } = name;
    public bool IsView { get; } = isView;
    public string Glyph => IsView ? "eye" : "table";
    [ObservableProperty] private bool _isSelected = !isView;
    [ObservableProperty] private string _state = string.Empty;
}

/// <summary>Strumenti › Trasferimento dati: copia struttura e/o dati di tabelle tra database o connessioni dello stesso tipo.</summary>
public partial class DataTransferViewModel : ObservableObject
{
    private readonly IDatabaseAccessService _databaseAccess;
    private CancellationTokenSource? _running;

    private readonly IDialogService? _dialogs;

    private IReadOnlyCollection<string>? _preselect;
    private bool _jumpToSummary;

    /// <summary>
    /// <paramref name="target"/>, <paramref name="targetDatabase"/> e <paramref name="preselect"/> preimpostano il trasferimento
    /// (es. dopo «Incolla» su un altro database); con <paramref name="jumpToSummary"/> si apre direttamente il riepilogo.
    /// </summary>
    public DataTransferViewModel(IDatabaseAccessService databaseAccess, IReadOnlyList<ConnectionProfile> profiles, ConnectionProfile? source, string? sourceDatabase,
        IDialogService? dialogs = null, ConnectionProfile? target = null, string? targetDatabase = null, IReadOnlyCollection<string>? preselect = null, bool jumpToSummary = false)
    {
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        Profiles = profiles;
        _preselect = preselect;
        _jumpToSummary = jumpToSummary;
        _pendingTargetDatabase = targetDatabase;
        _sourceProfile = source ?? profiles.FirstOrDefault();
        RefreshTargets();
        _targetProfile = target is not null && TargetProfiles.Contains(target) ? target : _sourceProfile;
        _pendingSourceDatabase = sourceDatabase;
        _ = LoadDatabasesAsync(true);
        _ = LoadDatabasesAsync(false);
        _ = LoadInfoAsync(true);
        _ = LoadInfoAsync(false);
    }

    public event Action? CloseRequested;

    // ───────────── Passi: 1 origine/destinazione · 2 tabelle · 3 riepilogo ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(StepTitle), nameof(CanGoBack), nameof(IsLastStep))]
    private int _step = 1;
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsLastStep => Step == 3;
    public bool CanGoBack => Step > 1 && !IsRunning;
    public string StepTitle => Step switch
    {
        1 => "1 · Origine e destinazione",
        2 => "2 · Tabelle e opzioni",
        _ => "3 · Riepilogo e avvio"
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetIsConnection), nameof(TargetTitle), nameof(TargetSubtitle))]
    private bool _targetIsFile;
    public bool TargetIsConnection
    {
        get => !TargetIsFile;
        set => TargetIsFile = !value;
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetTitle), nameof(TargetSubtitle))]
    private string _targetFile = string.Empty;

    public ObservableCollection<PropertyItem> SourceInfo { get; } = [];
    public ObservableCollection<PropertyItem> TargetInfo { get; } = [];
    public string SourceTitle => SourceProfile?.Name ?? "Origine";
    public string SourceSubtitle => SourceDatabase ?? "Database";
    public string TargetTitle => TargetIsFile ? "File SQL" : TargetProfile?.Name ?? "Destinazione";
    public string TargetSubtitle => TargetIsFile ? (TargetFile.Length == 0 ? "Scegli il file" : Path.GetFileName(TargetFile)) : TargetDatabase ?? "Database";
    public string Summary => BuildSummary();

    partial void OnStepChanged(int value)
    {
        if (value == 3) OnPropertyChanged(nameof(Summary));
    }

    partial void OnSourceDatabaseChanged(string? value)
    {
        OnPropertyChanged(nameof(SourceSubtitle));
        _ = LoadTablesAsync(value);
    }
    partial void OnTargetDatabaseChanged(string? value) => OnPropertyChanged(nameof(TargetSubtitle));

    [RelayCommand]
    private void Next()
    {
        StatusMessage = string.Empty;
        if (Step == 1)
        {
            if (SourceProfile is null || SourceDatabase is null) { StatusMessage = "Scegli la connessione e il database di origine."; return; }
            if (TargetIsFile && string.IsNullOrWhiteSpace(TargetFile)) { StatusMessage = "Scegli il file di destinazione."; return; }
            if (!TargetIsFile && (TargetProfile is null || TargetDatabase is null)) { StatusMessage = "Scegli la connessione e il database di destinazione."; return; }
            if (!TargetIsFile && TargetProfile!.Id == SourceProfile.Id && TargetDatabase == SourceDatabase) { StatusMessage = "Origine e destinazione coincidono."; return; }
        }
        if (Step == 2 && !Tables.Any(t => t.IsSelected && !t.IsView)) { StatusMessage = "Seleziona almeno una tabella."; return; }
        Step = Math.Min(3, Step + 1);
    }

    [RelayCommand]
    private void Back() => Step = Math.Max(1, Step - 1);

    /// <summary>Scambia origine e destinazione (solo se la destinazione è una connessione).</summary>
    [RelayCommand]
    private void Swap()
    {
        if (TargetIsFile || TargetProfile is null || TargetDatabase is null) return;
        var (profile, database) = (TargetProfile, TargetDatabase);
        var (oldProfile, oldDatabase) = (SourceProfile, SourceDatabase);
        _pendingSourceDatabase = database;
        _pendingTargetDatabase = oldDatabase;
        SourceProfile = profile;
        if (SourceProfile == oldProfile) _ = LoadDatabasesAsync(true);
        TargetProfile = oldProfile;
        if (TargetProfile == profile) _ = LoadDatabasesAsync(false);
    }


    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        if (_dialogs is null) return;
        var path = await _dialogs.PickSaveFileAsync("Trasferisci in un file SQL", $"{SourceDatabase ?? "export"}_{DateTime.Now:yyyyMMdd_HHmm}.sql", "sql", new FileFilter("Script SQL", "*.sql"));
        if (path is not null) TargetFile = path;
    }

    private async Task LoadInfoAsync(bool source)
    {
        var list = source ? SourceInfo : TargetInfo;
        var profile = source ? SourceProfile : TargetProfile;
        var generation = source ? ++_sourceInfoLoads : ++_targetInfoLoads;
        list.Clear();
        if (profile is null) return;
        list.Add(new PropertyItem("Tipo di connessione", Navifox.App.Controls.DialectVisuals.DialectName(profile)));
        list.Add(new PropertyItem("Nome connessione", profile.Name));
        list.Add(new PropertyItem("Cartella", profile.Folder));
        list.Add(new PropertyItem("Host", profile.Host));
        list.Add(new PropertyItem("Porta", profile.EffectivePort.ToString()));
        list.Add(new PropertyItem("Utente", string.IsNullOrWhiteSpace(profile.UserName) ? "—" : profile.UserName));
        try
        {
            var server = await _databaseAccess.GetPropertiesAsync(profile, null, null, null, CancellationToken.None);
            if (generation != (source ? _sourceInfoLoads : _targetInfoLoads)) return;
            var version = server.FirstOrDefault(p => p.Key == "Versione server").Value ?? "—";
            list.Add(new PropertyItem("Versione server", version.Length > 70 ? version[..70] + "…" : version));
        }
        catch (Exception exception) { list.Add(new PropertyItem("Versione server", $"non disponibile ({exception.Message})")); }
    }

    private string BuildSummary()
    {
        var selected = Tables.Where(t => t.IsSelected && !t.IsView).Select(t => t.Name).ToArray();
        var target = TargetIsFile ? $"File SQL: {TargetFile}" : $"{TargetProfile?.Name} ({Navifox.App.Controls.DialectVisuals.Summary(TargetProfile)}) · database {TargetDatabase}";
        var options = new List<string>();
        if (CreateTables) options.Add(TargetIsFile ? "struttura (DROP + CREATE)" : "ricrea le tabelle");
        if (CopyData) options.Add("dati");
        if (CopyData && AnonymizeData) options.Add("maschera i campi personali riconosciuti (chiavi invariate)");
        if (!CreateTables && EmptyTarget && !TargetIsFile) options.Add("svuota prima");
        if (ContinueOnError) options.Add("continua in caso di errore");
        return $"""
            Origine:       {SourceProfile?.Name} ({Navifox.App.Controls.DialectVisuals.Summary(SourceProfile)}) · database {SourceDatabase}
            Destinazione:  {target}
            Tabelle:       {selected.Length}
            Opzioni:       {string.Join(", ", options)}

            {string.Join(Environment.NewLine, selected.Select(name => "  • " + name))}
            """;
    }
    public IReadOnlyList<ConnectionProfile> Profiles { get; }
    public ObservableCollection<string> SourceDatabases { get; } = [];
    public ObservableCollection<string> TargetDatabases { get; } = [];
    public ObservableCollection<TransferTable> Tables { get; } = [];
    public ObservableCollection<TransferTable> VisibleTables { get; } = [];
    /// <summary>Destinazioni possibili: solo connessioni dello stesso tipo dell'origine.</summary>
    public ObservableCollection<ConnectionProfile> TargetProfiles { get; } = [];
    [ObservableProperty] private string _tableFilter = string.Empty;
    public string SelectionLabel => $"{Tables.Count(t => t.IsSelected)} di {Tables.Count(t => !t.IsView)} tabelle selezionate";

    partial void OnTableFilterChanged(string value) => ApplyTableFilter();

    private void ApplyTableFilter()
    {
        var query = TableFilter.Trim();
        VisibleTables.Clear();
        foreach (var table in Tables.Where(t => query.Length == 0 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase))) VisibleTables.Add(table);
        OnPropertyChanged(nameof(SelectionLabel));
    }

    private void RefreshTargets()
    {
        var kind = SourceProfile?.Kind;
        var current = TargetProfile;
        TargetProfiles.Clear();
        foreach (var profile in Profiles.Where(p => kind is null || p.Kind == kind)) TargetProfiles.Add(profile);
        TargetProfile = current is not null && TargetProfiles.Contains(current) ? current : SourceProfile;
    }
    public ObservableCollection<TransferLogEntry> Log { get; } = [];
    private string? _pendingSourceDatabase;
    private string? _pendingTargetDatabase;
    private int _sourceLoads;
    private int _targetLoads;
    private int _sourceInfoLoads;
    private int _targetInfoLoads;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceTitle))]
    private ConnectionProfile? _sourceProfile;
    [ObservableProperty] private ConnectionProfile? _targetProfile;
    [ObservableProperty] private string? _sourceDatabase;
    [ObservableProperty] private string? _targetDatabase;
    [ObservableProperty] private bool _createTables = true;
    [ObservableProperty] private bool _copyData = true;
    [ObservableProperty] private bool _anonymizeData;
    [ObservableProperty] private bool _emptyTarget;
    [ObservableProperty] private bool _continueOnError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _statusMessage = "Scegli origine, destinazione e tabelle.";

    partial void OnSourceProfileChanged(ConnectionProfile? value)
    {
        RefreshTargets();
        OnPropertyChanged(nameof(SourceTitle));
        _ = LoadDatabasesAsync(true);
        _ = LoadInfoAsync(true);
    }
    partial void OnTargetProfileChanged(ConnectionProfile? value)
    {
        OnPropertyChanged(nameof(TargetTitle));
        _ = LoadDatabasesAsync(false);
        _ = LoadInfoAsync(false);
    }

    private static string SchemaFor(ConnectionProfile profile, string database) => profile.Kind.DefaultSchema(database);

    private async Task LoadDatabasesAsync(bool source)
    {
        var profile = source ? SourceProfile : TargetProfile;
        var list = source ? SourceDatabases : TargetDatabases;
        // Valore da mantenere: preimpostato oppure quello già scelto (più caricamenti possono sovrapporsi).
        var keep = source ? _pendingSourceDatabase ?? SourceDatabase : _pendingTargetDatabase ?? TargetDatabase;
        var generation = source ? ++_sourceLoads : ++_targetLoads;
        list.Clear();
        if (profile is null) return;
        try
        {
            var names = await _databaseAccess.ListDatabasesAsync(profile, CancellationToken.None);
            if (generation != (source ? _sourceLoads : _targetLoads)) return; // superato da un caricamento più recente
            list.Clear();
            foreach (var database in names) list.Add(database);
            if (source)
            {
                SourceDatabase = keep is not null && list.Contains(keep) ? keep : list.FirstOrDefault();
                _pendingSourceDatabase = null;
            }
            else
            {
                TargetDatabase = keep is not null && list.Contains(keep) ? keep : list.FirstOrDefault(d => d != SourceDatabase) ?? list.FirstOrDefault();
                _pendingTargetDatabase = null;
            }
        }
        catch (Exception exception) { StatusMessage = $"{profile.Name}: {exception.Message}"; }
    }

    private async Task LoadTablesAsync(string? database)
    {
        Tables.Clear();
        ApplyTableFilter();
        if (SourceProfile is null || database is null) return;
        try
        {
            var objects = await _databaseAccess.ListTablesAsync(SourceProfile, database, SchemaFor(SourceProfile, database), CancellationToken.None);
            if (database != SourceDatabase) return;
            foreach (var item in objects)
            {
                var table = new TransferTable(item.Name, item.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase));
                table.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(TransferTable.IsSelected)) OnPropertyChanged(nameof(SelectionLabel));
                };
                Tables.Add(table);
            }
            if (_preselect is { } wanted)
            {
                foreach (var table in Tables) table.IsSelected = !table.IsView && wanted.Contains(table.Name, StringComparer.Ordinal);
                _preselect = null;
            }
            ApplyTableFilter();
            StatusMessage = $"{Tables.Count(t => !t.IsView)} tabelle in {database}";
            if (_jumpToSummary)
            {
                _jumpToSummary = false;
                // attende il database di destinazione prima di mostrare il riepilogo
                for (var i = 0; i < 50 && TargetDatabase is null && !TargetIsFile; i++) await Task.Delay(100);
                Step = 3;
                OnPropertyChanged(nameof(Summary));
            }
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    [RelayCommand]
    private void SelectAll(string? value)
    {
        // «Tutte/Nessuna» agiscono sulle tabelle visibili con il filtro corrente.
        foreach (var table in VisibleTables.Where(t => !t.IsView)) table.IsSelected = value != "none";
    }

    [RelayCommand]
    private void Close()
    {
        _running?.Cancel();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Stop() => _running?.Cancel();

    [RelayCommand]
    private async Task StartAsync()
    {
        if (TargetIsFile)
        {
            await ExportToFileAsync();
            return;
        }
        if (IsRunning || SourceProfile is not { } source || TargetProfile is not { } target || SourceDatabase is not { } sourceDb || TargetDatabase is not { } targetDb) return;
        if (source.Kind != target.Kind)
        {
            StatusMessage = "Origine e destinazione devono essere dello stesso tipo di database.";
            return;
        }
        if (source.Id == target.Id && sourceDb == targetDb)
        {
            StatusMessage = "Origine e destinazione coincidono.";
            return;
        }
        var selected = Tables.Where(t => t.IsSelected && !t.IsView).ToArray();
        if (selected.Length == 0)
        {
            StatusMessage = "Seleziona almeno una tabella.";
            return;
        }
        if (!await SqlGuard.ConfirmProductionAsync(_dialogs, target,
                $"Il trasferimento scriverà {selected.Length} tabelle nel database «{targetDb}»{(CreateTables ? " eliminando e ricreando quelle esistenti" : string.Empty)}.")) return;
        IsRunning = true;
        Log.Clear();
        Progress = 0;
        ProgressText = string.Empty;
        _running = new CancellationTokenSource();
        var token = _running.Token;
        var provider = _databaseAccess.Provider(source);
        var mysql = provider.UsesMySqlScriptSyntax;
        var sourceSchema = SchemaFor(source, sourceDb);
        var targetSchema = SchemaFor(target, targetDb);
        var errors = 0;
        var tableErrors = 0;
        var postData = new List<string>();
        AddLog($"Trasferimento {source.Name}/{sourceDb} → {target.Name}/{targetDb}: {selected.Length} tabelle");
        ActivityRecording.ActivityOperation? activity = null;
        try
        {
            activity = ActivityRecording.BeginOperation(target, targetDb, "Trasferimento dati",
                $"{source.Name}/{sourceDb} → {target.Name}/{targetDb}\nTabelle: {string.Join(", ", selected.Select(t => t.Name))}\nCrea struttura: {CreateTables}; copia dati: {CopyData}; mascheramento: {AnonymizeData}; continua con errori: {ContinueOnError}");
            await using var from = await _databaseAccess.OpenAsync(source, sourceDb, token);
            await using var to = await _databaseAccess.OpenAsync(target, targetDb, token);
            var maskers = await PrepareMaskersAsync(provider, from, sourceDb, sourceSchema, selected, token);
            if (!await ConfirmMaskingAsync(maskers)) { activity?.Fail(new OperationCanceledException("Mascheramento non confermato.")); StatusMessage = "Trasferimento annullato."; return; }
            var tracker = await PrepareProgressAsync(provider, from, sourceSchema, selected, token);

            async Task Run(string sql, string? display = null)
            {
                AddSql(display ?? sql);
                await provider.ExecuteAsync(to, sql, 0, token);
            }

            if (mysql) await Run("SET FOREIGN_KEY_CHECKS = 0");
            // Come nel dump SQLite: le tabelle possono essere elencate prima dei rispettivi padri.
            if (source.Kind == DatabaseKind.Sqlite) await Run("PRAGMA foreign_keys = OFF");
            for (var i = 0; i < selected.Length; i++)
            {
                var table = selected[i];
                token.ThrowIfCancellationRequested();
                table.State = "in corso…";
                tracker.StartTable(i);
                StatusMessage = $"{table.Name} ({i + 1}/{selected.Length})";
                AddLog($"── {table.Name} ({i + 1}/{selected.Length}){(tracker.Expected(i) is { } n ? $" · {n:N0} righe" : string.Empty)}");
                try
                {
                    var name = mysql ? provider.QuoteIdentifier(table.Name) : $"{provider.QuoteIdentifier(targetSchema)}.{provider.QuoteIdentifier(table.Name)}";
                    if (CreateTables)
                    {
                        var ddl = await provider.GetDdlAsync(from, sourceDb, sourceSchema, table.Name, false, token);
                        await Run($"DROP TABLE IF EXISTS {name}{(source.Kind == DatabaseKind.PostgreSql ? " CASCADE" : string.Empty)}");
                        foreach (var statement in provider.SplitScript(ddl.Create)) await Run(statement);
                        postData.AddRange(ddl.PostData);
                    }
                    else if (EmptyTarget && CopyData) await Run($"DELETE FROM {name}");

                    long rows = 0;
                    if (CopyData)
                    {
                        var columns = (await provider.GetColumnsAsync(from, sourceDb, sourceSchema, table.Name, token)).Where(c => !c.IsGenerated).Select(c => c.Name).ToArray();
                        var head = $"INSERT INTO {name} ({string.Join(", ", columns.Select(provider.QuoteIdentifier))}) VALUES";
                        var batch = new List<IReadOnlyList<object?>>(BatchSize);
                        async Task Flush()
                        {
                            rows += batch.Count;
                            await Run(provider.BuildInsert(targetSchema, table.Name, columns, batch), $"{head} … {batch.Count:N0} righe  →  {tracker.TableLabel(i, rows)}");
                            batch.Clear();
                            tracker.Advance(i, rows);
                            table.State = tracker.TableLabel(i, rows);
                            UpdateProgress(tracker);
                        }
                        // Una transazione per tabella: molto più veloce (soprattutto su server remoti) e, in caso di errore, nessuna tabella a metà.
                        await Run(source.Kind.BeginTransactionSql());
                        // SQL Server: per copiare i valori delle colonne IDENTITY serve IDENTITY_INSERT.
                        if (source.Kind == DatabaseKind.SqlServer)
                            await Run($"IF OBJECTPROPERTY(OBJECT_ID({provider.FormatLiteral(name)}), 'TableHasIdentity') = 1 SET IDENTITY_INSERT {name} ON");
                        try
                        {
                            await foreach (var row in provider.StreamQueryAsync(from, SelectTransferColumns(provider, sourceSchema, table.Name, columns), _ => { }, token))
                            {
                                batch.Add(maskers.GetValueOrDefault(table.Name)?.Mask(row) ?? row);
                                if (batch.Count >= BatchSize) await Flush();
                            }
                            if (batch.Count > 0) await Flush();
                            if (source.Kind == DatabaseKind.SqlServer)
                                await Run($"IF OBJECTPROPERTY(OBJECT_ID({provider.FormatLiteral(name)}), 'TableHasIdentity') = 1 SET IDENTITY_INSERT {name} OFF");
                            await Run("COMMIT");
                        }
                        catch
                        {
                            try { await provider.ExecuteAsync(to, "ROLLBACK", 0, CancellationToken.None); AddLog("ROLLBACK: i dati di questa tabella non sono stati scritti."); } catch { }
                            throw;
                        }
                    }
                    tracker.FinishTable(i, rows);
                    table.State = CopyData ? $"✓ {rows:N0} righe" : "✓ struttura";
                    AddOk($"{table.Name}: {(CreateTables ? "struttura creata, " : string.Empty)}{rows:N0} righe copiate");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    errors++;
                    tableErrors++;
                    table.State = "errore";
                    tracker.FinishTable(i, 0);
                    AddError($"{table.Name}: {exception.Message}");
                    if (!ContinueOnError) throw;
                }
                UpdateProgress(tracker);
            }
            if (postData.Count > 0) AddLog($"── Vincoli, indici e sequenze ({postData.Count})");
            foreach (var statement in postData)
            {
                try { await Run(statement); }
                catch (Exception exception) when (ContinueOnError) { errors++; AddError($"Vincolo non applicato: {exception.Message}"); }
            }
            if (mysql) await Run("SET FOREIGN_KEY_CHECKS = 1");
            if (source.Kind == DatabaseKind.Sqlite)
            {
                foreach (var table in selected)
                {
                    var check = $"PRAGMA {provider.QuoteIdentifier(targetSchema)}.foreign_key_check({provider.QuoteIdentifier(table.Name)})";
                    if ((await provider.ExecuteAsync(to, check, 1, token)).Rows.Count > 0)
                        throw new InvalidOperationException($"La copia di «{table.Name}» contiene relazioni non valide. Controlla le tabelle collegate e il registro attività.");
                }
                await Run("PRAGMA foreign_keys = ON");
            }
            Progress = 100;
            ProgressText = $"100% · {tracker.DoneRows:N0} righe · {selected.Length} tabelle · {tracker.Elapsed}";
            StatusMessage = $"Completato: {selected.Length - tableErrors} tabelle, {tracker.DoneRows:N0} righe in {tracker.Elapsed}{(errors > 0 ? $", {errors} errori" : string.Empty)}.";
            if (errors > 0) AddError(StatusMessage); else AddOk(StatusMessage);
            SqlHistory.Record(target.Name, targetDb, $"-- Trasferimento dati da {source.Name}/{sourceDb}: {selected.Length} tabelle, {tracker.DoneRows} righe", TimeSpan.Zero, errors > 0 ? $"{errors} errori" : null);
            activity?.Complete(errors > 0 ? StatusMessage : null);
        }
        catch (OperationCanceledException error) { activity?.Fail(error); StatusMessage = "Trasferimento interrotto."; AddError(StatusMessage); }
        catch (Exception exception) { activity?.Fail(exception); StatusMessage = $"Trasferimento interrotto: {exception.Message}"; AddError(StatusMessage); }
        finally { activity?.Dispose(); IsRunning = false; }
    }

    /// <summary>Conta le righe da copiare (per la percentuale per riga) e prepara il calcolo di velocità e tempo stimato.</summary>
    private async Task<TransferProgress> PrepareProgressAsync(Core.IDialectProvider provider, System.Data.Common.DbConnection from, string schema, IReadOnlyList<TransferTable> tables, CancellationToken token)
    {
        var expected = new long?[tables.Count];
        if (CopyData)
        {
            AddLog("Conteggio delle righe da copiare…");
            for (var i = 0; i < tables.Count; i++)
            {
                try { expected[i] = await provider.CountRowsAsync(from, schema, tables[i].Name, null, token); }
                catch (OperationCanceledException) { throw; }
                catch { expected[i] = null; }
                ProgressText = $"Conteggio righe… {i + 1}/{tables.Count}";
            }
            AddLog($"Da copiare: {expected.Sum(e => e ?? 0):N0} righe in {tables.Count} tabelle");
        }
        return new TransferProgress(expected, CopyData);
    }

    private void UpdateProgress(TransferProgress tracker)
    {
        Progress = tracker.Percent;
        ProgressText = tracker.Describe();
    }

    /// <summary>Destinazione «File»: script SQL con struttura e/o dati delle tabelle selezionate.</summary>
    private async Task ExportToFileAsync()
    {
        if (IsRunning || SourceProfile is not { } source || SourceDatabase is not { } sourceDb || string.IsNullOrWhiteSpace(TargetFile)) return;
        var selected = Tables.Where(t => t.IsSelected && !t.IsView).ToArray();
        IsRunning = true;
        Log.Clear();
        Progress = 0;
        ProgressText = string.Empty;
        _running = new CancellationTokenSource();
        var token = _running.Token;
        var provider = _databaseAccess.Provider(source);
        var mysql = provider.UsesMySqlScriptSyntax;
        var schema = SchemaFor(source, sourceDb);
        AddLog($"Esportazione di {selected.Length} tabelle in {TargetFile}");
        ActivityRecording.ActivityOperation? activity = null;
        try
        {
            activity = ActivityRecording.BeginOperation(source, sourceDb, "Esportazione dati", $"{source.Name}/{sourceDb} → {TargetFile}\nTabelle: {string.Join(", ", selected.Select(t => t.Name))}\nStruttura: {CreateTables}; dati: {CopyData}; mascheramento: {AnonymizeData}");
            await using var connection = await _databaseAccess.OpenAsync(source, sourceDb, token);
            var maskers = await PrepareMaskersAsync(provider, connection, sourceDb, schema, selected, token);
            if (!await ConfirmMaskingAsync(maskers)) { activity?.Fail(new OperationCanceledException("Mascheramento non confermato.")); StatusMessage = "Esportazione annullata."; return; }
            var tracker = await PrepareProgressAsync(provider, connection, schema, selected, token);
            await using var writer = new StreamWriter(TargetFile, false, new System.Text.UTF8Encoding(false));
            await ActivityRecording.WriteScriptAsync(writer, provider.DumpHeader(sourceDb));
            var postData = new List<string>();
            for (var i = 0; i < selected.Length; i++)
            {
                var table = selected[i];
                token.ThrowIfCancellationRequested();
                table.State = "in corso…";
                tracker.StartTable(i);
                StatusMessage = $"{table.Name} ({i + 1}/{selected.Length})";
                AddLog($"── {table.Name} ({i + 1}/{selected.Length}){(tracker.Expected(i) is { } n ? $" · {n:N0} righe" : string.Empty)}");
                var name = mysql ? provider.QuoteIdentifier(table.Name) : $"{provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(table.Name)}";
                if (CreateTables)
                {
                    var ddl = await provider.GetDdlAsync(connection, sourceDb, schema, table.Name, false, token);
                    var drop = $"DROP TABLE IF EXISTS {name}{(source.Kind == DatabaseKind.PostgreSql ? " CASCADE" : string.Empty)};";
                    AddSql(drop);
                    AddSql(ddl.Create);
                    await ActivityRecording.WriteScriptAsync(writer, $"\n-- Tabella {name}\n{drop}\n{ddl.Create}\n");
                    postData.AddRange(ddl.PostData);
                }
                long rows = 0;
                if (CopyData)
                {
                    var columns = (await provider.GetColumnsAsync(connection, sourceDb, schema, table.Name, token)).Where(c => !c.IsGenerated).Select(c => c.Name).ToArray();
                    var head = $"INSERT INTO {name} ({string.Join(", ", columns.Select(provider.QuoteIdentifier))}) VALUES";
                    var batch = new List<IReadOnlyList<object?>>(500);
                    async Task Flush()
                    {
                        rows += batch.Count;
                        AddSql($"{head} … {batch.Count:N0} righe  →  {tracker.TableLabel(i, rows)}");
                        await ActivityRecording.WriteScriptAsync(writer, provider.BuildInsert(schema, table.Name, columns, batch));
                        batch.Clear();
                        tracker.Advance(i, rows);
                        table.State = tracker.TableLabel(i, rows);
                        UpdateProgress(tracker);
                    }
                    await foreach (var row in provider.StreamQueryAsync(connection, SelectTransferColumns(provider, schema, table.Name, columns), _ => { }, token))
                    {
                        batch.Add(maskers.GetValueOrDefault(table.Name)?.Mask(row) ?? row);
                        if (batch.Count >= 500) await Flush();
                    }
                    if (batch.Count > 0) await Flush();
                }
                tracker.FinishTable(i, rows);
                table.State = CopyData ? $"✓ {rows:N0} righe" : "✓ struttura";
                AddOk($"{table.Name}: {rows:N0} righe scritte");
                UpdateProgress(tracker);
            }
            if (postData.Count > 0)
            {
                foreach (var statement in postData) AddSql(statement);
                await ActivityRecording.WriteScriptAsync(writer, "\n" + string.Join("\n", postData) + "\n");
            }
            await ActivityRecording.WriteScriptAsync(writer, provider.DumpFooter());
            await writer.FlushAsync();
            Progress = 100;
            ProgressText = $"100% · {tracker.DoneRows:N0} righe · {selected.Length} tabelle · {tracker.Elapsed}";
            StatusMessage = $"Completato: {selected.Length} tabelle, {tracker.DoneRows:N0} righe in {Path.GetFileName(TargetFile)}.";
            AddOk(StatusMessage);
            activity?.Complete();
        }
        catch (OperationCanceledException error) { activity?.Fail(error); StatusMessage = "Esportazione interrotta."; AddError(StatusMessage); }
        catch (Exception exception) { activity?.Fail(exception); StatusMessage = $"Esportazione non riuscita: {exception.Message}"; AddError(StatusMessage); }
        finally { activity?.Dispose(); IsRunning = false; }
    }

    /// <summary>Righe per ogni INSERT multiplo.</summary>
    private const int BatchSize = 1000;

    private void AddLog(string message) { Log.Add(new TransferLogEntry(DateTime.Now, "info", message)); ActivityRecording.Note(message); }
    private void AddSql(string sql) => Log.Add(new TransferLogEntry(DateTime.Now, "sql", sql.Trim()));
    private void AddOk(string message) { Log.Add(new TransferLogEntry(DateTime.Now, "ok", message)); ActivityRecording.Note(message); }
    private void AddError(string message) { Log.Add(new TransferLogEntry(DateTime.Now, "error", message)); ActivityRecording.Note(message, "Failed"); }

    /// <summary>Testo completo del registro (pulsante «Copia registro»).</summary>
    public string LogText => string.Join(Environment.NewLine, Log.Select(entry => entry.ToString()));
}

/// <summary>Strumenti › Opzioni.</summary>
/// <summary>Voce della scelta «Anteprima righe modificate» nelle opzioni.</summary>
public sealed record RowPreviewMode(string Value, string Label)
{
    public override string ToString() => Label;
}

public partial class OptionsViewModel : ObservableObject
{
    public OptionsViewModel()
    {
        var settings = AppSettings.Current;
        _theme = settings.Theme;
        _limitRecords = settings.LimitRecords;
        _pageSize = settings.PageSize;
        _monitorInterval = settings.MonitorInterval;
        _recordHistory = settings.RecordHistory;
        _queryMaxRows = settings.QueryMaxRows;
        _checkUpdatesOnStartup = settings.CheckUpdatesOnStartup;
        _safeMode = settings.SafeMode;
        _conditionalFormatting = settings.ConditionalFormatting;
        _offerCrashReports = settings.OfferCrashReports;
        _rowPreview = RowPreviewModes.FirstOrDefault(m => m.Value == settings.RowPreview) ?? RowPreviewModes[1];
        _restoreSession = settings.RestoreSession;
        _updateSource = settings.UpdateSource;
        _assistantProvider = SqlAssistant.Current;
        LoadAssistantFields();
    }

    // ───────────── Assistente SQL: qualsiasi fornitore, anche locale ─────────────

    public IReadOnlyList<AssistantProvider> AssistantProviders => SqlAssistant.Providers;
    public System.Collections.ObjectModel.ObservableCollection<string> AssistantModelList { get; } = [];
    [ObservableProperty] private AssistantProvider _assistantProvider;
    [ObservableProperty] private string _assistantKey = string.Empty;
    [ObservableProperty] private string _assistantModel = string.Empty;
    [ObservableProperty] private string _assistantBaseUrl = string.Empty;
    [ObservableProperty] private string _assistantStatus = string.Empty;
    [ObservableProperty] private bool _assistantBusy;

    /// <summary>Valori modificati per ogni fornitore (si può cambiare fornitore e tornare indietro senza perderli).</summary>
    private readonly Dictionary<string, (string Key, string Model, string Url)> _assistantDrafts = [];
    private bool _loadingAssistant;

    public bool AssistantShowsUrl => !AssistantProvider.IsAnthropic;
    public bool AssistantShowsKey => !AssistantProvider.IsLocal;
    public string AssistantKeyWatermark => AssistantProvider.KeyHint ?? (AssistantProvider.NeedsKey ? "Chiave API" : "Facoltativa");
    public string AssistantPrivacy => AssistantProvider.IsLocal
        ? "Modello in locale: le richieste restano su questo computer."
        : "Le chiavi sono salvate cifrate solo su questo computer. Al fornitore vengono inviati la query e la struttura del database, mai i dati delle righe.";

    partial void OnAssistantProviderChanging(AssistantProvider value) => RememberAssistantDraft();

    partial void OnAssistantProviderChanged(AssistantProvider value)
    {
        LoadAssistantFields();
        OnPropertyChanged(nameof(AssistantShowsUrl));
        OnPropertyChanged(nameof(AssistantShowsKey));
        OnPropertyChanged(nameof(AssistantKeyWatermark));
        OnPropertyChanged(nameof(AssistantPrivacy));
    }

    private void RememberAssistantDraft()
    {
        if (_loadingAssistant || AssistantProvider is null) return;
        _assistantDrafts[AssistantProvider.Name] = (AssistantKey, AssistantModel, AssistantBaseUrl);
    }

    private void LoadAssistantFields()
    {
        _loadingAssistant = true;
        var provider = AssistantProvider;
        if (_assistantDrafts.TryGetValue(provider.Name, out var draft))
            (AssistantKey, AssistantModel, AssistantBaseUrl) = draft;
        else
        {
            AssistantKey = SqlAssistant.ApiKey(provider) ?? string.Empty;
            AssistantModel = SqlAssistant.Model(provider);
            AssistantBaseUrl = SqlAssistant.BaseUrl(provider);
        }
        AssistantModelList.Clear();
        if (!string.IsNullOrWhiteSpace(AssistantModel)) AssistantModelList.Add(AssistantModel);
        AssistantStatus = string.Empty;
        _loadingAssistant = false;
    }

    [RelayCommand]
    private async Task LoadAssistantModelsAsync()
    {
        AssistantBusy = true;
        AssistantStatus = "Caricamento dei modelli…";
        try
        {
            var models = await SqlAssistant.ListModelsAsync(AssistantProvider, AssistantKey, AssistantBaseUrl, CancellationToken.None);
            var current = AssistantModel;
            AssistantModelList.Clear();
            foreach (var model in models) AssistantModelList.Add(model);
            AssistantModel = models.Contains(current) ? current : models.FirstOrDefault() ?? current;
            AssistantStatus = models.Count == 0 ? "Nessun modello disponibile." : $"{models.Count} modelli disponibili.";
        }
        catch (Exception exception) { AssistantStatus = exception.Message; }
        finally { AssistantBusy = false; }
    }

    /// <summary>Breve domanda di prova con le impostazioni attuali (salvate temporaneamente).</summary>
    [RelayCommand]
    private async Task TestAssistantAsync()
    {
        var settings = AppSettings.Current;
        var backup = (settings.AssistantProvider, new Dictionary<string, string>(settings.AssistantKeys), new Dictionary<string, string>(settings.AssistantModels),
            new Dictionary<string, string>(settings.AssistantBaseUrls), settings.AnthropicApiKeyProtected);
        AssistantBusy = true;
        AssistantStatus = "Prova in corso…";
        try
        {
            SqlAssistant.Save(AssistantProvider, AssistantKey, AssistantModel, AssistantBaseUrl);
            var answer = await SqlAssistant.AskAsync("Rispondi solo con: SELECT 1;", Navifox.Core.Models.DatabaseKind.MySql, null, _ => { }, CancellationToken.None);
            AssistantStatus = $"Funziona · {SqlAssistant.Model(AssistantProvider)} ha risposto: {answer.Trim().Replace('\n', ' ')[..Math.Min(80, answer.Trim().Length)]}";
        }
        catch (Exception exception) { AssistantStatus = exception.Message; }
        finally
        {
            (settings.AssistantProvider, settings.AssistantKeys, settings.AssistantModels, settings.AssistantBaseUrls, settings.AnthropicApiKeyProtected) = backup;
            AssistantBusy = false;
        }
    }

    public string CurrentVersion => $"Versione installata: {UpdateService.CurrentVersion}";
    public string DefaultUpdateSource => string.IsNullOrWhiteSpace(UpdateService.DefaultSource) ? "Nessuna sorgente predefinita" : $"Vuoto = {UpdateService.DefaultSource}";

    public event Action<bool>? CloseRequested;
    public IReadOnlyList<string> Themes { get; } = ["Sistema", "Chiaro", "Scuro"];
    public IReadOnlyList<string> Intervals { get; } = ["Manuale", "2 s", "5 s", "10 s", "30 s"];

    [ObservableProperty] private string _theme;
    [ObservableProperty] private bool _limitRecords;
    [ObservableProperty] private decimal? _pageSize;
    [ObservableProperty] private string _monitorInterval;
    [ObservableProperty] private bool _recordHistory;
    [ObservableProperty] private decimal? _queryMaxRows;
    [ObservableProperty] private bool _checkUpdatesOnStartup;
    [ObservableProperty] private bool _safeMode;
    [ObservableProperty] private bool _conditionalFormatting;
    [ObservableProperty] private bool _offerCrashReports;
    [ObservableProperty] private RowPreviewMode? _rowPreview;

    public IReadOnlyList<RowPreviewMode> RowPreviewModes { get; } =
    [
        new(SqlGuard.PreviewAlways, "Sempre, prima di ogni UPDATE/DELETE"),
        new(SqlGuard.PreviewWithConfirm, "Solo quando Navifox chiede conferma"),
        new(SqlGuard.PreviewNever, "Mai"),
    ];
    [ObservableProperty] private bool _restoreSession;
    [ObservableProperty] private string _updateSource;

    [RelayCommand]
    private void Save()
    {
        var settings = AppSettings.Current;
        settings.Theme = Theme;
        settings.LimitRecords = LimitRecords;
        settings.PageSize = (int)Math.Clamp(PageSize ?? 1000, 1, 1_000_000);
        settings.MonitorInterval = MonitorInterval;
        settings.RecordHistory = RecordHistory;
        settings.QueryMaxRows = (int)Math.Clamp(QueryMaxRows ?? 1000, 1, 1_000_000);
        settings.CheckUpdatesOnStartup = CheckUpdatesOnStartup;
        settings.SafeMode = SafeMode;
        settings.ConditionalFormatting = ConditionalFormatting;
        settings.OfferCrashReports = OfferCrashReports;
        settings.RowPreview = RowPreview?.Value ?? SqlGuard.PreviewWithConfirm;
        settings.RestoreSession = RestoreSession;
        settings.UpdateSource = UpdateSource?.Trim() ?? string.Empty;
        // Fornitore dell'assistente: si salvano anche le modifiche fatte agli altri fornitori durante la sessione.
        RememberAssistantDraft();
        foreach (var (name, draft) in _assistantDrafts)
        {
            var provider = SqlAssistant.Provider(name);
            if (provider != AssistantProvider) SqlAssistant.Save(provider, draft.Key, draft.Model, draft.Url);
        }
        // Se la chiave è quella di una variabile d'ambiente non la si duplica nelle impostazioni.
        var envKey = Environment.GetEnvironmentVariable(AssistantProvider.IsAnthropic ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY");
        SqlAssistant.Save(AssistantProvider, AssistantKey == envKey ? null : AssistantKey, AssistantModel, AssistantBaseUrl);
        settings.Save();
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
