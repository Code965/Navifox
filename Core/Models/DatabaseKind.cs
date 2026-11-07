namespace Navifox.Core.Models;

public enum DatabaseKind
{
    MySql,
    PostgreSql
}

public static class DatabaseKindExtensions
{
    public static string DisplayName(this DatabaseKind kind) => kind switch
    {
        DatabaseKind.MySql => "MySQL / MariaDB",
        DatabaseKind.PostgreSql => "PostgreSQL",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
