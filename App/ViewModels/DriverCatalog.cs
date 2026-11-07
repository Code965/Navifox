using Avalonia.Media;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>
/// Voce del catalogo driver mostrata nella procedura di nuova connessione.
/// I badge sono monogrammi originali Navifox (niente loghi di terze parti).
/// <see cref="Kind"/> è valorizzato solo per i motori con un dialect disponibile
/// (direttamente o tramite protocollo compatibile).
/// </summary>
public sealed class DriverInfo
{
    public DriverInfo(string name, string monogram, string color, int popularity, int port, DatabaseKind? kind, string description, params string[] categories)
    {
        Name = name;
        Monogram = monogram;
        BadgeBrush = new SolidColorBrush(Color.Parse(color));
        Popularity = popularity;
        DefaultPort = port;
        Kind = kind;
        Description = description;
        Categories = categories;
    }

    public string Name { get; }
    public string Monogram { get; }
    public IBrush BadgeBrush { get; }
    public int Popularity { get; }
    public int DefaultPort { get; }
    public DatabaseKind? Kind { get; }
    public string Description { get; }
    public IReadOnlyList<string> Categories { get; }
    public string DefaultSslMode { get; init; } = "Prefer";
    public string? DefaultUser { get; init; }
    public bool IsSupported => Kind is not null;
    public bool IsComingSoon => Kind is null;
    public double TileOpacity => IsSupported ? 1 : 0.55;
}

public sealed record DriverCategory(string Name, string Glyph, int Count)
{
    public string CountLabel => Count.ToString();
}

public static class DriverCatalog
{
    public const string All = "Tutti";
    public const string Popular = "Popolari";

    public static IReadOnlyList<string> CategoryOrder { get; } =
        [All, Popular, "SQL", "Cloud", "NoSQL", "Analitici", "Serie temporali", "Embedded", "File", "Ricerca full-text", "Grafi"];

    public static string GlyphFor(string category) => category switch
    {
        All => "▤", Popular => "★", "SQL" => "▦", "Cloud" => "☁", "NoSQL" => "◇", "Analitici" => "▥",
        "Serie temporali" => "◷", "Embedded" => "▣", "File" => "▢", "Ricerca full-text" => "⌕", "Grafi" => "⬡", _ => "•"
    };

    public static IReadOnlyList<DriverInfo> Drivers { get; } =
    [
        new("MySQL", "My", "#2F6F95", 100, 3306, DatabaseKind.MySql, "Server MySQL 5.7 / 8.x", Popular, "SQL") { DefaultUser = "root" },
        new("PostgreSQL", "Pg", "#3B5C8C", 99, 5432, DatabaseKind.PostgreSql, "PostgreSQL 10 e successivi", Popular, "SQL") { DefaultUser = "postgres" },
        new("MariaDB", "Ma", "#8A5A3C", 95, 3306, DatabaseKind.MySql, "MariaDB 10.x / 11.x (protocollo MySQL)", Popular, "SQL") { DefaultUser = "root" },
        new("SQL Server", "Ms", "#B23A48", 92, 1433, null, "Microsoft SQL Server / Azure SQL", Popular, "SQL"),
        new("SQLite", "Lt", "#4F7C8A", 90, 0, null, "File di database locale", Popular, "SQL", "Embedded", "File"),
        new("Oracle", "Or", "#C0392B", 85, 1521, null, "Oracle Database 12c e successivi", Popular, "SQL"),
        new("Supabase", "Sb", "#2E9E6E", 80, 5432, DatabaseKind.PostgreSql, "PostgreSQL gestito (pooler o diretto)", Popular, "Cloud", "SQL") { DefaultSslMode = "Require", DefaultUser = "postgres" },
        new("Neon", "Ne", "#1F9C8F", 70, 5432, DatabaseKind.PostgreSql, "PostgreSQL serverless", "Cloud", "SQL") { DefaultSslMode = "Require" },
        new("Amazon Aurora MySQL", "Au", "#D9822B", 68, 3306, DatabaseKind.MySql, "Cluster Aurora compatibile MySQL", "Cloud", "SQL") { DefaultSslMode = "Require" },
        new("Amazon Aurora PostgreSQL", "Ap", "#C46D1F", 66, 5432, DatabaseKind.PostgreSql, "Cluster Aurora compatibile PostgreSQL", "Cloud", "SQL") { DefaultSslMode = "Require" },
        new("TiDB", "Ti", "#C4373D", 55, 4000, DatabaseKind.MySql, "Database distribuito (protocollo MySQL)", "SQL", "Cloud") { DefaultUser = "root" },
        new("CockroachDB", "Cr", "#5B4BAE", 54, 26257, DatabaseKind.PostgreSql, "SQL distribuito (protocollo PostgreSQL)", "SQL", "Cloud") { DefaultSslMode = "Require", DefaultUser = "root" },
        new("TimescaleDB", "Ts", "#E0A21B", 50, 5432, DatabaseKind.PostgreSql, "Estensione serie temporali su PostgreSQL", "Serie temporali", "SQL") { DefaultUser = "postgres" },
        new("Db2", "D2", "#2A5DB0", 45, 50000, null, "IBM Db2 for LUW", "SQL"),
        new("Firebird", "Fb", "#D35400", 35, 3050, null, "Firebird 3 / 4", "SQL", "Embedded"),
        new("ClickHouse", "Ch", "#C9A227", 60, 8123, null, "Database colonnare OLAP", "Analitici"),
        new("DuckDB", "Dk", "#B8932E", 58, 0, null, "Motore analitico in-process", "Analitici", "Embedded", "File"),
        new("Snowflake", "Sf", "#3D9BD6", 57, 443, null, "Data warehouse in cloud", "Analitici", "Cloud"),
        new("BigQuery", "Bq", "#4A78C2", 52, 443, null, "Google BigQuery", "Analitici", "Cloud"),
        new("Amazon Redshift", "Rs", "#7A4FA3", 48, 5439, null, "Data warehouse AWS", "Analitici", "Cloud"),
        new("MongoDB", "Mg", "#3F8F4F", 75, 27017, null, "Database documentale", Popular, "NoSQL"),
        new("Redis", "Rd", "#B8322A", 65, 6379, null, "Archivio chiave-valore in memoria", "NoSQL"),
        new("Cassandra", "Cs", "#3C7BA8", 40, 9042, null, "Wide-column distribuito", "NoSQL"),
        new("InfluxDB", "If", "#7B4FC8", 42, 8086, null, "Database per serie temporali", "Serie temporali", "NoSQL"),
        new("Elasticsearch", "Es", "#2D9C96", 50, 9200, null, "Motore di ricerca e analisi", "Ricerca full-text", "NoSQL"),
        new("Neo4j", "N4", "#2A7AB0", 44, 7687, null, "Database a grafo", "Grafi", "NoSQL"),
        new("H2", "H2", "#3466A8", 30, 0, null, "Database Java embedded", "Embedded", "SQL"),
        new("File CSV", "Csv", "#5E8C3A", 38, 0, null, "Cartella di file CSV come tabelle", "File"),
        new("Excel", "Xl", "#2F7D4A", 36, 0, null, "Fogli .xlsx come tabelle", "File"),
    ];
}
