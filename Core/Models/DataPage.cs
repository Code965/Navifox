namespace MosaicDB.Core.Models;

public sealed record DataPage(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int PageIndex,
    int PageSize,
    bool HasMore);
