using System.Globalization;

namespace Navifox.Core;

/// <summary>
/// Totali della selezione nella griglia (come la barra di stato di Excel): conteggio, somma, media, minimo e massimo.
/// I numeri sono riconosciuti dal tipo (int, decimal, double…) o dal testo («1234.5»); le date danno solo minimo e massimo.
/// </summary>
public sealed record SelectionStats(int Count, int NullCount, int NumericCount, decimal Sum, decimal? Min, decimal? Max,
    DateTime? MinDate, DateTime? MaxDate, int DistinctCount)
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public int ValueCount => Count - NullCount;
    public decimal? Average => NumericCount > 0 ? Sum / NumericCount : null;

    public static SelectionStats Compute(IEnumerable<object?> values)
    {
        int count = 0, nulls = 0, numeric = 0, dates = 0;
        decimal sum = 0;
        decimal? min = null, max = null;
        DateTime? minDate = null, maxDate = null;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            count++;
            if (value is null or DBNull) { nulls++; continue; }
            if (distinct.Count < 100_000) distinct.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            if (AsNumber(value) is { } number)
            {
                numeric++;
                try { sum += number; } catch (OverflowException) { /* somma fuori scala: resta l'ultimo valore valido */ }
                min = min is null || number < min ? number : min;
                max = max is null || number > max ? number : max;
            }
            else if (AsDate(value) is { } date)
            {
                dates++;
                minDate = minDate is null || date < minDate ? date : minDate;
                maxDate = maxDate is null || date > maxDate ? date : maxDate;
            }
        }
        return new SelectionStats(count, nulls, numeric, sum, min, max, dates > 0 ? minDate : null, dates > 0 ? maxDate : null, distinct.Count);
    }

    /// <summary>Testo per la barra di stato; vuoto se la selezione ha meno di due celle.</summary>
    public string Format()
    {
        if (Count < 2) return string.Empty;
        var parts = new List<string> { $"Conteggio {Count.ToString("N0", Italian)}" };
        if (NumericCount > 0)
        {
            parts.Add($"Somma {Number(Sum)}");
            parts.Add($"Media {Number(Average!.Value)}");
            parts.Add($"Min {Number(Min!.Value)}");
            parts.Add($"Max {Number(Max!.Value)}");
            if (NumericCount < ValueCount) parts.Add($"{NumericCount.ToString("N0", Italian)} numerici");
        }
        else if (MinDate is { } from && MaxDate is { } to)
        {
            parts.Add($"Min {Date(from)}");
            parts.Add($"Max {Date(to)}");
        }
        else parts.Add($"Distinti {DistinctCount.ToString("N0", Italian)}");
        if (NullCount > 0) parts.Add($"NULL {NullCount.ToString("N0", Italian)}");
        return string.Join("  ·  ", parts);
    }

    private static string Number(decimal value)
    {
        var rounded = Math.Round(value, 4);
        return rounded == Math.Truncate(rounded) ? rounded.ToString("N0", Italian) : rounded.ToString("#,##0.####", Italian);
    }

    private static string Date(DateTime value) => value.TimeOfDay == TimeSpan.Zero
        ? value.ToString("dd/MM/yyyy", Italian) : value.ToString("dd/MM/yyyy HH:mm:ss", Italian);

    private static decimal? AsNumber(object value)
    {
        try
        {
            return value switch
            {
                bool => null,
                byte or sbyte or short or ushort or int or uint or long or ulong or decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                float f when float.IsFinite(f) => (decimal)f,
                double d when double.IsFinite(d) && Math.Abs(d) < 7.9e28 => (decimal)d,
                string s when s.Length is > 0 and < 40 && decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
        }
        catch (OverflowException) { return null; }
    }

    private static DateTime? AsDate(object value) => value switch
    {
        DateTime dateTime => dateTime,
        DateTimeOffset offset => offset.DateTime,
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        _ => null
    };
}
