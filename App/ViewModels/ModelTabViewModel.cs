using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Colonna mostrata in un riquadro del diagramma.</summary>
public sealed record DiagramColumn(string Name, string Type, bool IsPrimaryKey, bool IsForeignKey, bool IsNullable);

/// <summary>Riquadro tabella del diagramma ER (posizione in coordinate del diagramma).</summary>
public sealed class DiagramTable(string name, bool isView, IReadOnlyList<DiagramColumn> columns)
{
    public string Name { get; } = name;
    public bool IsView { get; } = isView;
    public IReadOnlyList<DiagramColumn> AllColumns { get; } = columns;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 200;
    public int RelationCount { get; set; }
}

/// <summary>Relazione FK → PK tra due riquadri.</summary>
public sealed record DiagramRelation(string Name, DiagramTable From, string FromColumn, DiagramTable To, string ToColumn);

/// <summary>Scheda «Modello»: diagramma relazionale dello schema.</summary>
public partial class ModelTabViewModel : WorkspaceTabViewModel
{
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly ConnectionProfile _profile;
    private readonly Func<string, Task>? _openTable;

    public ModelTabViewModel(IDatabaseAccessService databaseAccess, ConnectionProfile profile, string database, string schema, Func<string, Task>? openTable)
    {
        _databaseAccess = databaseAccess;
        _profile = profile;
        _openTable = openTable;
        Database = database;
        Schema = schema;
        TabTitle = schema == database ? $"Modello · {database} ({profile.Name})" : $"Modello · {database}.{schema} ({profile.Name})";
    }

    public override string TabTitle { get; }
    public override string TabGlyph => "network";
    public string Database { get; }
    public string Schema { get; }

    public IReadOnlyList<DiagramTable> Tables { get; private set; } = [];
    public IReadOnlyList<DiagramRelation> Relations { get; private set; } = [];

    /// <summary>Notifica alla vista che il diagramma va ridisegnato o riadattato.</summary>
    public event Action<bool>? DiagramChanged;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Caricamento del modello…";
    [ObservableProperty] private bool _keysOnly;
    [ObservableProperty] private bool _showTypes = true;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private DiagramTable? _selectedTable;

