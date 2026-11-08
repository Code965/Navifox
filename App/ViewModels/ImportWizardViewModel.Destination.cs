using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Navifox.App.Services;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public sealed record ImportTypeOption(ImportDataType Type, string Label);
public partial class NewImportColumn(string source, ImportTypeOption type) : ObservableObject
{
    public static IReadOnlyList<ImportTypeOption> Types { get; } = [new(ImportDataType.Text,"Testo"), new(ImportDataType.Integer,"Intero"), new(ImportDataType.Decimal,"Decimale"), new(ImportDataType.Boolean,"Booleano"), new(ImportDataType.Date,"Data"), new(ImportDataType.DateTime,"Data e ora"), new(ImportDataType.Time,"Ora"), new(ImportDataType.Binary,"Binario")];
    public string Source { get; } = source;
    [ObservableProperty] private string _name = source;
    [ObservableProperty] private ImportTypeOption _selectedType = type;
}

public partial class ImportWizardViewModel
{
    public ObservableCollection<string> ExistingTables { get; } = [];
    public ObservableCollection<NewImportColumn> NewColumns { get; } = [];
    public IReadOnlyList<string> RecordDelimiters { get; } = ["Automatico", "CRLF", "LF", "CR"];
    public IReadOnlyList<string> DateOrders { get; } = ["DMY", "MDY", "YMD"];
    public IReadOnlyList<string> DecimalSymbols { get; } = [".", ","];
    public IReadOnlyList<string> DateTimeOrders { get; } = ["Data Ora", "Ora Data"];
    public IReadOnlyList<string> BinaryEncodings { get; } = ["Base64", "Hex"];
    public string TargetContext => $"{_profile.Name} / {_database} / {_schema}";
    public bool TableCreated { get; private set; }
    [ObservableProperty] private bool _createNewTable;
    [ObservableProperty] private string _targetTable = "";
    [ObservableProperty] private decimal? _headerRow = 1;
    [ObservableProperty] private decimal? _firstDataRow = 2;
    [ObservableProperty] private decimal? _lastDataRow;
    [ObservableProperty] private string _selectedRecordDelimiter = "Automatico";
    [ObservableProperty] private string _dateOrder = "DMY";
    [ObservableProperty] private string _dateDelimiter = "/";
    [ObservableProperty] private string _timeDelimiter = ":";
    [ObservableProperty] private string _decimalSymbol = ".";
    [ObservableProperty] private string _dateTimeOrder = "Data Ora";
    [ObservableProperty] private string _binaryEncoding = "Base64";
    partial void OnHeaderRowChanged(decimal? value) => RefreshPreview();
    partial void OnFirstDataRowChanged(decimal? value) => RefreshPreview();
    partial void OnLastDataRowChanged(decimal? value) => RefreshPreview();
    partial void OnSelectedRecordDelimiterChanged(string value) => RefreshPreview();
    partial void OnFirstRowIsHeaderChanged(bool value)
    {
        if (value && FirstDataRow == 1) FirstDataRow = (HeaderRow ?? 1) + 1;
        if (!value && FirstDataRow == (HeaderRow ?? 1) + 1) FirstDataRow = 1;
        RefreshPreview();
    }

    public static bool SupportsFile(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".tsv" or ".txt" or ".xlsx" or ".json";
    public void SetSourceFile(string path)
    {
        if (!SupportsFile(path)) throw new NotSupportedException("Scegli un file CSV, TSV, TXT, Excel .xlsx oppure JSON. Per Excel .xls salva prima una copia .xlsx.");
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var format = extension switch { ".xlsx" => ImportFormat.Excel, ".json" => ImportFormat.Json, ".tsv" or ".txt" => ImportFormat.Text, _ => ImportFormat.Csv };
        SelectedFormat = Formats.First(f => f.Format == format);
        Path = path;
        if (CreateNewTable) TargetTable = System.IO.Path.GetFileNameWithoutExtension(path);
        Step = 2;
    }

    public async Task LoadTargetsAsync()
    {
        var tables = await _databaseAccess.ListTablesAsync(_profile, _database, _schema, CancellationToken.None);
        ExistingTables.Clear();
        foreach (var table in tables.Where(t => !t.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase))) ExistingTables.Add(table.Name);
        if (TargetTable.Length == 0 && !CreateNewTable) TargetTable = ExistingTables.FirstOrDefault() ?? "";
    }

    private ImportValueOptions ValueOptions() => new(DateOrder, DateDelimiter, TimeDelimiter, DecimalSymbol, DateTimeOrder, BinaryEncoding);
    private void InferColumns()
    {
        var source = DataImporter.Open(ReadOptions());
        var sample = source.Rows.Take(200).ToArray();
        NewColumns.Clear();
        for (var i = 0; i < source.Fields.Count; i++)
        {
            var type = ImportDataTypes.Infer(sample.Select(row => i < row.Length ? row[i] : null), ValueOptions());
            NewColumns.Add(new(source.Fields[i], NewImportColumn.Types.Single(t => t.Type == type)));
        }
    }

