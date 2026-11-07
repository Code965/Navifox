namespace Navifox.Core.Models;

/// <summary>
/// Struttura modificabile di una tabella (Progetta tabella). <c>OriginalName</c> = nome sul server (null = elemento nuovo):
/// serve a generare ALTER TABLE confrontando la versione caricata con quella modificata.
/// </summary>
public sealed record TableDesign(
    string? OriginalName,
    string Name,
    IReadOnlyList<DesignColumn> Columns,
    IReadOnlyList<DesignIndex> Indexes,
    IReadOnlyList<DesignForeignKey> ForeignKeys,
    IReadOnlyList<DesignTrigger> Triggers,
    TableDesignOptions Options,
    string? Comment,
    string? PrimaryKeyName = null)
{
    public static TableDesign Empty(string name) => new(null, name, [], [], [], [], new TableDesignOptions(), null);
}

/// <param name="Type">Tipo base (varchar, int, numeric, enum('a','b')…), senza lunghezza.</param>
/// <param name="KeyOrder">Posizione nella chiave primaria (0 = non fa parte della chiave).</param>
/// <param name="Default">Espressione SQL del valore predefinito ('testo', 0, CURRENT_TIMESTAMP…); null = nessuno.</param>
/// <param name="Expression">Espressione della colonna generata (Virtual).</param>
/// <param name="OnUpdate">MySQL: ON UPDATE (es. CURRENT_TIMESTAMP).</param>
public sealed record DesignColumn(
    string? OriginalName,
    string Name,
    string Type,
    int? Length = null,
    int? Decimals = null,
    bool NotNull = false,
    bool IsVirtual = false,
    string? Expression = null,
    int KeyOrder = 0,
    string? Default = null,
    string? Comment = null,
    bool AutoIncrement = false,
    bool Unsigned = false,
    bool Zerofill = false,
    string? OnUpdate = null,
    bool StoredGenerated = false,
    string? Collation = null);

/// <param name="Kind">NORMAL, UNIQUE, FULLTEXT, SPATIAL.</param>
/// <param name="Method">BTREE, HASH (MySQL) · btree, hash, gin, gist, brin (PostgreSQL).</param>
/// <param name="IsConstraint">PostgreSQL: indice creato da un vincolo UNIQUE (si elimina con DROP CONSTRAINT).</param>
public sealed record DesignIndex(
    string? OriginalName,
    string Name,
    IReadOnlyList<string> Columns,
    string Kind = "NORMAL",
    string? Method = null,
    string? Comment = null,
    bool IsConstraint = false);

public sealed record DesignForeignKey(
    string? OriginalName,
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    string OnDelete = "RESTRICT",
    string OnUpdate = "RESTRICT");

/// <param name="Events">INSERT, UPDATE, DELETE (PostgreSQL: anche combinati, es. "INSERT OR UPDATE").</param>
/// <param name="Body">MySQL: istruzione dopo FOR EACH ROW · PostgreSQL: «FOR EACH ROW EXECUTE FUNCTION f()».</param>
public sealed record DesignTrigger(
    string? OriginalName,
    string Name,
    string Timing,
    string Events,
    string Body);

/// <summary>Opzioni della tabella (MySQL: motore, set di caratteri, collation, AUTO_INCREMENT).</summary>
public sealed record TableDesignOptions(
    string? Engine = null,
    string? Charset = null,
    string? Collation = null,
    long? AutoIncrement = null,
    string? RowFormat = null);
