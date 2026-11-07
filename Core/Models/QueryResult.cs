namespace Navifox.Core.Models;

/// <summary>Esito di un'istruzione SQL arbitraria.</summary>
public sealed record QueryResult(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RecordsAffected,
    bool Truncated)
{
    public bool HasResultSet => Columns.Count > 0;
}

/// <summary>Opzioni di un database (set di caratteri / collation per MySQL, codifica per PostgreSQL).</summary>
public sealed record DatabaseOptions(string Name, string? CharacterSet = null, string? Collation = null);

public sealed record CharsetInfo(string Name, string DefaultCollation, IReadOnlyList<string> Collations);

/// <summary>DDL di una tabella o vista; <see cref="PostData"/> va eseguito dopo i dati (es. chiavi esterne).</summary>
public sealed record TableDdl(string Create, IReadOnlyList<string> PostData);
