namespace MosaicDB.Core.Models;

public sealed record ColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    string? DefaultValue = null,
    bool IsPrimaryKey = false);