    public string ZoomLabel => $"{Zoom * 100:0}%";
    public string SelectionInfo => SelectedTable is null
        ? string.Empty
        : $"{SelectedTable.Name} · {SelectedTable.AllColumns.Count} colonne · {Relations.Count(r => r.From == SelectedTable || r.To == SelectedTable)} relazioni";

    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomLabel));
    partial void OnSelectedTableChanged(DiagramTable? value) => OnPropertyChanged(nameof(SelectionInfo));
    partial void OnKeysOnlyChanged(bool value) => DiagramChanged?.Invoke(false);
    partial void OnShowTypesChanged(bool value) => DiagramChanged?.Invoke(false);
    partial void OnSearchTextChanged(string value)
    {
        var match = string.IsNullOrWhiteSpace(value) ? null
            : Tables.FirstOrDefault(t => t.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
              ?? Tables.FirstOrDefault(t => t.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
        if (match is not null) SelectedTable = match;
        DiagramChanged?.Invoke(false);
    }

    public IReadOnlyList<DiagramColumn> VisibleColumns(DiagramTable table) =>
        KeysOnly ? table.AllColumns.Where(c => c.IsPrimaryKey || c.IsForeignKey).ToArray() : table.AllColumns;

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusMessage = "Caricamento del modello…";
        try
        {
            var model = await _databaseAccess.GetSchemaModelAsync(_profile, Database, Schema, CancellationToken.None);
            var fkColumns = model.ForeignKeys.SelectMany(fk => fk.Columns.Select(column => (fk.Table, column))).ToHashSet();
            var tables = model.Tables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new DiagramTable(pair.Key, model.Views.Contains(pair.Key),
                    pair.Value.Select(c => new DiagramColumn(c.Name, c.DataType, c.IsPrimaryKey, fkColumns.Contains((pair.Key, c.Name)), c.IsNullable)).ToArray()))
                .ToDictionary(t => t.Name, StringComparer.Ordinal);
            var relations = new List<DiagramRelation>();
            foreach (var fk in model.ForeignKeys)
            {
                if (!tables.TryGetValue(fk.Table, out var from) || !tables.TryGetValue(fk.ReferencedTable, out var to)) continue;
                for (var i = 0; i < fk.Columns.Count; i++) relations.Add(new DiagramRelation(fk.Name, from, fk.Columns[i], to, fk.ReferencedColumns.ElementAtOrDefault(i) ?? fk.ReferencedColumns[0]));
                from.RelationCount++;
                if (to != from) to.RelationCount++;
            }
            Tables = tables.Values.ToArray();
            Relations = relations;
            StatusMessage = $"{Tables.Count} tabelle · {model.ForeignKeys.Count} relazioni{(model.ForeignKeys.Count == 0 ? " (nessuna chiave esterna definita)" : string.Empty)}";
            DiagramChanged?.Invoke(true);
        }
        catch (Exception exception) { StatusMessage = $"Impossibile caricare il modello: {exception.Message}"; }
        finally { IsLoading = false; }
    }

    /// <summary>Disposizione a livelli: ogni componente connessa in colonne (riferite a sinistra), le tabelle isolate in griglia.</summary>
    public void AutoLayout(Func<DiagramTable, (double Width, double Height)> measure)
    {
        const double gapX = 90, gapY = 36, margin = 40;
        foreach (var table in Tables) table.Width = measure(table).Width;
        var neighbours = Tables.ToDictionary(t => t, _ => new HashSet<DiagramTable>());
        foreach (var relation in Relations.Where(r => r.From != r.To))
        {
            neighbours[relation.From].Add(relation.To);
            neighbours[relation.To].Add(relation.From);
        }
        var visited = new HashSet<DiagramTable>();
        var components = new List<List<List<DiagramTable>>>();
        foreach (var root in Tables.Where(t => neighbours[t].Count > 0).OrderByDescending(t => neighbours[t].Count))
        {
            if (!visited.Add(root)) continue;
            // BFS: livello = distanza dalla tabella più collegata
            var layers = new List<List<DiagramTable>> { new() { root } };
            var frontier = new List<DiagramTable> { root };
            while (frontier.Count > 0)
            {
                var next = frontier.SelectMany(t => neighbours[t]).Where(visited.Add).Distinct().ToList();
                if (next.Count > 0) layers.Add(next);
                frontier = next;
            }
            components.Add(layers);
        }

        double cursorX = margin, cursorY = margin, rowHeight = 0, maxRowWidth = Math.Max(1800, Math.Sqrt(Tables.Count) * 420);
        foreach (var layers in components)
        {
            var widths = layers.Select(layer => layer.Max(t => t.Width)).ToArray();
            var heights = layers.Select(layer => layer.Sum(t => measure(t).Height + gapY) - gapY).ToArray();
            var componentWidth = widths.Sum() + gapX * (layers.Count - 1);
            var componentHeight = heights.Max();
            if (cursorX > margin && cursorX + componentWidth > maxRowWidth)
            {
                cursorX = margin;
                cursorY += rowHeight + gapY * 2;
                rowHeight = 0;
            }
            var x = cursorX;
            for (var l = 0; l < layers.Count; l++)
            {
                var y = cursorY + (componentHeight - heights[l]) / 2;
                foreach (var table in layers[l].OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                {
                    table.X = x;
                    table.Y = y;
                    y += measure(table).Height + gapY;
                }
                x += widths[l] + gapX;
            }
            cursorX += componentWidth + gapX * 1.5;
            rowHeight = Math.Max(rowHeight, componentHeight);
        }

        // tabelle senza relazioni: griglia sotto
        var isolated = Tables.Where(t => !visited.Contains(t)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (isolated.Count == 0) return;
        cursorY += components.Count > 0 ? rowHeight + gapY * 2 : 0;
        var perRow = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(isolated.Count * 1.6)));
        for (var start = 0; start < isolated.Count; start += perRow)
        {
            var row = isolated.Skip(start).Take(perRow).ToList();
            var x = margin;
            foreach (var table in row)
            {
                table.X = x;
                table.Y = cursorY;
                x += table.Width + gapX / 2;
            }
            cursorY += row.Max(t => measure(t).Height) + gapY;
        }
    }

    public Task OpenTableAsync(DiagramTable table) => _openTable?.Invoke(table.Name) ?? Task.CompletedTask;
}
