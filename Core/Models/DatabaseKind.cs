namespace Navifox.Core.Models;

public enum DatabaseKind
{
    MySql,
    PostgreSql,
    SqlServer,
    Sqlite
}

public static class DatabaseKindExtensions
{
    public static string DisplayName(this DatabaseKind kind) => kind switch
    {
        DatabaseKind.MySql => "MySQL / MariaDB",
        DatabaseKind.PostgreSql => "PostgreSQL",
        DatabaseKind.SqlServer => "SQL Server",
        DatabaseKind.Sqlite => "SQLite",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>Schema predefinito di un database: MySQL = il database stesso, PostgreSQL = public, SQL Server = dbo, SQLite = main.</summary>
    public static string DefaultSchema(this DatabaseKind kind, string database) => kind switch
    {
        DatabaseKind.MySql => database,
        DatabaseKind.SqlServer => "dbo",
        DatabaseKind.Sqlite => "main",
        _ => "public"
    };

    /// <summary>Nell'albero il database contiene schemi (PostgreSQL, SQL Server) oppure direttamente le categorie (MySQL, SQLite).</summary>
    public static bool HasSchemas(this DatabaseKind kind) => kind is DatabaseKind.PostgreSql or DatabaseKind.SqlServer;

    /// <summary>Il database è un file locale (nessun host, porta o utente).</summary>
    public static bool IsFileBased(this DatabaseKind kind) => kind == DatabaseKind.Sqlite;

    /// <summary>Istruzione che apre una transazione esplicita.</summary>
    public static string BeginTransactionSql(this DatabaseKind kind) => kind switch
    {
        DatabaseKind.MySql => "START TRANSACTION",
        DatabaseKind.SqlServer => "BEGIN TRANSACTION",
        _ => "BEGIN"
    };

    /// <summary>SELECT con al massimo <paramref name="rows"/> righe («FROM …» incluso nel corpo).</summary>
    public static string LimitSelect(this DatabaseKind kind, string columns, string fromAndWhere, int rows) => kind == DatabaseKind.SqlServer
        ? $"SELECT TOP ({rows}) {columns} {fromAndWhere}"
        : $"SELECT {columns} {fromAndWhere} LIMIT {rows}";
}
