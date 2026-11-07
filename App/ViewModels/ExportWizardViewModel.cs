using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public sealed record ExportFormatOption(string Label, string Extension, ExportFormat? Format, string Glyph)
{
    public bool IsAvailable => Format is not null;
    public string Display => IsAvailable ? Label : $"{Label}  · presto";
}

public partial class SelectableColumn(string name, string type) : ObservableObject
{
    public string Name { get; } = name;
    public string Type { get; } = type;
    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>Export Wizard in 5 passi: formato → destinazione → colonne → opzioni → avvio.</summary>
public partial class ExportWizardViewModel : ObservableObject
{
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IDialogService _dialogs;
    private readonly ConnectionProfile _profile;
    private readonly string _database;
    private readonly string _schema;
    private readonly string _table;
    private readonly IReadOnlyList<ColumnInfo> _columns;
    private readonly string _selectAllSql;
    private readonly IReadOnlyList<TableRowViewModel> _page;
    private readonly IReadOnlyList<TableRowViewModel> _selected;
    private CancellationTokenSource? _running;

    public ExportWizardViewModel(IDatabaseAccessService databaseAccess, IDialogService dialogs, ConnectionProfile profile, string database, string schema, string table,
        IReadOnlyList<ColumnInfo> columns, string selectAllSql, IReadOnlyList<TableRowViewModel> page, IReadOnlyList<TableRowViewModel> selected, long totalRows)
    {
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        _profile = profile;
        _database = database;
        _schema = schema;
        _table = table;
        _columns = columns;
        _selectAllSql = selectAllSql;
        _page = page;
        _selected = selected;
        TotalRows = totalRows;
        foreach (var column in columns) Columns.Add(new SelectableColumn(column.Name, column.DataType));
        _selectedFormat = Formats.First(format => format.Format == ExportFormat.Csv);
        _scope = "all";
        UpdatePath();
    }

    public event Action? CloseRequested;

    public IReadOnlyList<ExportFormatOption> Formats { get; } =
    [
        new("DBase (*.dbf)", "dbf", null, "▤"),
        new("File di testo (*.txt)", "txt", ExportFormat.Text, "▤"),
        new("File CSV (*.csv)", "csv", ExportFormat.Csv, "▦"),
        new("File HTML (*.htm; *.html)", "html", ExportFormat.Html, "◈"),
        new("Foglio Excel 97-2003 (*.xls)", "xls", null, "▥"),
        new("File Excel 2007 o successivo (*.xlsx)", "xlsx", ExportFormat.Excel, "▥"),
        new("Script SQL (*.sql)", "sql", ExportFormat.Sql, "⌘"),
        new("File XML (*.xml)", "xml", ExportFormat.Xml, "‹›"),
        new("File JSON (*.json)", "json", ExportFormat.Json, "{}"),
        new("Tabella Markdown (*.md)", "md", ExportFormat.Markdown, "#"),
        new("Database MS Access 2007 (*.accdb)", "accdb", null, "▣"),
    ];

