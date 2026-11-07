namespace Navifox.Core.Models;

/// <summary>Oggetti di schema diversi dalle tabelle, gestiti con il loro SQL di definizione.</summary>
public enum SchemaObjectKind
{
    /// <summary>Funzioni e procedure (Kind dell'oggetto: FUNCTION o PROCEDURE).</summary>
    Routine,
    /// <summary>Trigger (Identity = tabella).</summary>
    Trigger,
    /// <summary>Eventi pianificati (solo MySQL/MariaDB).</summary>
    Event,
    /// <summary>Sequenze (solo PostgreSQL).</summary>
    Sequence,
    /// <summary>Viste (la definizione è la sola SELECT).</summary>
    View,
}

/// <summary>Proprietà di una sequenza PostgreSQL (Progetta sequenza).</summary>
public sealed record SequenceInfo(
    string Name,
    long Start = 1,
    long Increment = 1,
    long? Min = null,
    long? Max = null,
    long Cache = 1,
    bool Cycle = false,
    long? Current = null,
    string? OwnedBy = null,
    string DataType = "bigint");

/// <summary>
/// Piano di salvataggio di un oggetto: istruzioni da eseguire, se vanno in un'unica transazione,
/// un piano alternativo se il primo fallisce e le istruzioni per ripristinare la versione precedente.
/// </summary>
public sealed record SchemaObjectSavePlan(
    IReadOnlyList<string> Statements,
    bool Atomic = false,
    SchemaObjectSavePlan? Fallback = null,
    IReadOnlyList<string>? Restore = null);
