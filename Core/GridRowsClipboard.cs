using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Navifox.Core;

/// <summary>Formato trasferibile tra finestre/processi: valori tipizzati, senza interpretare SQL dagli appunti.</summary>
public sealed record ClipboardCell(string Type, string? Value)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static ClipboardCell From(object? value) => value switch
    {
        null or DBNull => new("null", null),
        byte[] bytes => new("bytes", Convert.ToBase64String(bytes)),
        bool flag => new("bool", flag ? "1" : "0"),
        DateTime date => new("datetime", date.ToString("O", Invariant)),
        DateTimeOffset date => new("offset", date.ToString("O", Invariant)),
        DateOnly date => new("date", date.ToString("O", Invariant)),
        TimeOnly time => new("time", time.ToString("O", Invariant)),
        TimeSpan span => new("span", span.ToString("c", Invariant)),
        Guid id => new("guid", id.ToString("D")),
        sbyte or byte or short or ushort or int or uint or long or ulong or BigInteger => new("integer", Convert.ToString(value, Invariant)),
        decimal number => new("decimal", number.ToString(Invariant)),
        float number => new("float", number.ToString("R", Invariant)),
        double number => new("double", number.ToString("R", Invariant)),
        _ => new("string", Convert.ToString(value, Invariant) ?? "")
    };
    public object? ToValue() => Type switch
    {
        "null" => null,
        "string" => Value ?? throw new FormatException("Testo degli appunti non valido."),
        "bytes" => Convert.FromBase64String(Value!),
        "bool" when Value is "0" or "1" => Value == "1",
        "integer" => long.TryParse(Value, NumberStyles.Integer, Invariant, out var number) ? (object)number : BigInteger.Parse(Value!, Invariant),
        "decimal" => decimal.Parse(Value!, Invariant),
        "float" => float.Parse(Value!, Invariant),
        "double" => double.Parse(Value!, Invariant),
        "datetime" => DateTime.Parse(Value!, Invariant, DateTimeStyles.RoundtripKind),
        "offset" => DateTimeOffset.Parse(Value!, Invariant, DateTimeStyles.None),
        "date" => DateOnly.Parse(Value!, Invariant),
        "time" => TimeOnly.Parse(Value!, Invariant),
        "span" => TimeSpan.Parse(Value!, Invariant),
        "guid" => Guid.Parse(Value!),
        _ => throw new FormatException("Tipo di valore non riconosciuto negli appunti.")
    };
}

public sealed record GridRowsClipboard(string[] Columns, ClipboardCell[][] Rows, string Source = "", bool ByPosition = false, int Version = 1)
{
    public const string Format = "Navifox.Rows.v1";
    public string Serialize() => JsonSerializer.Serialize(this);
    public static GridRowsClipboard Deserialize(string json)
    {
        var data = JsonSerializer.Deserialize<GridRowsClipboard>(json) ?? throw new FormatException("Appunti vuoti.");
        data.Validate();
        return data;
    }
    public void Validate()
    {
        if (Version != 1 || Columns is not { Length: > 0 } || Rows is not { Length: > 0 } ||
            Columns.Any(string.IsNullOrWhiteSpace) || Columns.Distinct(StringComparer.Ordinal).Count() != Columns.Length ||
            Rows.Any(r => r is null || r.Length != Columns.Length || r.Any(c => c is null)))
            throw new FormatException("Gli appunti non contengono righe e colonne valide.");
    }
    public string ToTsv(bool headers = true)
    {
        static string Escape(string value) => value.IndexOfAny(['\t', '\r', '\n', '"']) >= 0 ? '"' + value.Replace("\"", "\"\"") + '"' : value;
        var lines = Rows.Select(r => string.Join('\t', r.Select(c => Escape(c.Type == "null" ? "\\N" : c.Type == "bytes" ? "0x" + Convert.ToHexString((byte[])c.ToValue()!) : c.Value ?? ""))));
        if (headers) lines = new[] { string.Join('\t', Columns.Select(Escape)) }.Concat(lines);
        return string.Join(Environment.NewLine, lines);
    }
}
