using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Navifox.App.Controls;

namespace Navifox.App.ViewModels;

/// <summary>Scheda «Grafico» dei risultati: barre, linee, area o torta da una colonna di etichette e una numerica.</summary>
public partial class QueryTabViewModel
{
    public IReadOnlyList<string> ChartKinds { get; } = ["Barre", "Linee", "Area", "Torta"];
    public ObservableCollection<string> ChartLabelColumns { get; } = [];
    public ObservableCollection<string> ChartValueColumns { get; } = [];

    [ObservableProperty] private string _chartKind = "Barre";
    [ObservableProperty] private string? _chartLabelColumn;
    [ObservableProperty] private string? _chartValueColumn;
    [ObservableProperty] private IReadOnlyList<ChartPoint>? _chartPoints;
    [ObservableProperty] private string _chartInfo = string.Empty;

    private bool _preparingChart;
    partial void OnChartLabelColumnChanged(string? value) { if (!_preparingChart) RebuildChart(); }
    partial void OnChartValueColumnChanged(string? value) { if (!_preparingChart) RebuildChart(); }

    private static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case null: number = 0; return false;
            case double d: number = d; return true;
            case float f: number = f; return true;
            case decimal m: number = (double)m; return true;
            case IConvertible c when value is not string and not DateTime and not bool:
                try { number = c.ToDouble(CultureInfo.InvariantCulture); return true; } catch { number = 0; return false; }
            case string s: return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out number);
            default: number = 0; return false;
        }
    }

    /// <summary>Chiamato dopo ogni esecuzione: propone come etichetta la prima colonna testuale e come valore la prima numerica.</summary>
    private void PrepareChart()
    {
        ChartLabelColumns.Clear();
        ChartValueColumns.Clear();
        if (Columns.Count == 0 || Rows.Count == 0)
        {
            ChartPoints = null;
            ChartInfo = "Esegui una query che restituisce righe per vedere il grafico.";
            return;
        }
        var sample = Rows.Take(50).ToArray();
        for (var i = 0; i < Columns.Count; i++)
        {
            ChartLabelColumns.Add(Columns[i].Name);
            if (sample.Any(r => TryNumber(r.CellAt(i)?.Value, out _))) ChartValueColumns.Add(Columns[i].Name);
        }
        var firstValue = ChartValueColumns.FirstOrDefault(c => !c.Equals("id", StringComparison.OrdinalIgnoreCase)) ?? ChartValueColumns.FirstOrDefault();
        _preparingChart = true;
        try
        {
            ChartLabelColumn = Columns.Select(c => c.Name).FirstOrDefault(c => !ChartValueColumns.Contains(c)) ?? Columns[0].Name;
            ChartValueColumn = firstValue;
        }
        finally { _preparingChart = false; }
        RebuildChart();
    }

    private void RebuildChart()
    {
        var labelIndex = Columns.ToList().FindIndex(c => c.Name == ChartLabelColumn);
        var valueIndex = Columns.ToList().FindIndex(c => c.Name == ChartValueColumn);
        if (labelIndex < 0 || valueIndex < 0)
        {
            ChartPoints = null;
            ChartInfo = ChartValueColumns.Count == 0 ? "Il risultato non ha colonne numeriche: usa ad esempio COUNT(*) o SUM(...) con GROUP BY." : string.Empty;
            return;
        }
        var points = new List<ChartPoint>();
        foreach (var row in Rows.Take(500))
            if (TryNumber(row.CellAt(valueIndex)?.Value, out var number))
                points.Add(new ChartPoint(row.IsNull(labelIndex) ? "(NULL)" : row.TextAt(labelIndex), number));
        ChartPoints = points;
        ChartInfo = $"{points.Count:N0} punti{(Rows.Count > 500 ? " (prime 500 righe)" : string.Empty)} · totale {points.Sum(p => p.Value):N2} · media {(points.Count == 0 ? 0 : points.Average(p => p.Value)):N2}";
    }
}
