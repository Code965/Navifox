namespace MosaicDB.Core.Models;

/// <summary>Chiave esterna: colonne di <see cref="Table"/> che puntano a <see cref="ReferencedTable"/>.</summary>
public sealed record ForeignKeyInfo(
    string Name,
    string Table,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns);

/// <summary>Struttura di uno schema per il modello relazionale.</summary>
public sealed record SchemaModel(
    IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>> Tables,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys,
    IReadOnlySet<string> Views);

/// <summary>Indicatori sintetici dello stato del server (Server Monitor).</summary>
public sealed record ServerHealth(
    string Version,
    TimeSpan? Uptime,
    long? Connections,
    long? MaxConnections,
    long? Running,
    long? Queries,
    long? SlowQueries,
    long? BytesReceived,
    long? BytesSent);
