using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using ClosedXML.Excel;
using Navifox.Core;

namespace Navifox.App.Services;

public enum ExportFormat { Text, Csv, Html, Excel, Sql, Xml, Json, Markdown }

public sealed class ExportOptions
{
    public required ExportFormat Format { get; init; }
    public required string Path { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public Encoding Encoding { get; init; } = new UTF8Encoding(false);
    public bool IncludeHeader { get; init; } = true;
    public string Delimiter { get; init; } = ",";
    public string Qualifier { get; init; } = "\"";
    public string LineEnding { get; init; } = "\r\n";
    public string NullText { get; init; } = string.Empty;
    public string DateFormat { get; init; } = "yyyy-MM-dd HH:mm:ss";
    public string DecimalSeparator { get; init; } = ".";
    public bool QuoteAll { get; init; }
    public string TableName { get; init; } = "table";
    public string Schema { get; init; } = string.Empty;
    public string? CreateTableDdl { get; init; }
    public int SqlBatchSize { get; init; } = 100;
    public bool PrettyJson { get; init; } = true;
    public string Title { get; init; } = "Export";
}

/// <summary>Scrive righe in streaming nei formati supportati dall'Export Wizard.</summary>
public static class DataExporter
{
    public static async Task<long> ExportAsync(ExportOptions options, IAsyncEnumerable<IReadOnlyList<object?>> rows, IDialectProvider provider, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (options.Format == ExportFormat.Excel) return await ExportExcelAsync(options, rows, progress, cancellationToken);
        await using var stream = new FileStream(options.Path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await using var writer = new StreamWriter(stream, options.Encoding) { NewLine = options.LineEnding };
        var sink = CreateSink(options, writer, provider);
        await sink.BeginAsync();
        long count = 0;
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            await sink.WriteRowAsync(row);
            if (++count % 2000 == 0) progress?.Report(count);
        }
        await sink.EndAsync();
        await writer.FlushAsync(cancellationToken);
        progress?.Report(count);
        return count;
    }

