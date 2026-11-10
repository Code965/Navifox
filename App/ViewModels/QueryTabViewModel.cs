using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;
using Navifox.Core;
using Navifox.Dialects;

namespace Navifox.App.ViewModels;

/// <summary>Scheda «Nuova query» (editor + griglia) oppure «Console» (riga di comando con trascrizione testuale).</summary>
public partial class QueryTabViewModel : WorkspaceTabViewModel, Navifox.App.Controls.ISqlCompletionSource
{
    private const int ConsoleMaxRows = 200;
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IDialogService? _dialogs;
    private readonly List<string> _history = [];
    private readonly Dictionary<string, SchemaModel?> _schemaCache = new(StringComparer.OrdinalIgnoreCase);
    private int _historyIndex;
    private CancellationTokenSource? _running;
    private readonly int _number;
    private bool _initializing = true;
    private static int _queryCounter;

    public QueryTabViewModel(IDatabaseAccessService databaseAccess, ConnectionProfile profile, string? database, bool isConsole,
        IReadOnlyList<ConnectionProfile>? profiles = null, IDialogService? dialogs = null)
    {
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        Profiles = profiles ?? [profile];
        _selectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile;
        _selectedDatabase = database;
        IsConsole = isConsole;
        _number = isConsole ? 0 : Interlocked.Increment(ref _queryCounter);
        if (isConsole) _transcript.Append($"Navifox console · {profile.Name} ({profile.Endpoint}){(database is null ? string.Empty : " · " + database)}\nTermina le istruzioni con ';' e premi Invio. ↑/↓ per la cronologia.\n\n");
        ConsoleText = _transcript.ToString();
        _initializing = false;
        _ = LoadDatabasesAsync(database);
    }

    private ConnectionProfile _profile => SelectedProfile ?? Profiles[0];

    // ───────────── Connessione e database (barra in alto) ─────────────

    public IReadOnlyList<ConnectionProfile> Profiles { get; }
    public ObservableCollection<string> Databases { get; } = [];
    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private string? _filePath;

    /// <summary>Query salvata collegata alla scheda (null = non salvata nella libreria).</summary>
    public Guid? SavedQueryId { get; private set; }
    private string? _savedQueryName;

    public void AttachSavedQuery(SavedQuery query)
    {
        SavedQueryId = query.Id;
        _savedQueryName = query.Name;
        FilePath = query.FilePath;
        MarkClean();
        RefreshTitle();
    }

    /// <summary>Testo al momento dell'ultimo salvataggio (o dell'apertura): se cambia, chiudendo si chiede se salvare.</summary>
    private string _cleanText = string.Empty;

    public bool IsDirty => !IsConsole && (SqlText ?? string.Empty).Trim() != _cleanText.Trim();

    /// <summary>Il testo attuale è considerato salvato (query aperta, salvata o generata da Navifox).</summary>
    public void MarkClean() => _cleanText = SqlText ?? string.Empty;

