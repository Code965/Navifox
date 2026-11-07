using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Strumenti › Registro cronologia.</summary>
public partial class HistoryTabViewModel : WorkspaceTabViewModel
{
    private readonly Action<SqlHistoryEntry> _openInQuery;

    public HistoryTabViewModel(Action<SqlHistoryEntry> openInQuery)
    {
        _openInQuery = openInQuery;
        SqlHistory.Entries.CollectionChanged += (_, _) => ApplyFilter();
        ApplyFilter();
    }

    public override string TabTitle => "Registro cronologia";
    public override string TabGlyph => "history";
    public ObservableCollection<SqlHistoryEntry> Visible { get; } = [];
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _onlyErrors;
    [ObservableProperty] private string _countLabel = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInQueryCommand), nameof(CopyCommand))]
    private SqlHistoryEntry? _selected;

    public event Action<string>? CopyRequested;

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnOnlyErrorsChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = FilterText.Trim();
        Visible.Clear();
        foreach (var entry in SqlHistory.Entries.Where(e => (!OnlyErrors || !e.Succeeded)
                     && (query.Length == 0 || e.Sql.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Target.Contains(query, StringComparison.OrdinalIgnoreCase))))
            Visible.Add(entry);
        CountLabel = $"{Visible.Count:N0} di {SqlHistory.Entries.Count:N0} istruzioni";
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenInQuery()
    {
        if (Selected is not null) _openInQuery(Selected);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy() => CopyRequested?.Invoke(Selected?.Sql ?? string.Empty);

    [RelayCommand]
    private void Clear() => SqlHistory.Clear();
}

public sealed record FindResult(string Table, string Column, long Matches, string Kind)
{
    public string MatchesLabel => Kind == "dati" ? $"{Matches:N0}" : "—";
}

/// <summary>Strumenti › Trova nel database: cerca un testo nei dati o nei nomi di tabelle e colonne.</summary>
public partial class FindTabViewModel : WorkspaceTabViewModel
{
    private static readonly string[] TextTypes = ["char", "text", "json", "enum", "set", "uuid", "xml", "citext"];
    private static readonly string[] NumericTypes = ["int", "decimal", "numeric", "float", "double", "real", "serial"];
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly ConnectionProfile _profile;
    private readonly Func<FindResult, string, Task> _open;
    private CancellationTokenSource? _running;

    public FindTabViewModel(IDatabaseAccessService databaseAccess, ConnectionProfile profile, string database, string schema, Func<FindResult, string, Task> open)
    {
        _databaseAccess = databaseAccess;
        _profile = profile;
        _open = open;
        Database = database;
        Schema = schema;
        TabTitle = $"Trova · {database} ({profile.Name})";
    }

    public override string TabTitle { get; }
    public override string TabGlyph => "search";
    public string Database { get; }
    public string Schema { get; }
    public IReadOnlyList<string> Modes { get; } = ["Nei dati", "Nei nomi di tabelle e colonne"];
    public ObservableCollection<FindResult> Results { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _mode = "Nei dati";
    [ObservableProperty] private bool _exactMatch;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusMessage = "Inserisci il testo da cercare e premi Invio.";
    [ObservableProperty] private FindResult? _selected;

    [RelayCommand]
    private async Task SearchAsync()
    {
        var text = SearchText.Trim();
        if (text.Length == 0 || IsRunning) return;
        _running = new CancellationTokenSource();
        var token = _running.Token;
        IsRunning = true;
        Results.Clear();
        Progress = 0;
        try
        {
            var model = await _databaseAccess.GetSchemaModelAsync(_profile, Database, Schema, token);
            if (Mode != "Nei dati")
            {
                foreach (var (table, columns) in model.Tables.OrderBy(p => p.Key))
                {
                    if (Matches(table, text)) Results.Add(new FindResult(table, string.Empty, 0, model.Views.Contains(table) ? "vista" : "tabella"));
                    foreach (var column in columns.Where(c => Matches(c.Name, text))) Results.Add(new FindResult(table, column.Name, 0, "colonna"));
                }
                StatusMessage = $"{Results.Count} corrispondenze nei nomi";
                return;
            }

            var provider = _databaseAccess.Provider(_profile);
            var numeric = double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _);
            var tables = model.Tables.Where(p => !model.Views.Contains(p.Key)).OrderBy(p => p.Key).ToArray();
            await using var connection = await _databaseAccess.OpenAsync(_profile, Database, token);
            var mysql = provider.UsesMySqlScriptSyntax;
            var pattern = ExactMatch ? text : $"%{text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            var literal = provider.FormatLiteral(pattern);
            for (var t = 0; t < tables.Length; t++)
            {
                token.ThrowIfCancellationRequested();
                var (table, columns) = tables[t];
                StatusMessage = $"Ricerca in {table} ({t + 1}/{tables.Length})…";
                Progress = t * 100d / tables.Length;
                var searchable = columns.Where(c => TextTypes.Any(c.DataType.ToLowerInvariant().Contains) || numeric && NumericTypes.Any(c.DataType.ToLowerInvariant().Contains)).ToArray();
                if (searchable.Length == 0) continue;
                var parts = searchable.Select((c, i) =>
                {
                    var column = provider.QuoteIdentifier(c.Name);
                    var expression = provider.Kind switch { DatabaseKind.MySql or DatabaseKind.Sqlite => column, DatabaseKind.SqlServer => $"CAST({column} AS nvarchar(max))", _ => $"CAST({column} AS text)" };
                    var escape = provider.Kind is DatabaseKind.SqlServer or DatabaseKind.Sqlite ? " ESCAPE '\\'" : string.Empty;
                    var condition = ExactMatch ? $"{expression} = {literal}" : $"{expression} {(provider.Kind == DatabaseKind.PostgreSql ? "ILIKE" : "LIKE")} {literal}{escape}";
                    return $"SUM(CASE WHEN {condition} THEN 1 ELSE 0 END) AS c{i}";
                });
                var sql = $"SELECT {string.Join(", ", parts)} FROM {provider.QuoteIdentifier(Schema)}.{provider.QuoteIdentifier(table)}";
                try
                {
                    var result = await provider.ExecuteAsync(connection, sql, 1, token);
                    if (result.Rows.Count == 0) continue;
                    for (var i = 0; i < searchable.Length; i++)
                        if (Convert.ToInt64(result.Rows[0][i] ?? 0L, System.Globalization.CultureInfo.InvariantCulture) is var count and > 0)
                            Results.Add(new FindResult(table, searchable[i].Name, count, "dati"));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { Results.Add(new FindResult(table, $"(errore: {exception.Message})", 0, "errore")); }
            }
            Progress = 100;
            StatusMessage = $"«{text}» trovato in {Results.Count(r => r.Kind == "dati")} colonne di {Results.Select(r => r.Table).Distinct().Count()} tabelle ({Results.Sum(r => r.Matches):N0} righe).";
        }
        catch (OperationCanceledException) { StatusMessage = "Ricerca interrotta."; }
        catch (Exception exception) { StatusMessage = $"Errore: {exception.Message}"; }
        finally { IsRunning = false; }
    }

    private bool Matches(string value, string text) =>
        ExactMatch ? value.Equals(text, StringComparison.OrdinalIgnoreCase) : value.Contains(text, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void Stop() => _running?.Cancel();

    [RelayCommand]
    private Task OpenAsync(FindResult? result) => (result ?? Selected) is { } target ? _open(target, SearchText.Trim()) : Task.CompletedTask;
}
