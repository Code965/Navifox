using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public partial class BuilderColumn(string table, string name, string type, bool isKey) : ObservableObject
{
    public string Table { get; } = table;
    public string Name { get; } = name;
    public string Type { get; } = type;
    public bool IsKey { get; } = isKey;
    public string Icon => IsKey ? "key" : "column";
    [ObservableProperty] private bool _isSelected;
}

public partial class BuilderJoin(ForeignKeyInfo key, string otherTable, bool outgoing) : ObservableObject
{
    public ForeignKeyInfo Key { get; } = key;
    public string Table { get; } = otherTable;
    public bool Outgoing { get; } = outgoing;
    public string Label => Outgoing
        ? $"{Key.Table}.{string.Join(",", Key.Columns)} → {Key.ReferencedTable}.{string.Join(",", Key.ReferencedColumns)}"
        : $"{Key.Table}.{string.Join(",", Key.Columns)} → {Key.ReferencedTable}.{string.Join(",", Key.ReferencedColumns)}";
    public IReadOnlyList<string> JoinTypes { get; } = ["INNER JOIN", "LEFT JOIN", "RIGHT JOIN"];
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _joinType = "LEFT JOIN";
}

/// <summary>Query Builder: tabella principale, join suggeriti dalle chiavi esterne, colonne, filtro, ordinamento e limite.</summary>
public partial class QueryBuilderViewModel : ObservableObject
{
    private readonly SchemaModel _schema;
    private readonly IDialectProvider _provider;
    private readonly string _schemaName;

    public QueryBuilderViewModel(SchemaModel schema, IDialectProvider provider, string schemaName)
    {
        _schema = schema;
        _provider = provider;
        _schemaName = schemaName;
        Tables = schema.Tables.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
        _mainTable = Tables.FirstOrDefault();
        Rebuild();
    }

    public event Action<bool>? CloseRequested;
    public IReadOnlyList<string> Tables { get; }
    public ObservableCollection<BuilderColumn> Columns { get; } = [];
    public ObservableCollection<BuilderJoin> Joins { get; } = [];
    public IReadOnlyList<string> Directions { get; } = ["ASC", "DESC"];
    public ObservableCollection<string> OrderColumns { get; } = [];

    [ObservableProperty] private string? _mainTable;
    [ObservableProperty] private bool _distinct;
    [ObservableProperty] private string _where = string.Empty;
    [ObservableProperty] private string? _orderBy;
    [ObservableProperty] private string _direction = "ASC";
    [ObservableProperty] private decimal? _limit = 100;
    [ObservableProperty] private string _generatedSql = string.Empty;
    [ObservableProperty] private string _tableFilter = string.Empty;

    public IEnumerable<string> FilteredTables => Tables.Where(t => TableFilter.Length == 0 || t.Contains(TableFilter, StringComparison.OrdinalIgnoreCase));

    partial void OnTableFilterChanged(string value) => OnPropertyChanged(nameof(FilteredTables));
    partial void OnMainTableChanged(string? value) => Rebuild();
    partial void OnDistinctChanged(bool value) => Generate();
    partial void OnWhereChanged(string value) => Generate();
    partial void OnOrderByChanged(string? value) => Generate();
    partial void OnDirectionChanged(string value) => Generate();
    partial void OnLimitChanged(decimal? value) => Generate();

    private void Rebuild()
    {
        foreach (var join in Joins) join.PropertyChanged -= OnPartChanged;
        Joins.Clear();
        if (MainTable is { } main)
        {
            foreach (var key in _schema.ForeignKeys.Where(k => k.Table == main && k.ReferencedTable != main)) Joins.Add(new BuilderJoin(key, key.ReferencedTable, true));
            foreach (var key in _schema.ForeignKeys.Where(k => k.ReferencedTable == main && k.Table != main)) Joins.Add(new BuilderJoin(key, key.Table, false));
        }
        foreach (var join in Joins) join.PropertyChanged += OnPartChanged;
        RebuildColumns();
    }