    public IReadOnlyList<string> Encodings { get; } = ["UTF-8", "UTF-8 con BOM", "UTF-16 LE", "Windows-1252"];
    public IReadOnlyList<string> Delimiters { get; } = ["Virgola (,)", "Punto e virgola (;)", "Tabulazione", "Barra verticale (|)", "Spazio"];
    public IReadOnlyList<string> Qualifiers { get; } = ["\"", "'", "(nessuno)"];
    public IReadOnlyList<string> LineEndings { get; } = ["CRLF (Windows)", "LF (Unix)"];
    public IReadOnlyList<string> DecimalSeparators { get; } = [".", ","];
    public ObservableCollection<SelectableColumn> Columns { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public long TotalRows { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepTitle), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(IsStep5), nameof(CanGoBack), nameof(CanGoNext), nameof(IsLastStep))]
    private int _step = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDelimited), nameof(IsSql), nameof(IsJson), nameof(HasHeaderOption), nameof(CanGoNext), nameof(FormatHint))]
    private ExportFormatOption _selectedFormat;
    [ObservableProperty] private string _scope;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private string _selectedEncoding = "UTF-8";
    [ObservableProperty] private bool _includeHeader = true;
    [ObservableProperty] private string _selectedDelimiter = "Virgola (,)";
    [ObservableProperty] private string _selectedQualifier = "\"";
    [ObservableProperty] private string _selectedLineEnding = "CRLF (Windows)";
    [ObservableProperty] private string _nullText = string.Empty;
    [ObservableProperty] private string _dateFormat = "yyyy-MM-dd HH:mm:ss";
    [ObservableProperty] private string _decimalSeparator = ".";
    [ObservableProperty] private bool _quoteAllText;
    [ObservableProperty] private bool _includeCreateTable;
    [ObservableProperty] private bool _prettyJson = true;
    [ObservableProperty] private decimal? _sqlBatchSize = 100;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public string StepTitle => Step switch
    {
        1 => "Questa procedura guidata permette di esportare i dati. Quale formato di export vuoi usare? (1/5)",
        2 => "Scegli quali record esportare e dove salvare il file. (2/5)",
        3 => "Seleziona le colonne da esportare. (3/5)",
        4 => "Imposta le opzioni del formato. (4/5)",
        _ => "Tutto pronto: premi «Avvia» per esportare. (5/5)"
    };
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsStep4 => Step == 4;
    public bool IsStep5 => Step == 5;
    public bool IsLastStep => Step == 5;
    public bool CanGoBack => Step > 1 && !IsRunning;
    public bool CanGoNext => Step < 5 && SelectedFormat.IsAvailable && !IsRunning;
    public bool IsDelimited => SelectedFormat.Format is ExportFormat.Csv or ExportFormat.Text;
    public bool IsSql => SelectedFormat.Format == ExportFormat.Sql;
    public bool IsJson => SelectedFormat.Format == ExportFormat.Json;
    public bool HasHeaderOption => SelectedFormat.Format is ExportFormat.Csv or ExportFormat.Text or ExportFormat.Html or ExportFormat.Excel;
    public string FormatHint => SelectedFormat.IsAvailable ? string.Empty : "Questo formato arriverà in una fase successiva.";
    public string AllLabel => $"Tutti i record (filtro e ordinamento correnti){(TotalRows >= 0 ? $" · {TotalRows:N0}" : string.Empty)}";
    public string PageLabel => $"Solo la pagina visualizzata · {_page.Count:N0}";
    public string SelectedLabel => $"Solo le righe selezionate · {_selected.Count:N0}";
    public bool HasSelection => _selected.Count > 0;
    public string Summary => BuildSummary();

    public bool ScopeAll { get => Scope == "all"; set { if (value) Scope = "all"; } }
    public bool ScopePage { get => Scope == "page"; set { if (value) Scope = "page"; } }
    public bool ScopeSelected { get => Scope == "selected"; set { if (value) Scope = "selected"; } }

    partial void OnSelectedFormatChanged(ExportFormatOption value)
    {
        if (value.Format == ExportFormat.Text) SelectedDelimiter = "Tabulazione";
        else if (value.Format == ExportFormat.Csv && SelectedDelimiter == "Tabulazione") SelectedDelimiter = "Virgola (,)";
        UpdatePath();
    }

    partial void OnScopeChanged(string value)
    {
        OnPropertyChanged(nameof(ScopeAll));
        OnPropertyChanged(nameof(ScopePage));
        OnPropertyChanged(nameof(ScopeSelected));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
    }

    [RelayCommand]
    private void Next()
    {
        if (!ValidateStep()) return;
        Step = Math.Min(5, Step + 1);
        if (Step == 5) OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void JumpToEnd()
    {
        for (var s = Step; s < 5; s++)
        {
            if (!ValidateStep()) return;
            Step++;
        }
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void Back() => Step = Math.Max(1, Step - 1);

    [RelayCommand]
    private void SelectAllColumns(string? value)
    {
        var select = value != "none";
        foreach (var column in Columns) column.IsSelected = select;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _dialogs.PickSaveFileAsync("Esporta in…", System.IO.Path.GetFileName(Path), SelectedFormat.Extension,
            new FileFilter(SelectedFormat.Label, $"*.{SelectedFormat.Extension}"));
        if (path is not null) Path = path;
    }

    [RelayCommand]
    private void Close()
    {
        _running?.Cancel();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path}\"") { UseShellExecute = true }); }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        for (var s = 1; s <= 4; s++)
        {
            var current = Step;
            Step = s;
            if (!ValidateStep()) return;
            Step = current;
        }
        Step = 5;
        if (File.Exists(Path) && !await _dialogs.ConfirmAsync("File esistente", $"«{Path}» esiste già. Sovrascriverlo?", "Sovrascrivi")) return;

        IsRunning = true;
        IsFinished = false;
        ErrorMessage = string.Empty;
        Log.Clear();
        _running = new CancellationTokenSource();
        var token = _running.Token;
        var columnIndexes = Columns.Select((column, index) => (column, index)).Where(pair => pair.column.IsSelected).Select(pair => pair.index).ToArray();
        var expected = Scope switch { "page" => _page.Count, "selected" => _selected.Count, _ => TotalRows };
        ProgressIndeterminate = expected <= 0;
        ProgressValue = 0;
        var timer = Stopwatch.StartNew();
        AddLog($"Avvio export di {_table} in {SelectedFormat.Label}");
        AddLog($"Destinazione: {Path}");
        try
        {
            var provider = _databaseAccess.Provider(_profile);
            string? ddl = null;
            if (IsSql && IncludeCreateTable)
            {
                await using var connection = await _databaseAccess.OpenAsync(_profile, _database, token);
                ddl = (await provider.GetDdlAsync(connection, _database, _schema, _table, false, token)).Create;
            }
            var options = BuildOptions(columnIndexes, ddl);
            var progress = new Progress<long>(count =>
            {
                ProgressText = $"{count:N0} record esportati";
                if (expected > 0) ProgressValue = Math.Min(100, count * 100d / expected);
            });
            var rows = SelectRows(columnIndexes, token);
            var exported = await Task.Run(() => DataExporter.ExportAsync(options, rows, provider, progress, token), token);
            ProgressValue = 100;
            ProgressIndeterminate = false;
            ProgressText = $"{exported:N0} record esportati in {timer.Elapsed.TotalSeconds:0.0} s";
            AddLog($"Completato: {exported:N0} record · {new FileInfo(Path).Length / 1024d:N0} KB");
            IsFinished = true;
        }
        catch (OperationCanceledException)
        {
            AddLog("Export interrotto.");
            ProgressText = "Interrotto";
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            AddLog($"ERRORE: {exception.Message}");
        }
        finally
        {
            IsRunning = false;
            ProgressIndeterminate = false;
        }
    }

    private async IAsyncEnumerable<IReadOnlyList<object?>> SelectRows(int[] indexes, [EnumeratorCancellation] CancellationToken token)
    {
        IReadOnlyList<object?> Project(IReadOnlyList<object?> row) => indexes.Select(index => index < row.Count ? row[index] : null).ToArray();
        if (Scope == "all")
        {
            await foreach (var row in _databaseAccess.StreamQueryAsync(_profile, _database, _selectAllSql, _ => { }, token)) yield return Project(row);
            yield break;
        }
        foreach (var row in Scope == "page" ? _page : _selected)
        {
            token.ThrowIfCancellationRequested();
            if (!row.IsDeleted) yield return Project(row.Values);
        }
    }

    private ExportOptions BuildOptions(int[] indexes, string? ddl) => new()
    {
        Format = SelectedFormat.Format!.Value,
        Path = Path,
        Columns = indexes.Select(index => Columns[index].Name).ToArray(),
        Encoding = SelectedEncoding switch
        {
            "UTF-8 con BOM" => new UTF8Encoding(true),
            "UTF-16 LE" => Encoding.Unicode,
            "Windows-1252" => CodePagesEncoding(1252),
            _ => new UTF8Encoding(false)
        },
        IncludeHeader = IncludeHeader,
        Delimiter = SelectedDelimiter switch { "Punto e virgola (;)" => ";", "Tabulazione" => "\t", "Barra verticale (|)" => "|", "Spazio" => " ", _ => "," },
        Qualifier = SelectedQualifier == "(nessuno)" ? string.Empty : SelectedQualifier,
        LineEnding = SelectedLineEnding.StartsWith("LF", StringComparison.Ordinal) ? "\n" : "\r\n",
        NullText = NullText,
        DateFormat = string.IsNullOrWhiteSpace(DateFormat) ? "yyyy-MM-dd HH:mm:ss" : DateFormat,
        DecimalSeparator = DecimalSeparator,
        QuoteAll = QuoteAllText,
        Schema = _schema,
        TableName = _table,
        CreateTableDdl = ddl,
        SqlBatchSize = (int)Math.Clamp(SqlBatchSize ?? 100, 1, 10_000),
        PrettyJson = PrettyJson,
        Title = $"{_table} @{_database}"
    };

    private static Encoding CodePagesEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }

    private bool ValidateStep()
    {
        ErrorMessage = string.Empty;
        switch (Step)
        {
            case 1 when !SelectedFormat.IsAvailable:
                ErrorMessage = "Scegli un formato disponibile.";
                return false;
            case 2 when string.IsNullOrWhiteSpace(Path):
                ErrorMessage = "Indica il file di destinazione.";
                return false;
            case 2 when System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) is { } folder && !Directory.Exists(folder):
                ErrorMessage = "La cartella di destinazione non esiste.";
                return false;
            case 2 when Scope == "selected" && _selected.Count == 0:
                ErrorMessage = "Non ci sono righe selezionate.";
                return false;
            case 3 when !Columns.Any(column => column.IsSelected):
                ErrorMessage = "Seleziona almeno una colonna.";
                return false;
        }
        return true;
    }

    private void UpdatePath()
    {
        var folder = string.IsNullOrWhiteSpace(Path)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : System.IO.Path.GetDirectoryName(Path) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var name = string.IsNullOrWhiteSpace(Path) ? _table : System.IO.Path.GetFileNameWithoutExtension(Path);
        Path = System.IO.Path.Combine(folder, $"{name}.{SelectedFormat.Extension}");
    }

    private string BuildSummary()
    {
        var count = Columns.Count(column => column.IsSelected);
        var scope = Scope switch { "page" => PageLabel, "selected" => SelectedLabel, _ => AllLabel };
        return $"""
            Origine:      {_table} @{_database} ({_profile.Name})
            Record:       {scope}
            Colonne:      {count} di {Columns.Count}
            Formato:      {SelectedFormat.Label}
            File:         {Path}
            Codifica:     {SelectedEncoding}
            """;
    }

    private void AddLog(string message) => Log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
}