    private static async Task<long> ExportExcelAsync(ExportOptions options, IAsyncEnumerable<IReadOnlyList<object?>> rows, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        const int maxRows = 1_048_575;
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SafeSheetName(options.TableName));
        var line = 1;
        if (options.IncludeHeader)
        {
            for (var c = 0; c < options.Columns.Count; c++) sheet.Cell(1, c + 1).Value = options.Columns[c];
            var header = sheet.Range(1, 1, 1, Math.Max(1, options.Columns.Count));
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFEDE2");
            sheet.SheetView.FreezeRows(1);
            line++;
        }
        long count = 0;
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            if (line > maxRows) throw new InvalidOperationException($"Excel supporta al massimo {maxRows:N0} righe per foglio: applica un filtro o usa CSV.");
            for (var c = 0; c < row.Count; c++)
            {
                var cell = sheet.Cell(line, c + 1);
                switch (row[c])
                {
                    case null: if (options.NullText.Length > 0) cell.Value = options.NullText; break;
                    case DateTime date: cell.Value = date; cell.Style.DateFormat.Format = options.DateFormat; break;
                    case DateTimeOffset offset: cell.Value = offset.DateTime; cell.Style.DateFormat.Format = options.DateFormat; break;
                    case bool flag: cell.Value = flag; break;
                    case byte or sbyte or short or ushort or int or uint or long or float or double or decimal:
                        cell.Value = Convert.ToDouble(row[c], CultureInfo.InvariantCulture); break;
                    case ulong big: cell.Value = big.ToString(CultureInfo.InvariantCulture); break;
                    case byte[] bytes: cell.Value = $"(BLOB {bytes.Length} byte)"; break;
                    default: cell.Value = Truncate(Convert.ToString(row[c], CultureInfo.InvariantCulture) ?? string.Empty, 32767); break;
                }
            }
            line++;
            if (++count % 2000 == 0) progress?.Report(count);
        }
        if (count <= 5000) sheet.Columns().AdjustToContents(1, Math.Min(line, 200));
        await using (var output = new FileStream(options.Path, FileMode.Create, FileAccess.Write, FileShare.None)) workbook.SaveAs(output);
        progress?.Report(count);
        return count;
    }

    private static string SafeSheetName(string name)
    {
        var clean = new string(name.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "Dati" : clean.Length > 31 ? clean[..31] : clean;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    // ───────────── Formati testuali ─────────────

    private abstract class Sink(ExportOptions options, StreamWriter writer)
    {
        protected ExportOptions Options { get; } = options;
        protected StreamWriter Writer { get; } = writer;
        public virtual Task BeginAsync() => Task.CompletedTask;
        public abstract Task WriteRowAsync(IReadOnlyList<object?> row);
        public virtual Task EndAsync() => Task.CompletedTask;

        protected string FormatValue(object? value) => value switch
        {
            null => Options.NullText,
            DateTime date => date.ToString(Options.DateFormat, CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToString(Options.DateFormat, CultureInfo.InvariantCulture),
            bool flag => flag ? "1" : "0",
            byte[] bytes => Convert.ToBase64String(bytes),
            float or double or decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!.Replace(".", Options.DecimalSeparator),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static Sink CreateSink(ExportOptions options, StreamWriter writer, IDialectProvider provider) => options.Format switch
    {
        ExportFormat.Text or ExportFormat.Csv => new DelimitedSink(options, writer),
        ExportFormat.Html => new HtmlSink(options, writer),
        ExportFormat.Sql => new SqlSink(options, writer, provider),
        ExportFormat.Xml => new XmlSink(options, writer),
        ExportFormat.Json => new JsonSink(options, writer),
        ExportFormat.Markdown => new MarkdownSink(options, writer),
        _ => throw new NotSupportedException(options.Format.ToString())
    };

    private sealed class DelimitedSink(ExportOptions options, StreamWriter writer) : Sink(options, writer)
    {
        public override async Task BeginAsync()
        {
            if (Options.IncludeHeader) await Writer.WriteLineAsync(string.Join(Options.Delimiter, Options.Columns.Select(column => Quote(column, true))));
        }

        public override Task WriteRowAsync(IReadOnlyList<object?> row) =>
            Writer.WriteLineAsync(string.Join(Options.Delimiter, row.Select(value => value is null ? Options.NullText : Quote(FormatValue(value), value is string))));

        private string Quote(string text, bool isText)
        {
            var q = Options.Qualifier;
            if (q.Length == 0) return text;
            var needs = Options.QuoteAll && isText || text.Contains(Options.Delimiter) || text.Contains(q) || text.Contains('\n') || text.Contains('\r');
            return needs ? q + text.Replace(q, q + q) + q : text;
        }
    }

    private sealed class HtmlSink(ExportOptions options, StreamWriter writer) : Sink(options, writer)
    {
        public override async Task BeginAsync()
        {
            await Writer.WriteLineAsync($"""
                <!DOCTYPE html>
                <html lang="it"><head><meta charset="{Options.Encoding.WebName}"><title>{WebUtility.HtmlEncode(Options.Title)}</title>
                <style>
                body{"{"}font-family:Segoe UI,Inter,sans-serif;background:#F6F4F1;color:#1D1B19;margin:24px{"}"}
                h1{"{"}font-size:18px;font-weight:600{"}"}
                table{"{"}border-collapse:collapse;background:#fff;font-size:13px{"}"}
                th{"{"}background:#FFEDE2;color:#C2410C;text-align:left;font-weight:600{"}"}
                th,td{"{"}border:1px solid #E3DED7;padding:4px 8px;white-space:nowrap{"}"}
                tr:nth-child(even) td{"{"}background:#FAF8F5{"}"}
                td.null{"{"}color:#9BA1A9;font-style:italic{"}"}
                </style></head><body>
                <h1>{WebUtility.HtmlEncode(Options.Title)}</h1>
                <table>
                """);
            if (Options.IncludeHeader) await Writer.WriteLineAsync("<thead><tr>" + string.Concat(Options.Columns.Select(column => $"<th>{WebUtility.HtmlEncode(column)}</th>")) + "</tr></thead>");
            await Writer.WriteLineAsync("<tbody>");
        }

        public override Task WriteRowAsync(IReadOnlyList<object?> row) =>
            Writer.WriteLineAsync("<tr>" + string.Concat(row.Select(value => value is null
                ? $"<td class=\"null\">{WebUtility.HtmlEncode(Options.NullText.Length > 0 ? Options.NullText : "NULL")}</td>"
                : $"<td>{WebUtility.HtmlEncode(FormatValue(value))}</td>")) + "</tr>");

        public override Task EndAsync() => Writer.WriteLineAsync("</tbody></table></body></html>");
    }

    private sealed class MarkdownSink(ExportOptions options, StreamWriter writer) : Sink(options, writer)
    {
        public override async Task BeginAsync()
        {
            await Writer.WriteLineAsync("| " + string.Join(" | ", Options.Columns.Select(Escape)) + " |");
            await Writer.WriteLineAsync("|" + string.Concat(Options.Columns.Select(_ => " --- |")));
        }

        public override Task WriteRowAsync(IReadOnlyList<object?> row) =>
            Writer.WriteLineAsync("| " + string.Join(" | ", row.Select(value => Escape(value is null ? Options.NullText : FormatValue(value)))) + " |");

        private static string Escape(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }

    private sealed class SqlSink(ExportOptions options, StreamWriter writer, IDialectProvider provider) : Sink(options, writer)
    {
        private readonly List<IReadOnlyList<object?>> _batch = [];

        public override async Task BeginAsync()
        {
            await Writer.WriteLineAsync($"-- Navifox export · {Options.Title} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            if (Options.CreateTableDdl is { } ddl) await Writer.WriteLineAsync(Environment.NewLine + ddl + Environment.NewLine);
        }

        public override async Task WriteRowAsync(IReadOnlyList<object?> row)
        {
            _batch.Add(row);
            if (_batch.Count >= Options.SqlBatchSize) await FlushAsync();
        }

        public override Task EndAsync() => FlushAsync();

        private async Task FlushAsync()
        {
            if (_batch.Count == 0) return;
            await Writer.WriteAsync(provider.BuildInsert(Options.Schema, Options.TableName, Options.Columns, _batch).Replace("\n", Options.LineEnding));
            _batch.Clear();
        }
    }

    private sealed class XmlSink(ExportOptions options, StreamWriter writer) : Sink(options, writer)
    {
        private XmlWriter? _xml;

        public override Task BeginAsync()
        {
            _xml = XmlWriter.Create(Writer, new XmlWriterSettings { Indent = true, Async = true, NewLineChars = Options.LineEnding });
            _xml.WriteStartDocument();
            _xml.WriteStartElement("RECORDS");
            return Task.CompletedTask;
        }

        public override Task WriteRowAsync(IReadOnlyList<object?> row)
        {
            _xml!.WriteStartElement("RECORD");
            for (var c = 0; c < row.Count; c++)
            {
                _xml.WriteStartElement(XmlConvert.EncodeLocalName(Options.Columns[c]));
                if (row[c] is null) _xml.WriteAttributeString("null", "true");
                else _xml.WriteString(FormatValue(row[c]));
                _xml.WriteEndElement();
            }
            _xml.WriteEndElement();
            return Task.CompletedTask;
        }

        public override async Task EndAsync()
        {
            _xml!.WriteEndElement();
            _xml.WriteEndDocument();
            await _xml.FlushAsync();
        }
    }

    private sealed class JsonSink(ExportOptions options, StreamWriter writer) : Sink(options, writer)
    {
        private Utf8JsonWriter? _json;
        private MemoryStream? _buffer;

        public override Task BeginAsync()
        {
            _buffer = new MemoryStream();
            _json = new Utf8JsonWriter(_buffer, new JsonWriterOptions { Indented = Options.PrettyJson, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            _json.WriteStartArray();
            return Task.CompletedTask;
        }

        public override async Task WriteRowAsync(IReadOnlyList<object?> row)
        {
            _json!.WriteStartObject();
            for (var c = 0; c < row.Count; c++)
            {
                var name = Options.Columns[c];
                switch (row[c])
                {
                    case null: _json.WriteNull(name); break;
                    case bool flag: _json.WriteBoolean(name, flag); break;
                    case byte or sbyte or short or ushort or int or uint or long: _json.WriteNumber(name, Convert.ToInt64(row[c], CultureInfo.InvariantCulture)); break;
                    case ulong big: _json.WriteNumber(name, big); break;
                    case float or double: _json.WriteNumber(name, Convert.ToDouble(row[c], CultureInfo.InvariantCulture)); break;
                    case decimal number: _json.WriteNumber(name, number); break;
                    default: _json.WriteString(name, FormatValue(row[c])); break;
                }
            }
            _json.WriteEndObject();
            if (_buffer!.Length > 1 << 20) await DrainAsync();
        }

        public override async Task EndAsync()
        {
            _json!.WriteEndArray();
            await DrainAsync();
            await _json.DisposeAsync();
        }

        private async Task DrainAsync()
        {
            await _json!.FlushAsync();
            await Writer.WriteAsync(Encoding.UTF8.GetString(_buffer!.GetBuffer(), 0, (int)_buffer.Length));
            _buffer.SetLength(0);
        }
    }
}
