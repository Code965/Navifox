namespace MosaicDB.Core.Models;

public sealed record DatabaseObjectInfo(
    string Name,
    string Kind,
    long? EstimatedRows = null,
    long? DataBytes = null,
    string? Engine = null,
    DateTimeOffset? ModifiedAt = null,
    string? Comment = null,
    long? AutoIncrement = null);
