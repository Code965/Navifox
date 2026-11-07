namespace Navifox.Core.Models;

public sealed record ColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    string? DefaultValue = null,
    bool IsPrimaryKey = false,
    bool IsAutoIncrement = false,
    bool IsGenerated = false);
