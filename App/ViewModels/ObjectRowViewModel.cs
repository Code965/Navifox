using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public sealed class ObjectRowViewModel(DatabaseObjectInfo info)
{
    public string Name => info.Name;
    public string Kind => info.Kind;
    public string Glyph => GlyphFor(info.Kind);
    public string Detail => info.Detail ?? string.Empty;

    public static string GlyphFor(string kind) => kind.ToUpperInvariant() switch
    {
        var k when k.Contains("VIEW") => "eye",
        "FUNCTION" or "PROCEDURE" => "function",
        "TRIGGER" => "zap",
        "EVENT" => "clock",
        "SEQUENCE" => "hash",
        "QUERY" => "bookmark",
        "BACKUP" => "archive",
        _ => "table"
    };
    public string Rows => info.EstimatedRows is long value ? value.ToString("N0") : "";
    public long RowsValue => info.EstimatedRows ?? -1;
    public string DataSize => info.DataBytes is long value ? FormatBytes(value) : "";
    public long DataSizeValue => info.DataBytes ?? -1;
    public string AutoIncrement => info.AutoIncrement is long value ? value.ToString("N0") : "0";
    public long AutoIncrementValue => info.AutoIncrement ?? 0;
    public string Engine => info.Engine ?? "";
    public string Modified => info.ModifiedAt?.ToLocalTime().ToString("g") ?? "";
    public DateTimeOffset ModifiedValue => info.ModifiedAt ?? DateTimeOffset.MinValue;
    public string Comment => info.Comment ?? string.Empty;
    public DatabaseObjectInfo Info => info;

    public bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Comment.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Detail.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.0} GB",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.0} MB",
        _ => $"{Math.Max(bytes / 1024d, bytes > 0 ? 1 : 0):0} KB"
    };
}
