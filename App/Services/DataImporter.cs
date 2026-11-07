using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace Navifox.App.Services;

public enum ImportFormat { Csv, Text, Json, Excel }

public sealed class ImportReadOptions
{
    public required ImportFormat Format { get; init; }
    public required string Path { get; init; }
    public Encoding Encoding { get; init; } = Encoding.UTF8;
    public char Delimiter { get; init; } = ',';
    public char? Qualifier { get; init; } = '"';
    public bool FirstRowIsHeader { get; init; } = true;
    public int SkipRows { get; init; }
    public string? Sheet { get; init; }
}

/// <summary>Sorgente dati per l'Import Wizard: intestazioni + righe di testo (null = valore assente).</summary>
public sealed record ImportSource(IReadOnlyList<string> Fields, IEnumerable<string?[]> Rows);

public static class DataImporter
{
    public static IReadOnlyList<string> ListSheets(string path)
    {
        using var stream = File.OpenRead(path);
        using var workbook = new XLWorkbook(stream);
        return workbook.Worksheets.Select(sheet => sheet.Name).ToArray();
    }

    /// <summary>Apre la sorgente; le righe vengono lette in modo pigro (CSV/testo in streaming).</summary>
    public static ImportSource Open(ImportReadOptions options) => options.Format switch
    {
        ImportFormat.Csv or ImportFormat.Text => OpenDelimited(options),
        ImportFormat.Json => OpenJson(options),
        ImportFormat.Excel => OpenExcel(options),
        _ => throw new NotSupportedException(options.Format.ToString())
    };

    // ───────────── CSV / testo delimitato ─────────────

    private static ImportSource OpenDelimited(ImportReadOptions options)
    {
        string[]? header = null;
        using (var reader = new StreamReader(options.Path, options.Encoding, detectEncodingFromByteOrderMarks: true))
        {
            using var records = ParseDelimited(reader, options.Delimiter, options.Qualifier).Skip(options.SkipRows).GetEnumerator();
            if (records.MoveNext()) header = records.Current;
        }
        header ??= [];
        var fields = options.FirstRowIsHeader
            ? Deduplicate(header.Select((name, index) => string.IsNullOrWhiteSpace(name) ? $"Campo{index + 1}" : name.Trim()))
            : header.Select((_, index) => $"Campo{index + 1}").ToArray();
        return new ImportSource(fields, ReadDelimitedRows(options, fields.Count));
    }

    private static IEnumerable<string?[]> ReadDelimitedRows(ImportReadOptions options, int width)
    {
        using var reader = new StreamReader(options.Path, options.Encoding, detectEncodingFromByteOrderMarks: true);
        var records = ParseDelimited(reader, options.Delimiter, options.Qualifier).Skip(options.SkipRows + (options.FirstRowIsHeader ? 1 : 0));
        foreach (var record in records)
        {
            if (record.Length == 1 && record[0].Length == 0) continue; // riga vuota
            var row = new string?[Math.Max(width, record.Length)];
            Array.Copy(record, row, record.Length);
            yield return row;
        }
    }

    /// <summary>Parser RFC 4180: qualificatori raddoppiati come escape, campi su più righe.</summary>
    public static IEnumerable<string[]> ParseDelimited(TextReader reader, char delimiter, char? qualifier)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var any = false;
        int read;
        while ((read = reader.Read()) != -1)
        {
            var c = (char)read;
            any = true;
            if (inQuotes)
            {
                if (c == qualifier)
                {
                    if (reader.Peek() == qualifier) { field.Append(c); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            if (qualifier is not null && c == qualifier && field.Length == 0) { inQuotes = true; continue; }
            if (c == delimiter) { fields.Add(field.ToString()); field.Clear(); continue; }
            if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n') reader.Read();
                fields.Add(field.ToString());
                field.Clear();
                yield return fields.ToArray();
                fields.Clear();
                any = false;
                continue;
            }
            field.Append(c);
        }
        if (any || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }

    // ───────────── JSON ─────────────

    private static ImportSource OpenJson(ImportReadOptions options)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(options.Path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var array = FindArray(document.RootElement) ?? throw new FormatException("Il file JSON deve contenere un array di oggetti.");
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray();
        foreach (var item in items.Take(1000))
            foreach (var property in item.EnumerateObject())
                if (seen.Add(property.Name)) fields.Add(property.Name);
        var rows = items.Select(item =>
        {
            var row = new string?[fields.Count];
            foreach (var property in item.EnumerateObject())
            {
                var index = fields.IndexOf(property.Name);
                if (index >= 0) row[index] = JsonText(property.Value);
            }
            return row;
        }).ToArray();
        return new ImportSource(fields, rows);
    }

    private static JsonElement? FindArray(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element,
        JsonValueKind.Object => element.EnumerateObject().Select(property => property.Value).FirstOrDefault(value => value.ValueKind == JsonValueKind.Array) is { ValueKind: JsonValueKind.Array } nested ? nested : null,
        _ => null
    };

    private static string? JsonText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText()
    };

    // ───────────── Excel ─────────────

    private static ImportSource OpenExcel(ImportReadOptions options)
    {
        using var stream = File.OpenRead(options.Path);
        using var workbook = new XLWorkbook(stream);
        var sheet = options.Sheet is { Length: > 0 } name && workbook.Worksheets.TryGetWorksheet(name, out var found) ? found : workbook.Worksheet(1);
        var range = sheet.RangeUsed();
        if (range is null) return new ImportSource([], []);
        var width = range.ColumnCount();
        var all = range.Rows().Skip(options.SkipRows).Select(row => Enumerable.Range(1, width).Select(column => ExcelText(row.Cell(column))).ToArray()).ToList();
        var header = all.Count == 0 ? [] : all[0];
        var fields = options.FirstRowIsHeader
            ? Deduplicate(header.Select((name, index) => string.IsNullOrWhiteSpace(name) ? $"Campo{index + 1}" : name!.Trim()))
            : Enumerable.Range(1, width).Select(index => $"Campo{index}").ToArray();
        return new ImportSource(fields, options.FirstRowIsHeader ? all.Skip(1).ToArray() : all.ToArray());
    }

    private static string? ExcelText(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        var value = cell.Value;
        if (value.IsDateTime) return value.GetDateTime().ToString(value.GetDateTime().TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (value.IsNumber) return value.GetNumber().ToString("R", CultureInfo.InvariantCulture);
        if (value.IsBoolean) return value.GetBoolean() ? "1" : "0";
        return cell.GetFormattedString();
    }

    private static IReadOnlyList<string> Deduplicate(IEnumerable<string> names)
    {
        var result = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var candidate = name;
            for (var suffix = 2; !used.Add(candidate); suffix++) candidate = $"{name}_{suffix}";
            result.Add(candidate);
        }
        return result;
    }
}
