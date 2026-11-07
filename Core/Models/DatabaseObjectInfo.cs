namespace Navifox.Core.Models;

public sealed record DatabaseObjectInfo(
    string Name,
    string Kind,
    long? EstimatedRows = null,
    long? DataBytes = null,
    string? Engine = null,
    DateTimeOffset? ModifiedAt = null,
    string? Comment = null,
    long? AutoIncrement = null,
    string? Detail = null,
    string? Identity = null,
    string? BaseName = null)
{
    /// <summary>Nome reale sul server (per le funzioni PostgreSQL <see cref="Name"/> include gli argomenti).</summary>
    public string ObjectName => BaseName ?? Name;
}
