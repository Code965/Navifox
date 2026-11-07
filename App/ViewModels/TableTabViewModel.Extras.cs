using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Campo della vista modulo (un record alla volta).</summary>
public sealed class FormField(string name, string type, bool isKey, string? reference, CellViewModel? cell, bool editable)
{
    public string Name { get; } = name;
    public string Type { get; } = type;
    public bool IsKey { get; } = isKey;
    public string? Reference { get; } = reference;
    public bool HasReference => Reference is not null;
    public CellViewModel? Cell { get; } = cell;
    public bool IsEditable { get; } = editable;
    public bool IsReadOnly => !editable;
}

/// <summary>Chiave esterna di una colonna: tabella e colonna di riferimento.</summary>
public sealed record ColumnReference(string Column, string Schema, string Table, string ReferencedColumn);

/// <summary>Vista modulo, editor JSON/immagine, copia come SQL, ricerca nei dati, chiavi esterne.</summary>
public partial class TableTabViewModel
{
    // ───────────── Editor di campo: testo / esadecimale / JSON / immagine ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonMode), nameof(IsImageMode), nameof(IsPlainTextMode), nameof(FieldModeTitle))]
    private string _fieldMode = "text";

    public bool IsJsonMode => FieldMode == "json";
    public bool IsImageMode => FieldMode == "image";
    public bool IsPlainTextMode => FieldMode == "text";
    public string FieldModeTitle => FieldMode switch { "hex" => "Esadecimale", "json" => "JSON", "image" => "Immagine", _ => "Testo" };

    partial void OnFieldModeChanged(string value)
    {
        IsHexMode = value == "hex";
        OnPropertyChanged(nameof(FieldModeLabel));
        RefreshFieldViews();
    }

    partial void OnCurrentCellChanged(CellViewModel? value)
    {
        // Riconosce da solo JSON e immagini quando si cambia cella con l'editor aperto.
        if (IsFieldPanelOpen && value is not null && FieldMode is "text" or "json" or "image")
            FieldMode = LooksLikeImage(value.Value) ? "image" : LooksLikeJson(value.EditText) ? "json" : FieldMode == "image" ? "text" : FieldMode;
        RefreshFieldViews();
        RefreshFormFields();
    }

    private void RefreshFieldViews()
    {
        OnPropertyChanged(nameof(CurrentCellJson));
        OnPropertyChanged(nameof(JsonError));
        OnPropertyChanged(nameof(CurrentCellImage));
        OnPropertyChanged(nameof(ImageInfo));
        OnPropertyChanged(nameof(CurrentReference));
        OnPropertyChanged(nameof(HasCurrentReference));
    }

    private static bool LooksLikeJson(string text) => text.TrimStart() is { Length: > 1 } t && (t[0] == '{' || t[0] == '[');

    private static bool LooksLikeImage(object? value) => value is byte[] { Length: > 8 } b &&
        (b[0] == 0x89 && b[1] == 0x50 || b[0] == 0xFF && b[1] == 0xD8 || b[0] == 0x47 && b[1] == 0x49 || b[0] == 0x42 && b[1] == 0x4D || b[0] == 0x52 && b[1] == 0x49 && b[8] == 0x57);

    /// <summary>JSON formattato (modificabile): salvando torna nella cella.</summary>
    public string CurrentCellJson
    {
        get
        {
            var text = CurrentCell?.EditText ?? string.Empty;
            try { return text.Trim().Length == 0 ? string.Empty : JsonSerializer.Serialize(JsonDocument.Parse(text).RootElement, JsonPretty); }
            catch (JsonException) { return text; }
        }
        set
        {
            if (CurrentCell is null || !CanEdit) return;
            CurrentCell.EditText = value;
            OnPropertyChanged(nameof(JsonError));
        }
    }

    private static readonly JsonSerializerOptions JsonPretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions JsonCompact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string JsonError
    {
        get
        {
            var text = CurrentCell?.EditText ?? string.Empty;
            if (text.Trim().Length == 0) return string.Empty;
            try { JsonDocument.Parse(text); return "JSON valido"; }
            catch (JsonException exception) { return "JSON non valido: " + exception.Message; }
        }
    }

    [RelayCommand]
    private void CompactJson()
    {
        if (CurrentCell is null || !CanEdit) return;
        try { CurrentCell.EditText = JsonSerializer.Serialize(JsonDocument.Parse(CurrentCell.EditText).RootElement, JsonCompact); }
        catch (JsonException) { return; }
        RefreshFieldViews();
    }

    public Avalonia.Media.Imaging.Bitmap? CurrentCellImage
    {
        get
        {
            if (CurrentCell?.Value is not byte[] bytes || !LooksLikeImage(bytes)) return null;
            try { return new Avalonia.Media.Imaging.Bitmap(new MemoryStream(bytes)); }
            catch { return null; }
        }
    }