    public override async Task<bool> ConfirmCloseAsync()
    {
        if (!IsDirty || _dialogs is null) return true;
        var dialog = new Views.ChoiceDialog("Query non salvata",
            $"La query «{TabTitle}» contiene modifiche non salvate.\n\nVuoi salvarla prima di chiudere?", ["Salva", "Non salvare", "Annulla"]);
        await _dialogs.ShowDialogAsync(dialog);
        switch (dialog.Choice)
        {
            case 0:
                await SaveToLibraryCoreAsync(false);
                return !IsDirty; // salvataggio annullato o non riuscito: la scheda resta aperta
            case 1:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Salva il testo nella categoria «Query salvate» del database (aggiorna se già salvata).</summary>
    [RelayCommand]
    private Task SaveToLibraryAsync() => SaveToLibraryCoreAsync(false);

    private async Task SaveToLibraryCoreAsync(bool newName)
    {
        if (_dialogs is null) return;
        if (string.IsNullOrWhiteSpace(SelectedDatabase))
        {
            await _dialogs.ShowMessageAsync("Salva query", "Scegli il database nella barra dell’editor per salvare la query nella sua cartella. Puoi anche usare «Esporta file SQL…».");
            return;
        }
        try
        {
            var database = SelectedDatabase;
            var attached = SavedQueryId is { } id ? LibraryStore.Query(id) : null;
            if (attached is not null && (attached.ProfileId != _profile.Id || attached.Database != database)) attached = null;
            var name = attached?.Name ?? _savedQueryName ?? (FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : $"Query {_number}");
            if (attached is null || newName)
            {
                name = await _dialogs.PromptAsync("Salva query", $"Nome della query\nPosizione: {_profile.Name} / {database} / Query salvate", name, "Salva");
                if (string.IsNullOrWhiteSpace(name)) return;
            }
            name = name.Trim();
            var sameName = LibraryStore.QueriesFor(_profile.Id, database).FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));
            var queryId = newName ? Guid.NewGuid() : attached?.Id ?? Guid.NewGuid();
            if (sameName is not null && sameName.Id != queryId)
            {
                if (!await _dialogs.ConfirmAsync("Query già presente", $"Sostituire la query «{sameName.Name}» in {_profile.Name} / {database}?", "Sostituisci")) return;
                queryId = sameName.Id;
            }
            var saved = LibraryStore.SaveQuery(new SavedQuery { Id = queryId, ProfileId = _profile.Id, Database = database, Name = name, Sql = SqlText });
            AttachSavedQuery(saved);
            StatusMessage = $"Query «{saved.Name}» salvata in {_profile.Name} / {database} / Query salvate.";
        }
        catch (Exception exception) { await _dialogs.ShowMessageAsync("Salvataggio non riuscito", exception.Message, isError: true); }
    }