    private async Task PrepareMappingsAsync()
    {
        ValidateTarget();
        _table = TargetTable.Trim();
        if (CreateNewTable)
        {
            if (ExistingTables.Contains(_table, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Questo nome esiste già: scegli un altro nome oppure importa nella tabella esistente.");
            _columns = NewColumns.Select(c => new ColumnInfo(c.Name.Trim(), ImportDataTypes.SqlType(_profile.Kind, c.SelectedType.Type), true)).ToArray();
        }
        else
        {
            if (!ExistingTables.Contains(_table)) throw new InvalidOperationException("Seleziona una tabella esistente.");
            _columns = await _databaseAccess.GetColumnsAsync(_profile, _database, _schema, _table, CancellationToken.None);
        }
        BuildMappings();
        if (CreateNewTable)
            for (var i = 0; i < NewColumns.Count; i++) Mappings[i].Source = NewColumns[i].Source;
    }

    private void ValidateTarget()
    {
        var max = _profile.Kind switch { DatabaseKind.PostgreSql => 63, DatabaseKind.MySql => 64, _ => 128 };
        void Name(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0 || System.Text.Encoding.UTF8.GetByteCount(name.Trim()) > max)
                throw new InvalidOperationException($"I nomi devono essere non vuoti e lunghi al massimo {max} byte.");
        }
        Name(TargetTable);
        if (!CreateNewTable) return;
        if (NewColumns.Count == 0) throw new InvalidOperationException("Nessuna colonna disponibile: controlla il file e le righe selezionate.");
        foreach (var column in NewColumns) Name(column.Name);
        if (NewColumns.Select(c => c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != NewColumns.Count)
            throw new InvalidOperationException("I nomi delle nuove colonne devono essere univoci.");
    }

    private bool ValidateRows()
    {
        bool Whole(decimal? n) => n is > 0 and <= 1_048_576 && n == decimal.Truncate(n.Value);
        if (HasHeaderOption && (!Whole(FirstDataRow) || FirstRowIsHeader && (!Whole(HeaderRow) || HeaderRow >= FirstDataRow) || LastDataRow is { } end && (!Whole(end) || end < FirstDataRow)))
        { ErrorMessage = "Controlla i numeri di riga: l'intestazione deve precedere i dati e l'ultima riga non può precedere la prima."; return false; }
        if (string.IsNullOrEmpty(DateDelimiter) || DateDelimiter.Length != 1 || char.IsLetterOrDigit(DateDelimiter[0]) || string.IsNullOrEmpty(TimeDelimiter) || TimeDelimiter.Length != 1 || char.IsLetterOrDigit(TimeDelimiter[0]))
        { ErrorMessage = "I separatori di data e ora devono essere singoli caratteri non alfanumerici."; return false; }
        return true;
    }

    private void ValidateImport()
    {
        if (_profile.ReadOnly) throw new InvalidOperationException("Connessione in sola lettura: importazione disattivata.");
        if (!ValidateRows()) throw new InvalidOperationException(ErrorMessage);
        ValidateTarget();
        if (_table != TargetTable.Trim()) throw new InvalidOperationException("La destinazione è cambiata: torna al passaggio Destinazione e aggiorna la mappatura.");
        if (!File.Exists(Path)) throw new InvalidOperationException("Seleziona un file esistente.");
        var fields = DataImporter.Open(ReadOptions()).Fields;
        if (!fields.SequenceEqual(_fields)) throw new InvalidOperationException("I campi del file sono cambiati: aggiorna la mappatura.");
        foreach (var column in _columns.Where(c => !c.IsNullable && c.DefaultValue is null && !c.IsAutoIncrement && !c.IsGenerated))
            if (!Mappings.Any(m => m.Target == column.Name && m.IsMapped)) throw new InvalidOperationException($"Associa il campo obbligatorio «{column.Name}».");
        if (Mappings.Any(m => m.IsMapped && !fields.Contains(m.Source))) throw new InvalidOperationException("La mappatura contiene un campo non più disponibile.");
    }

    private IReadOnlyList<string> CreateTableStatements()
    {
        var design = TableDesign.Empty(_table) with { Columns = _columns.Select(c => new DesignColumn(null, c.Name, c.DataType)).ToArray(), Options = new TableDesignOptions(Engine: _profile.Kind == DatabaseKind.MySql ? "InnoDB" : null) };
        return TableDesignSql.Build(_profile.Kind, _schema, null, design);
    }
}