    public string ImageInfo => CurrentCell?.Value is byte[] bytes
        ? CurrentCellImage is { } image ? $"{image.PixelSize.Width} × {image.PixelSize.Height} px · {bytes.Length:N0} byte" : $"Dati binari ({bytes.Length:N0} byte): non è un'immagine riconosciuta"
        : "Il campo non contiene un'immagine.";

    // ───────────── Vista modulo ─────────────

    [ObservableProperty] private bool _isFormView;
    public bool IsGridView => !IsFormView;
    public ObservableCollection<FormField> FormFields { get; } = [];

    partial void OnIsFormViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGridView));
        if (value && CurrentRow is null && Rows.Count > 0) CurrentRow = Rows[0];
        RefreshFormFields();
    }

    partial void OnCurrentRowChanged(TableRowViewModel? oldValue, TableRowViewModel? newValue) => RefreshFormFields();

    private void RefreshFormFields()
    {
        if (!IsFormView) return;
        FormFields.Clear();
        if (CurrentRow is not { } row) return;
        for (var i = 0; i < Columns.Count; i++)
        {
            var column = Columns[i];
            var reference = _references.FirstOrDefault(r => r.Column == column.Name);
            FormFields.Add(new FormField(column.Name, column.DataType, column.IsPrimaryKey, reference is null ? null : $"→ {reference.Table}.{reference.ReferencedColumn}",
                row.CellAt(i), CanEdit));
        }
        OnPropertyChanged(nameof(FormPosition));
    }

    public string FormPosition => CurrentRow is { } row && Rows.IndexOf(row) is var i and >= 0 ? $"Record {i + 1} di {Rows.Count:N0}" : "Nessun record";

    /// <summary>Spostamento nella vista modulo (la vista sincronizza anche la selezione della griglia).</summary>
    [RelayCommand]
    private void MoveRecord(string? where)
    {
        if (Rows.Count == 0) return;
        var index = CurrentRow is null ? -1 : Rows.IndexOf(CurrentRow);
        index = where switch
        {
            "first" => 0,
            "last" => Rows.Count - 1,
            "prev" => Math.Max(0, index - 1),
            _ => Math.Min(Rows.Count - 1, index + 1),
        };
        CurrentRow = Rows[index];
    }

    [RelayCommand]
    private void ToggleFormView() => IsFormView = !IsFormView;

    // ───────────── Copia come SQL ─────────────

    private string QualifiedName => _provider.UsesMySqlScriptSyntax ? _provider.QuoteIdentifier(TableName) : $"{_provider.QuoteIdentifier(Schema)}.{_provider.QuoteIdentifier(TableName)}";

    private IReadOnlyList<TableRowViewModel> RowsForCopy() => SelectedRows.Count > 0 ? SelectedRows : CurrentRow is { } row ? [row] : [];

    /// <summary>INSERT delle righe selezionate (valori attuali, comprese le modifiche non salvate).</summary>
    public string BuildInsertForSelection()
    {
        var rows = RowsForCopy();
        if (rows.Count == 0) return string.Empty;
        var values = rows.Select(row => (IReadOnlyList<object?>)Enumerable.Range(0, Columns.Count).Select(i => row.CellAt(i)?.Value).ToArray()).ToArray();
        return _provider.BuildInsert(Schema, TableName, ColumnNames, values).Trim();
    }

    /// <summary>UPDATE per ogni riga selezionata, con WHERE sulla chiave primaria.</summary>
    public string BuildUpdateForSelection()
    {
        var rows = RowsForCopy();
        var keys = KeyIndexes();
        if (rows.Count == 0) return string.Empty;
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            var set = Enumerable.Range(0, Columns.Count).Where(i => !keys.Contains(i))
                .Select(i => $"{_provider.QuoteIdentifier(Columns[i].Name)} = {_provider.FormatLiteral(row.CellAt(i)?.Value)}");
            var where = keys.Length > 0
                ? string.Join(" AND ", keys.Select(i => $"{_provider.QuoteIdentifier(Columns[i].Name)} = {_provider.FormatLiteral(row.CellAt(i)?.OriginalValue ?? row.CellAt(i)?.Value)}"))
                : "/* nessuna chiave primaria: completa la condizione */ 1 = 0";
            text.Append($"UPDATE {QualifiedName} SET {string.Join(", ", set)} WHERE {where};\n");
        }
        return text.ToString().Trim();
    }

    /// <summary>Righe selezionate con intestazione, separate da tabulazione (incollabili in Excel).</summary>
    public string BuildTsvForSelection()
    {
        var rows = RowsForCopy();
        var lines = new List<string> { string.Join('\t', ColumnNames) };
        lines.AddRange(rows.Select(row => string.Join('\t', Enumerable.Range(0, Columns.Count).Select(i => row.IsNull(i) ? string.Empty : row.TextAt(i)))));
        return string.Join(Environment.NewLine, lines);
    }

    // ───────────── Cerca nei dati ─────────────

    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _searchStatus = string.Empty;

    /// <summary>Riga e colonna trovate (la vista le seleziona e le porta in vista).</summary>
    public event Action<TableRowViewModel, int>? SearchHit;

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchOpen = !IsSearchOpen;
        if (!IsSearchOpen) SearchStatus = string.Empty;
    }

    [RelayCommand]
    private void FindNext(string? direction)
    {
        var query = SearchText.Trim();
        if (query.Length == 0 || Rows.Count == 0 || Columns.Count == 0) return;
        var backwards = direction == "prev";
        var startRow = CurrentRow is null ? (backwards ? Rows.Count : -1) : Rows.IndexOf(CurrentRow);
        var startColumn = CurrentColumnName.Length > 0 ? Columns.ToList().FindIndex(c => c.Name == CurrentColumnName) : -1;
        var total = Rows.Count * Columns.Count;
        var position = Math.Max(-1, startRow) * Columns.Count + startColumn;
        for (var step = 1; step <= total; step++)
        {
            var p = ((position + (backwards ? -step : step)) % total + total) % total;
            var (r, c) = (p / Columns.Count, p % Columns.Count);
            var row = Rows[r];
            if (row.IsNull(c) || !row.TextAt(c).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var matches = Rows.Sum(x => Enumerable.Range(0, Columns.Count).Count(i => !x.IsNull(i) && x.TextAt(i).Contains(query, StringComparison.OrdinalIgnoreCase)));
            SearchStatus = $"«{query}» in {Columns[c].Name}, record {r + 1} · {matches:N0} occorrenze nella pagina";
            SetCurrent(row, c);
            SearchHit?.Invoke(row, c);
            return;
        }
        SearchStatus = $"«{query}» non trovato nella pagina caricata (usa il Filtro per cercare in tutta la tabella).";
    }

    // ───────────── Chiavi esterne ─────────────

    private IReadOnlyList<ColumnReference> _references = [];
    public IReadOnlyList<ColumnReference> References => _references;
    public ColumnReference? CurrentReference => _references.FirstOrDefault(r => r.Column == CurrentColumnName);
    public bool HasCurrentReference => CurrentReference is not null && CanEdit;

    /// <summary>Legge le chiavi esterne della tabella (una volta, al primo caricamento).</summary>
    private async Task LoadReferencesAsync(System.Data.Common.DbConnection connection, CancellationToken token)
    {
        if (_references.Count > 0 || IsView) return;
        try
        {
            var design = await _provider.GetTableDesignAsync(connection, Database, Schema, TableName, token);
            _references = design.ForeignKeys.Where(k => k.Columns.Count == 1)
                .Select(k => new ColumnReference(k.Columns[0], k.ReferencedSchema, k.ReferencedTable, k.ReferencedColumns[0])).ToArray();
        }
        catch { _references = []; }
        OnPropertyChanged(nameof(References));
        RefreshFieldViews();
    }

    /// <summary>Record della tabella collegata per scegliere il valore della chiave esterna.</summary>
    public async Task<QueryResult?> LoadReferenceRowsAsync(ColumnReference reference, string? filter)
    {
        await using var connection = await _databaseAccess.OpenAsync(_profile, Database, CancellationToken.None);
        var table = _provider.UsesMySqlScriptSyntax
            ? $"{_provider.QuoteIdentifier(reference.Schema)}.{_provider.QuoteIdentifier(reference.Table)}"
            : $"{_provider.QuoteIdentifier(reference.Schema)}.{_provider.QuoteIdentifier(reference.Table)}";
        var pattern = _provider.FormatLiteral("%" + filter + "%");
        string where;
        if (string.IsNullOrWhiteSpace(filter)) where = string.Empty;
        else if (_provider.Kind == Navifox.Core.Models.DatabaseKind.PostgreSql) where = $" WHERE CAST(t AS text) ILIKE {pattern}";
        else
        {
            var columns = (await _provider.GetColumnsAsync(connection, Database, reference.Schema, reference.Table, CancellationToken.None)).Select(c => _provider.QuoteIdentifier(c.Name)).ToArray();
            where = _provider.Kind switch
            {
                Navifox.Core.Models.DatabaseKind.MySql => $" WHERE CONCAT_WS(' ', {string.Join(", ", columns)}) LIKE {pattern}",
                Navifox.Core.Models.DatabaseKind.SqlServer => $" WHERE CONCAT_WS(' ', {string.Join(", ", columns)}) LIKE {pattern}",
                _ => $" WHERE ({string.Join(" || ' ' || ", columns.Select(c => $"COALESCE({c}, '')"))}) LIKE {pattern}",
            };
        }
        var sql = _provider.Kind.LimitSelect("*", $"FROM {table} t{where}", 500);
        return await _provider.ExecuteAsync(connection, sql, 500, CancellationToken.None);
    }

    public void SetCurrentValue(object? value)
    {
        if (CurrentCell is null || !CanEdit) return;
        CurrentCell.EditText = value is null ? string.Empty : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        RefreshFieldViews();
    }
}