    public string? Database => SelectedDatabase;

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        if (_initializing) return;
        _schemaCache.Clear();
        _ = LoadDatabasesAsync(null);
        RefreshTitle();
    }

    partial void OnSelectedDatabaseChanged(string? value)
    {
        if (_initializing) return;
        RefreshTitle();
        _ = LoadSchemaAsync();
    }

    partial void OnFilePathChanged(string? value) => RefreshTitle();

    private void RefreshTitle()
    {
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(Context));
        OnPropertyChanged(nameof(Prompt));
    }

    private async Task LoadDatabasesAsync(string? select)
    {
        try
        {
            var names = await _databaseAccess.ListDatabasesAsync(_profile, CancellationToken.None);
            _initializing = true;
            Databases.Clear();
            foreach (var name in names) Databases.Add(name);
            SelectedDatabase = select is not null && Databases.Contains(select) ? select
                : SelectedDatabase is { } current && Databases.Contains(current) ? current
                : string.IsNullOrWhiteSpace(_profile.DefaultDatabase) ? null : _profile.DefaultDatabase;
            _initializing = false;
            // La ComboBox azzera la selezione quando l'elenco viene ricaricato: si riallinea esplicitamente.
            OnPropertyChanged(nameof(SelectedDatabase));
            OtherNames = names;
            RefreshTitle();
            await LoadSchemaAsync();
        }
        catch (Exception exception)
        {
            _initializing = false;
            StatusMessage = $"Impossibile leggere i database: {exception.Message}";
        }
    }

    // ───────────── Autocompletamento ─────────────

    /// <summary>Schema usato dall'autocompletamento (caricato in background).</summary>
    public SchemaModel? Schema { get; private set; }
    public IReadOnlyList<string> OtherNames { get; private set; } = [];
    public string? CurrentDatabase => SelectedDatabase;

    private async Task LoadSchemaAsync()
    {
        Schema = SelectedDatabase is null ? null : await GetSchemaForAsync(SelectedDatabase);
    }

    public async Task<SchemaModel?> GetSchemaForAsync(string database)
    {
        if (_schemaCache.TryGetValue(database, out var cached)) return cached;
        try
        {
            var schema = _profile.Kind.DefaultSchema(database);
            cached = await _databaseAccess.GetSchemaModelAsync(_profile, database, schema, CancellationToken.None);
        }
        catch { cached = null; }
        _schemaCache[database] = cached;
        return cached;
    }

    private readonly StringBuilder _transcript = new();

    public override string TabTitle
    {
        get
        {
            var target = SelectedDatabase is null ? _profile.Name : $"{SelectedDatabase} ({_profile.Name})";
            if (IsConsole) return $"Console · {target}";
            if (_savedQueryName is { } saved) return $"{saved} · {target}";
            return FilePath is { } path ? $"{Path.GetFileName(path)} · {target}" : $"Query {_number} · {target}";
        }
    }
    public override string TabGlyph => IsConsole ? "terminal" : "file-code";
    public bool IsConsole { get; }
    public bool IsEditor => !IsConsole;
    public string Prompt => $"{_profile.Kind switch { DatabaseKind.MySql => "mysql", DatabaseKind.SqlServer => "sqlcmd", DatabaseKind.Sqlite => "sqlite", _ => "psql" }}{(SelectedDatabase is null ? string.Empty : ":" + SelectedDatabase)}>";
    public string Context => SelectedDatabase is null ? $"{_profile.Name} · server" : $"{_profile.Name} · {SelectedDatabase}";
    public ObservableCollection<ColumnInfo> Columns { get; } = [];
    public ObservableCollection<TableRowViewModel> Rows { get; } = [];

    /// <summary>Un risultato per ogni istruzione che restituisce righe: si passa dall'uno all'altro con le linguette.</summary>
    public ObservableCollection<ResultSetTab> ResultSets { get; } = [];
    [ObservableProperty] private ResultSetTab? _selectedResultSet;
    public bool HasManyResults => ResultSets.Count > 1;

    partial void OnSelectedResultSetChanged(ResultSetTab? value)
    {
        if (value is null) return;
        _quickFilters.Clear();
        _quickFilterTexts.Clear();
        _allRows.Clear();
        _allRows.AddRange(value.Rows);
        Columns.Clear();
        Rows.Clear();
        foreach (var column in value.Columns) Columns.Add(column);
        foreach (var row in value.Rows) Rows.Add(row);
        if (HasManyResults) StatusMessage = $"{value.Title}: {value.Rows.Count:N0} righe{(value.Truncated ? $" (prime {MaxRows:N0})" : string.Empty)}";
        PrepareChart();
    }

    /// <summary>Ultimi valori dei parametri (:nome) usati in questa scheda.</summary>
    private readonly Dictionary<string, Views.QueryParameterValue> _parameterValues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chiede i valori dei parametri :nome / ${nome} e li sostituisce; null se l'utente annulla.</summary>
    private async Task<string?> ResolveParametersAsync(string sql)
    {
        var names = SqlParameters.Names(sql);
        if (names.Count == 0 || _dialogs is null) return sql;
        var window = new Views.QueryParametersWindow(names, _parameterValues);
        if (!await _dialogs.ShowDialogAsync(window)) return null;
        foreach (var (name, value) in window.Values) _parameterValues[name] = value;
        var literals = names.ToDictionary(n => n, n => SqlParameters.Literal(_parameterValues[n].Value, _parameterValues[n].Raw, _profile.Kind), StringComparer.OrdinalIgnoreCase);
        return SqlParameters.Substitute(sql, literals);
    }

    /// <summary>Tutte le righe del risultato; <see cref="Rows"/> contiene solo quelle che passano il filtro rapido.</summary>
    private readonly List<TableRowViewModel> _allRows = [];
    private readonly Dictionary<int, QuickFilterCondition> _quickFilters = [];
    private readonly Dictionary<int, string> _quickFilterTexts = [];
    [ObservableProperty] private bool _showQuickFilter = AppSettings.Current.QuickFilterRow;

    partial void OnShowQuickFilterChanged(bool value)
    {
        AppSettings.Current.QuickFilterRow = value;
        AppSettings.Current.Save();
        if (!value && _quickFilters.Count > 0)
        {
            _quickFilters.Clear();
            _quickFilterTexts.Clear();
            ApplyQuickFilter();
        }
    }

    public string QuickFilterText(int column) => _quickFilterTexts.TryGetValue(column, out var text) ? text : string.Empty;

    public void SetQuickFilter(int column, string text)
    {
        if (QuickFilter.Parse(text) is { } condition) { _quickFilters[column] = condition; _quickFilterTexts[column] = text.Trim(); }
        else { _quickFilters.Remove(column); _quickFilterTexts.Remove(column); }
        ApplyQuickFilter();
    }

    private void ApplyQuickFilter()
    {
        Rows.Clear();
        foreach (var row in _allRows)
            if (_quickFilters.All(filter => QuickFilter.Matches(filter.Value, row.ValueAt(filter.Key)))) Rows.Add(row);
        if (_quickFilters.Count > 0) StatusMessage = $"Filtro rapido: {Rows.Count:N0} di {_allRows.Count:N0} righe";
        PrepareChart();
    }
    public ObservableCollection<string> Messages { get; } = [];

    [ObservableProperty] private string _sqlText = string.Empty;
    [ObservableProperty] private string _consoleInput = string.Empty;
    [ObservableProperty] private string _consoleText;
    [ObservableProperty] private string _statusMessage = "Pronto";
    /// <summary>Totali della selezione nella griglia dei risultati.</summary>
    [ObservableProperty] private string _selectionSummary = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _resultTabIndex;

    /// <summary>Testo selezionato nell'editor: se presente si esegue solo quello.</summary>
    public string? SelectedSql { get; set; }
    /// <summary>Istruzione sotto il cursore (impostata dalla vista).</summary>
    public string? CurrentStatementSql { get; set; }

    /// <summary>La vista inserisce questo testo al cursore (snippet, Query Builder).</summary>
    public event Action<string>? InsertRequested;

    private int MaxRows => Math.Max(1, AppSettings.Current.QueryMaxRows);

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        var sql = string.IsNullOrWhiteSpace(SelectedSql) ? SqlText : SelectedSql;
        if (string.IsNullOrWhiteSpace(sql) || IsBusy) return;
        await RunAsync(sql, console: false);
    }

    [RelayCommand]
    private async Task ExecuteAllAsync()
    {
        if (string.IsNullOrWhiteSpace(SqlText) || IsBusy) return;
        await RunAsync(SqlText, console: false);
    }

    [RelayCommand]
    private async Task ExecuteCurrentAsync()
    {
        var sql = string.IsNullOrWhiteSpace(CurrentStatementSql) ? SqlText : CurrentStatementSql;
        if (string.IsNullOrWhiteSpace(sql) || IsBusy) return;
        await RunAsync(sql, console: false);
    }

    [RelayCommand]
    private async Task ExplainAsync()
    {
        var sql = !string.IsNullOrWhiteSpace(SelectedSql) ? SelectedSql : !string.IsNullOrWhiteSpace(CurrentStatementSql) ? CurrentStatementSql : SqlText;
        var statement = Navifox.Core.SqlScriptSplitter.Split(sql ?? string.Empty, _profile.Kind).FirstOrDefault();
        if (statement is null || IsBusy) return;
        if (statement.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase)) statement = statement[7..].TrimStart();
        if (await ResolveParametersAsync(statement) is not { } resolved) return;
        statement = resolved.Trim().TrimEnd(';');
        // Piano come albero (il comando non esegue la query): se il server non supporta il formato, si torna all'EXPLAIN in tabella.
        if (await ShowPlanTreeAsync(statement)) return;
        await RunAsync("EXPLAIN " + statement, console: false);
    }

    public System.Collections.ObjectModel.ObservableCollection<PlanNode> PlanNodes { get; } = [];
    [ObservableProperty] private string _planInfo = "Premi «Explain» per vedere il piano di esecuzione dell'istruzione corrente come albero.";

    private async Task<bool> ShowPlanTreeAsync(string statement)
    {
        IsBusy = true;
        StatusMessage = "Piano di esecuzione…";
        try
        {
            var provider = _databaseAccess.Provider(_profile);
            await using var connection = await _databaseAccess.OpenAsync(_profile, SelectedDatabase, CancellationToken.None);
            QueryResult? plan = null;
            var statements = ExplainPlan.Statements(_profile.Kind, statement);
            try
            {
                for (var i = 0; i < statements.Count; i++)
                {
                    var result = await provider.ExecuteAsync(connection, statements[i], 10_000, CancellationToken.None);
                    if (result.HasResultSet && plan is null) plan = result;
                }
            }
            finally
            {
                // SQL Server: SHOWPLAN resta attivo sulla sessione finché non lo si spegne.
                if (_profile.Kind == DatabaseKind.SqlServer) try { await provider.ExecuteAsync(connection, "SET SHOWPLAN_XML OFF", 0, CancellationToken.None); } catch { }
            }
            if (plan is null || ExplainPlan.Parse(_profile.Kind, plan) is not { } root) return false;
            PlanNodes.Clear();
            PlanNodes.Add(root);
            var expensive = root.SelfAndDescendants().Count(n => n.IsExpensive);
            PlanInfo = (expensive == 0 ? "Nessun passaggio costoso evidente." : $"{expensive} passaggi costosi in rosso (scansioni complete, ordinamenti su disco o gran parte del costo).")
                + " Stime del server: la query non è stata eseguita.";
            ResultTabIndex = PlanTabIndex;
            StatusMessage = "Piano di esecuzione pronto.";
            return true;
        }
        catch (Exception error)
        {
            // MariaDB/MySQL < 8.0.16 non conoscono FORMAT=TREE: si usa l'EXPLAIN classico.
            Messages.Add($"[{DateTime.Now:HH:mm:ss}] Piano ad albero non disponibile: {error.Message}");
            return false;
        }
        finally { IsBusy = false; }
    }

    private const int PlanTabIndex = 4;

    [RelayCommand]
    private void Beautify()
    {
        if (string.IsNullOrWhiteSpace(SqlText)) return;
        SqlText = SqlFormatter.Format(SqlText, _profile.Kind == DatabaseKind.MySql);
        StatusMessage = "SQL formattato.";
    }

    [RelayCommand]
    private void InsertSnippet(string? snippet)
    {
        if (SqlSnippets.All.FirstOrDefault(s => s.Name == snippet) is { } found) InsertRequested?.Invoke(found.Build(_profile.Kind == DatabaseKind.MySql));
    }

    public IReadOnlyList<SqlSnippet> Snippets => SqlSnippets.All;

    [RelayCommand]
    private Task SaveAsync() => SaveToLibraryCoreAsync(false);

    [RelayCommand]
    private Task SaveAsAsync() => SaveToLibraryCoreAsync(true);

    [RelayCommand]
    private Task ExportFileAsync() => SaveCoreAsync(null);

    private async Task SaveCoreAsync(string? path)
    {
        if (_dialogs is null) return;
        path ??= await _dialogs.PickSaveFileAsync("Salva query", $"query{_number}.sql", "sql", new FileFilter("Script SQL", "*.sql"));
        if (path is null) return;
        try
        {
            await File.WriteAllTextAsync(path, SqlText, new UTF8Encoding(false));
            MarkClean();
            StatusMessage = $"Esportato in {path}";
        }
        catch (Exception exception) { await _dialogs.ShowMessageAsync("Esportazione non riuscita", exception.Message, isError: true); }
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (_dialogs is null) return;
        var path = await _dialogs.PickOpenFileAsync("Apri script SQL", new FileFilter("Script SQL", "*.sql"), new FileFilter("Tutti i file", "*.*"));
        if (path is null) return;
        SqlText = await File.ReadAllTextAsync(path);
        MarkClean();
        SavedQueryId = null;
        _savedQueryName = null;
        FilePath = path;
        RefreshTitle();
    }

    [RelayCommand]
    private async Task QueryBuilderAsync()
    {
        if (_dialogs is null) return;
        if (Schema is null && SelectedDatabase is not null) await LoadSchemaAsync();
        if (Schema is null)
        {
            StatusMessage = "Scegli un database per usare il Query Builder.";
            return;
        }
        var builder = new QueryBuilderViewModel(Schema, _databaseAccess.Provider(_profile), _profile.Kind.DefaultSchema(SelectedDatabase!));
        if (await _dialogs.ShowQueryBuilderAsync(builder) && builder.GeneratedSql.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(SqlText)) SqlText = builder.GeneratedSql;
            else InsertRequested?.Invoke(Environment.NewLine + builder.GeneratedSql);
        }
    }

    [RelayCommand]
    private async Task SubmitConsoleAsync()
    {
        var input = ConsoleInput;
        if (string.IsNullOrWhiteSpace(input) || IsBusy) return;
        ConsoleInput = string.Empty;
        _history.Add(input);
        _historyIndex = _history.Count;
        var trimmed = input.Trim();
        if (trimmed is "clear" or "cls")
        {
            _transcript.Clear();
            ConsoleText = string.Empty;
            return;
        }
        AppendConsole($"{Prompt} {input}\n");
        await RunAsync(input, console: true);
    }

    public void HistoryUp()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Max(0, _historyIndex - 1);
        ConsoleInput = _history[_historyIndex];
    }

    public void HistoryDown()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        ConsoleInput = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
    }

    [RelayCommand]
    private void Stop() => _running?.Cancel();

    public override ConnectionProfile? TabConnection => SelectedProfile;

    private async Task RunAsync(string sql, bool console)
    {
        if (await ResolveParametersAsync(sql) is not { } resolved) { StatusMessage = "Esecuzione annullata."; return; }
        sql = resolved;
        if (!await Services.SqlGuard.ConfirmAsync(_dialogs, _profile, sql, countRows: Services.SqlGuard.Counter(_databaseAccess, _profile, SelectedDatabase)))
        { StatusMessage = "Esecuzione annullata."; return; }
        _running = new CancellationTokenSource();
        IsBusy = true;
        StatusMessage = "Esecuzione…";
        var timer = Stopwatch.StartNew();
        ActivityRecording.ActivityOperation? activity = null;
        try
        {
            activity = ActivityRecording.BeginOperation(_profile, SelectedDatabase, console ? "Console" : "Query", "Esecuzione dall’editor SQL");
            var results = await _databaseAccess.ExecuteBatchAsync(_profile, SelectedDatabase, sql, console ? ConsoleMaxRows : MaxRows, _running.Token);
            activity?.Complete(results.FirstOrDefault(r => r.Error is not null)?.Error?.Message);
            timer.Stop();
            if (console) WriteConsoleResults(results);
            else ShowEditorResults(results, timer.Elapsed);
        }
        catch (OperationCanceledException error)
        {
            activity?.Fail(error);
            StatusMessage = "Esecuzione interrotta.";
            if (console) AppendConsole("Interrotto.\n\n");
            else Messages.Add($"[{DateTime.Now:HH:mm:ss}] Esecuzione interrotta.");
        }
        catch (Exception exception)
        {
            activity?.Fail(exception);
            StatusMessage = $"Errore: {exception.Message}";
            RememberError(sql, exception.Message);
            if (console) AppendConsole($"ERRORE: {exception.Message}\n\n");
            else
            {
                Messages.Add($"[{DateTime.Now:HH:mm:ss}] ERRORE: {exception.Message}");
                ResultTabIndex = 2;
            }
        }
        finally
        {
            activity?.Dispose();
            IsBusy = false;
            _running.Dispose();
            _running = null;
        }
    }

    private void ShowEditorResults(IReadOnlyList<StatementResult> results, TimeSpan elapsed)
    {
        var lastWithRows = results.LastOrDefault(result => result.Result?.HasResultSet == true);
        _quickFilters.Clear();
        _quickFilterTexts.Clear();
        _allRows.Clear();
        Columns.Clear();
        Rows.Clear();
        ResultSets.Clear();
        var number = 0;
        foreach (var result in results)
            if (result.Result is { HasResultSet: true } set)
                ResultSets.Add(new ResultSetTab(++number, ResultSetTab.Describe(result.Sql), set.Columns, set.Rows.Select(row => new TableRowViewModel(row)).ToArray(), set.Truncated));
        OnPropertyChanged(nameof(HasManyResults));
        SelectedResultSet = null;
        SelectedResultSet = ResultSets.FirstOrDefault();
        foreach (var result in results)
        {
            // Come nei client classici: l'istruzione intera, poi l'esito e il tempo su righe che iniziano con «>».
            Messages.Add(result.Error is not null
                ? $"{result.Sql.Trim()}\n> Errore: {result.Error.Message}\n> Tempo: {Seconds(result.Elapsed)}s"
                : $"{result.Sql.Trim()}\n> {Outcome(result.Result!)}\n> Tempo: {Seconds(result.Elapsed)}s");
        }
        var failed = results.FirstOrDefault(result => result.Error is not null);
        if (failed is not null) RememberError(failed.Sql, failed.Error!.Message);
        StatusMessage = failed is not null
            ? $"Errore: {failed.Error!.Message}"
            : ResultSets.Count > 1
                ? $"{ResultSets.Count} risultati · {results.Count} istruzioni · {elapsed.TotalSeconds:0.000} s"
            : lastWithRows?.Result is { } r
                ? $"{r.Rows.Count:N0} righe{(r.Truncated ? $" (prime {MaxRows:N0})" : string.Empty)} · {results.Count} istruzioni · {elapsed.TotalSeconds:0.000} s"
                : $"{results.Count} istruzioni eseguite · {elapsed.TotalSeconds:0.000} s";
        ResultTabIndex = failed is not null || lastWithRows is null ? 2 : ResultTabIndex == 1 ? 1 : 0;
        PrepareChart();
    }

    private void WriteConsoleResults(IReadOnlyList<StatementResult> results)
    {
        foreach (var result in results)
        {
            if (result.Error is not null)
            {
                AppendConsole($"ERRORE: {result.Error.Message}\n\n");
                continue;
            }
            var query = result.Result!;
            if (query.HasResultSet) AppendConsole(AsciiTable(query));
            AppendConsole($"{Describe(query)} ({result.Elapsed.TotalSeconds:0.000} s)\n\n");
        }
        StatusMessage = "Pronto";
    }

    /// <summary>Esito per la scheda Messaggi: «Righe interessate: 22869» o «Righe restituite: 100».</summary>
    private static string Outcome(QueryResult result) => result.HasResultSet
        ? $"Righe restituite: {result.Rows.Count}{(result.Truncated ? " (risultato troncato)" : string.Empty)}"
        : $"Righe interessate: {Math.Max(0, result.RecordsAffected)}";

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));

    private static string Describe(QueryResult result) => result.HasResultSet
        ? $"{result.Rows.Count:N0} righe{(result.Truncated ? " (risultato troncato)" : string.Empty)}"
        : $"{Math.Max(0, result.RecordsAffected):N0} righe interessate";

    private static string OneLine(string sql)
    {
        var line = string.Join(' ', sql.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 140 ? line[..140] + "…" : line;
    }

    private static string AsciiTable(QueryResult result)
    {
        const int maxWidth = 40;
        var cells = result.Rows.Select(row => row.Select(value => Clip(TableRowViewModel.Format(value), maxWidth)).ToArray()).ToList();
        var widths = result.Columns.Select((column, index) => Math.Max(Clip(column.Name, maxWidth).Length, cells.Count == 0 ? 0 : cells.Max(row => row[index].Length))).ToArray();
        var separator = "+" + string.Join("+", widths.Select(width => new string('-', width + 2))) + "+\n";
        var builder = new StringBuilder(separator);
        builder.Append("| ").Append(string.Join(" | ", result.Columns.Select((column, index) => Clip(column.Name, maxWidth).PadRight(widths[index])))).Append(" |\n").Append(separator);
        foreach (var row in cells) builder.Append("| ").Append(string.Join(" | ", row.Select((cell, index) => cell.PadRight(widths[index])))).Append(" |\n");
        if (cells.Count > 0) builder.Append(separator);
        return builder.ToString();
    }

    private static string Clip(string value, int width)
    {
        value = value.Replace('\n', ' ').Replace('\r', ' ');
        return value.Length <= width ? value : value[..(width - 1)] + "…";
    }

    private void AppendConsole(string text)
    {
        _transcript.Append(text);
        // Limita la trascrizione a ~400 KB per mantenere reattivo il controllo.
        if (_transcript.Length > 400_000) _transcript.Remove(0, _transcript.Length - 300_000);
        ConsoleText = _transcript.ToString();
    }
}

/// <summary>Risultato di una delle istruzioni eseguite (linguetta sopra la griglia).</summary>
public sealed class ResultSetTab(int number, string source, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<TableRowViewModel> rows, bool truncated)
{
    public int Number { get; } = number;
    public string Title { get; } = $"Risultato {number}";
    public string Source { get; } = source;
    public IReadOnlyList<ColumnInfo> Columns { get; } = columns;
    public IReadOnlyList<TableRowViewModel> Rows { get; } = rows;
    public bool Truncated { get; } = truncated;
    public string Label => $"{Number} · {Source} ({Rows.Count:N0})";

    /// <summary>Nome breve: la tabella dopo FROM, altrimenti l'inizio dell'istruzione.</summary>
    public static string Describe(string sql)
    {
        var match = System.Text.RegularExpressions.Regex.Match(sql, @"\bFROM\s+([`""\[]?[\w.]+[`""\]]?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value.Trim('`', '"', '[', ']');
        var line = string.Join(' ', sql.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 24 ? line[..24] + "…" : line;
    }
}