    private void RebuildColumns()
    {
        var previous = Columns.Where(c => c.IsSelected).Select(c => (c.Table, c.Name)).ToHashSet();
        foreach (var column in Columns) column.PropertyChanged -= OnPartChanged;
        Columns.Clear();
        OrderColumns.Clear();
        foreach (var table in ActiveTables())
        {
            if (!_schema.Tables.TryGetValue(table, out var columns)) continue;
            foreach (var column in columns)
            {
                var item = new BuilderColumn(table, column.Name, column.DataType, column.IsPrimaryKey) { IsSelected = previous.Contains((table, column.Name)) };
                item.PropertyChanged += OnPartChanged;
                Columns.Add(item);
                OrderColumns.Add($"{Alias(table)}.{column.Name}");
            }
        }
        if (OrderBy is not null && !OrderColumns.Contains(OrderBy)) OrderBy = null;
        Generate();
    }

    private void OnPartChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is BuilderJoin && e.PropertyName == nameof(BuilderJoin.IsSelected)) RebuildColumns();
        else Generate();
    }

    private IEnumerable<string> ActiveTables()
    {
        if (MainTable is null) yield break;
        yield return MainTable;
        foreach (var join in Joins.Where(j => j.IsSelected)) yield return join.Table;
    }

    private string Alias(string table) => table == MainTable ? "t0" : $"t{Joins.Where(j => j.IsSelected).ToList().FindIndex(j => j.Table == table) + 1}";

    private string Name(string table) => $"{_provider.QuoteIdentifier(table)}";

    [RelayCommand]
    private void SelectAll(string? value)
    {
        foreach (var column in Columns) column.IsSelected = value != "none";
    }

    private void Generate()
    {
        if (MainTable is null)
        {
            GeneratedSql = string.Empty;
            return;
        }
        var selected = Columns.Where(c => c.IsSelected).ToArray();
        var fields = selected.Length == 0
            ? (Joins.Any(j => j.IsSelected) ? "t0.*" : "*")
            : string.Join(",\n    ", selected.Select(c => $"{Alias(c.Table)}.{_provider.QuoteIdentifier(c.Name)}"));
        var sql = $"SELECT {(Distinct ? "DISTINCT " : string.Empty)}{(selected.Length > 1 ? "\n    " : string.Empty)}{fields}\nFROM {Name(MainTable)} t0";
        foreach (var join in Joins.Where(j => j.IsSelected))
        {
            var alias = Alias(join.Table);
            var conditions = join.Key.Columns.Select((column, index) => join.Outgoing
                ? $"{alias}.{_provider.QuoteIdentifier(join.Key.ReferencedColumns[index])} = t0.{_provider.QuoteIdentifier(column)}"
                : $"{alias}.{_provider.QuoteIdentifier(column)} = t0.{_provider.QuoteIdentifier(join.Key.ReferencedColumns[index])}");
            sql += $"\n    {join.JoinType} {Name(join.Table)} {alias} ON {string.Join(" AND ", conditions)}";
        }
        if (!string.IsNullOrWhiteSpace(Where)) sql += $"\nWHERE {Where.Trim()}";
        if (OrderBy is { } order)
        {
            var parts = order.Split('.', 2);
            sql += $"\nORDER BY {parts[0]}.{_provider.QuoteIdentifier(parts[1])} {Direction}";
        }
        if (Limit is > 0)
        {
            if (_provider.Kind == Navifox.Core.Models.DatabaseKind.SqlServer) sql = (Distinct ? "SELECT DISTINCT TOP (" + (int)Limit + ") " + sql["SELECT DISTINCT ".Length..] : "SELECT TOP (" + (int)Limit + ")" + sql["SELECT".Length..]);
            else sql += $"\nLIMIT {(int)Limit}";
        }
        GeneratedSql = sql + ";";
    }

    [RelayCommand]
    private void Confirm() => CloseRequested?.Invoke(true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
