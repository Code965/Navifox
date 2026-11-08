using System.Globalization;
using Navifox.Core.Models;

namespace Navifox.Core;

/// <summary>Esito del confronto per un risultato: righe presenti solo qui (rispetto al primo) e righe mancanti.</summary>
public sealed record ResultDifference(int OnlyHere, int Missing, bool SameColumns, IReadOnlySet<int> DifferentRows);

/// <summary>
/// Confronto di più risultati della stessa query (es. Sviluppo, Test, Produzione): il primo è il riferimento.
/// Le righe si confrontano per contenuto (come multinsieme), quindi l'ordine non conta; le colonne per nome.
/// </summary>
public static class ResultComparer
{
    public static IReadOnlyList<ResultDifference> Compare(IReadOnlyList<QueryResult?> results)
    {
        var reference = results.FirstOrDefault(r => r is not null);
        var referenceCounts = reference is null ? new Dictionary<string, int>() : Counts(reference);
        var referenceColumns = reference?.Columns.Select(c => c.Name.ToLowerInvariant()).ToArray() ?? [];
        var output = new List<ResultDifference>();
        foreach (var result in results)
        {
            if (result is null) { output.Add(new ResultDifference(0, 0, false, new HashSet<int>())); continue; }
            var sameColumns = result.Columns.Select(c => c.Name.ToLowerInvariant()).SequenceEqual(referenceColumns);
            var remaining = new Dictionary<string, int>(referenceCounts);
            var different = new HashSet<int>();
            for (var i = 0; i < result.Rows.Count; i++)
            {
                var key = Key(result.Rows[i]);
                if (remaining.TryGetValue(key, out var count) && count > 0) remaining[key] = count - 1;
                else different.Add(i);
            }
            output.Add(new ResultDifference(different.Count, remaining.Values.Sum(), sameColumns, different));
        }
        return output;
    }

    public static bool AllEqual(IReadOnlyList<ResultDifference> differences) =>
        differences.All(d => d.SameColumns && d.OnlyHere == 0 && d.Missing == 0);

    private static Dictionary<string, int> Counts(QueryResult result)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in result.Rows)
        {
            var key = Key(row);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    private static string Key(IReadOnlyList<object?> row) => string.Join("\u001F", row.Select(Normalize));

    private static string Normalize(object? value) => value switch
    {
        null or DBNull => "␀",
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        decimal number => number.ToString("0.############################", CultureInfo.InvariantCulture),
        double number => ((decimal)number).ToString("0.############################", CultureInfo.InvariantCulture),
        float number => ((decimal)number).ToString("0.############################", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        bool flag => flag ? "1" : "0",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
