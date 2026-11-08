using Avalonia.Media;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>
/// Voce del catalogo driver mostrata nella procedura di nuova connessione.
/// Il riquadro mostra il logo del database (Simple Icons, vedi Controls.DriverLogos) quando disponibile,
/// altrimenti una sigla colorata. I marchi appartengono ai rispettivi proprietari.
/// <see cref="Kind"/> è valorizzato solo per i motori con un dialect disponibile
/// (direttamente o tramite protocollo compatibile).
/// </summary>
public sealed class DriverInfo
{
    public DriverInfo(string name, string monogram, string color, int popularity, int port, DatabaseKind? kind, string description, params string[] categories)
    {
        Logo = LogoFor(name);
        Name = name;
        Monogram = monogram;
        BadgeBrush = new SolidColorBrush(Color.Parse(color));
        Popularity = popularity;
        DefaultPort = port;
        Kind = kind;
        Description = description;
        Categories = categories;
    }

    /// <summary>Logo del marchio (null = si usa la sigla).</summary>
    public Controls.DriverLogo? Logo { get; }
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

    private static readonly Dictionary<string, string> LogoSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MySQL"] = "mysql", ["PostgreSQL"] = "postgresql", ["MariaDB"] = "mariadb", ["SQLite"] = "sqlite", ["Supabase"] = "supabase",
        ["Neon"] = "neon", ["TiDB"] = "tidb", ["CockroachDB"] = "cockroachlabs", ["TimescaleDB"] = "timescale", ["ClickHouse"] = "clickhouse",
        ["DuckDB"] = "duckdb", ["Snowflake"] = "snowflake", ["BigQuery"] = "googlebigquery", ["MongoDB"] = "mongodb", ["Redis"] = "redis",
        ["Cassandra"] = "apachecassandra", ["InfluxDB"] = "influxdb", ["Elasticsearch"] = "elasticsearch", ["Neo4j"] = "neo4j",
        ["PlanetScale"] = "planetscale", ["SingleStore"] = "singlestore", ["Vitess"] = "vitess", ["Apache Doris"] = "apachedoris",
        ["Google Cloud SQL per MySQL"] = "googlecloud", ["Google Cloud SQL per PostgreSQL"] = "googlecloud", ["AlloyDB"] = "googlecloud",
        ["DigitalOcean MySQL"] = "digitalocean", ["DigitalOcean PostgreSQL"] = "digitalocean", ["Render PostgreSQL"] = "render",
        ["Railway PostgreSQL"] = "railway", ["Railway MySQL"] = "railway", ["Couchbase"] = "couchbase", ["CouchDB"] = "apachecouchdb",
        ["ScyllaDB"] = "scylladb", ["ArangoDB"] = "arangodb", ["Trino"] = "trino", ["Apache Druid"] = "apachedruid", ["Databricks"] = "databricks",
        ["SAP HANA"] = "sap", ["Teradata"] = "teradata", ["Turso"] = "turso", ["SurrealDB"] = "surrealdb", ["Meilisearch"] = "meilisearch",
        ["OpenSearch"] = "opensearch", ["Dgraph"] = "dgraph", ["Firebase Firestore"] = "firebase", ["Qdrant"] = "qdrant", ["Milvus"] = "milvus",
    };

    private static Controls.DriverLogo? LogoFor(string name) =>
        LogoSlugs.TryGetValue(name, out var slug) && Controls.DriverLogos.All.TryGetValue(slug, out var logo) ? logo : null;
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
        [All, Popular, "SQL", "Cloud", "NoSQL", "Analitici", "Serie temporali", "Embedded", "File", "Ricerca full-text", "Grafi", "Vettoriali"];

    public static string GlyphFor(string category) => category switch
    {
        All => "▤", Popular => "★", "SQL" => "▦", "Cloud" => "☁", "NoSQL" => "◇", "Analitici" => "▥",
        "Serie temporali" => "◷", "Embedded" => "▣", "File" => "▢", "Ricerca full-text" => "⌕", "Grafi" => "⬡", "Vettoriali" => "◈", _ => "•"
    };

    public static IReadOnlyList<DriverInfo> Drivers { get; } =
    [
        new("MySQL", "My", "#2F6F95", 100, 3306, DatabaseKind.MySql, "Server MySQL 5.7 / 8.x", Popular, "SQL") { DefaultUser = "root" },
        new("PostgreSQL", "Pg", "#3B5C8C", 99, 5432, DatabaseKind.PostgreSql, "PostgreSQL 10 e successivi", Popular, "SQL") { DefaultUser = "postgres" },
        new("MariaDB", "Ma", "#8A5A3C", 95, 3306, DatabaseKind.MySql, "MariaDB 10.x / 11.x (protocollo MySQL)", Popular, "SQL") { DefaultUser = "root" },
        new("SQL Server", "Ms", "#B23A48", 92, 1433, DatabaseKind.SqlServer, "SQL Server 2016 e successivi (utente vuoto = autenticazione di Windows)", Popular, "SQL"),
        new("SQLite", "Lt", "#4F7C8A", 90, 0, DatabaseKind.Sqlite, "File di database locale (.db, .sqlite)", Popular, "SQL", "Embedded", "File"),
        new("Azure SQL Database", "Az", "#2F7FC1", 78, 1433, DatabaseKind.SqlServer, "Database SQL gestito di Microsoft Azure", "Cloud", "SQL") { DefaultSslMode = "Require" },
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
        // ── Compatibili con i driver già presenti (protocollo MySQL, PostgreSQL o SQL Server) ──
        new("Percona Server", "Pc", "#B5283A", 47, 3306, DatabaseKind.MySql, "Percona Server for MySQL", "SQL") { DefaultUser = "root" },
        new("PlanetScale", "Ps", "#222222", 49, 3306, DatabaseKind.MySql, "MySQL serverless su Vitess", "Cloud", "SQL") { DefaultSslMode = "Require" },
        new("SingleStore", "S2", "#AA00FF", 41, 3306, DatabaseKind.MySql, "SQL distribuito (protocollo MySQL)", "SQL", "Analitici", "Cloud") { DefaultSslMode = "Require" },
        new("Vitess", "Vt", "#F16728", 34, 15306, DatabaseKind.MySql, "Clustering di MySQL (vtgate)", "SQL"),
        new("Apache Doris", "Do", "#5A86C9", 33, 9030, DatabaseKind.MySql, "Database analitico (protocollo MySQL)", "Analitici") { DefaultUser = "root" },
        new("Amazon RDS per MySQL", "Rm", "#D9822B", 64, 3306, DatabaseKind.MySql, "MySQL gestito su AWS", "Cloud", "SQL") { DefaultSslMode = "Require" },
        new("Amazon RDS per PostgreSQL", "Rp", "#C46D1F", 63, 5432, DatabaseKind.PostgreSql, "PostgreSQL gestito su AWS", "Cloud", "SQL") { DefaultSslMode = "Require", DefaultUser = "postgres" },
        new("Amazon RDS per SQL Server", "Rs", "#B85C1C", 46, 1433, DatabaseKind.SqlServer, "SQL Server gestito su AWS", "Cloud", "SQL") { DefaultSslMode = "Require" },
        new("Google Cloud SQL per MySQL", "Gm", "#4285F4", 62, 3306, DatabaseKind.MySql, "MySQL gestito su Google Cloud", "Cloud", "SQL") { DefaultSslMode = "Require", DefaultUser = "root" },
        new("Google Cloud SQL per PostgreSQL", "Gp", "#4285F4", 61, 5432, DatabaseKind.PostgreSql, "PostgreSQL gestito su Google Cloud", "Cloud", "SQL") { DefaultSslMode = "Require", DefaultUser = "postgres" },
        new("AlloyDB", "Ad", "#4285F4", 43, 5432, DatabaseKind.PostgreSql, "Google AlloyDB per PostgreSQL", "Cloud", "SQL") { DefaultSslMode = "Require", DefaultUser = "postgres" },
        new("Azure Database per MySQL", "Am", "#2F7FC1", 59, 3306, DatabaseKind.MySql, "MySQL Flexible Server su Azure", "Cloud", "SQL") { DefaultSslMode = "Require" },
        new("Azure Database per PostgreSQL", "Ap", "#2F7FC1", 58, 5432, DatabaseKind.PostgreSql, "PostgreSQL Flexible Server su Azure", "Cloud", "SQL") { DefaultSslMode = "Require" },
        new("Azure SQL Managed Instance", "Mi", "#2F7FC1", 44, 1433, DatabaseKind.SqlServer, "Istanza gestita SQL Server su Azure", "Cloud", "SQL") { DefaultSslMode = "Require" },
        new("DigitalOcean MySQL", "Dm", "#0080FF", 39, 25060, DatabaseKind.MySql, "MySQL gestito da DigitalOcean", "Cloud", "SQL") { DefaultSslMode = "Require", DefaultUser = "doadmin" },
        new("DigitalOcean PostgreSQL", "Dp", "#0080FF", 39, 25060, DatabaseKind.PostgreSql, "PostgreSQL gestito da DigitalOcean", "Cloud", "SQL") { DefaultSslMode = "Require", DefaultUser = "doadmin" },
        new("YugabyteDB", "Yb", "#FF6E42", 37, 5433, DatabaseKind.PostgreSql, "SQL distribuito (protocollo PostgreSQL)", "SQL", "Cloud") { DefaultUser = "yugabyte" },
        new("Citus", "Ci", "#3D6FB6", 32, 5432, DatabaseKind.PostgreSql, "PostgreSQL distribuito", "SQL", "Analitici") { DefaultUser = "citus" },
        new("Render PostgreSQL", "Re", "#46E3B7", 31, 5432, DatabaseKind.PostgreSql, "PostgreSQL gestito da Render", "Cloud", "SQL") { DefaultSslMode = "Require" },
        new("Railway PostgreSQL", "Rw", "#0B0D0E", 30, 5432, DatabaseKind.PostgreSql, "PostgreSQL su Railway", "Cloud", "SQL") { DefaultSslMode = "Require", DefaultUser = "postgres" },
        new("Railway MySQL", "Rw", "#0B0D0E", 29, 3306, DatabaseKind.MySql, "MySQL su Railway", "Cloud", "SQL") { DefaultUser = "root" },
        // ── In arrivo ──
        new("SAP HANA", "Hn", "#0FAAFF", 40, 30015, null, "SAP HANA", "SQL", "Analitici"),
        new("Teradata", "Td", "#F37440", 28, 1025, null, "Teradata Vantage", "Analitici"),
        new("Databricks", "Db", "#FF3621", 46, 443, null, "Databricks SQL", "Analitici", "Cloud"),
        new("Trino", "Tr", "#DD00A1", 35, 8080, null, "Motore SQL federato (anche Presto)", "Analitici"),
        new("Apache Druid", "Dr", "#29F1FB", 27, 8082, null, "Analisi in tempo reale", "Analitici", "Serie temporali"),
        new("Turso", "Tu", "#4FF8D2", 30, 0, null, "SQLite distribuito (libSQL)", "Cloud", "Embedded"),
        new("Couchbase", "Cb", "#EA2328", 36, 8091, null, "Database documentale", "NoSQL"),
        new("CouchDB", "Cd", "#E42528", 30, 5984, null, "Apache CouchDB", "NoSQL"),
        new("ScyllaDB", "Sc", "#6CD5E7", 31, 9042, null, "Wide-column compatibile Cassandra", "NoSQL"),
        new("ArangoDB", "Ar", "#DDE072", 29, 8529, null, "Multi-modello (documenti e grafi)", "NoSQL", "Grafi"),
        new("SurrealDB", "Su", "#FF00A0", 28, 8000, null, "Multi-modello", "NoSQL"),
        new("Dgraph", "Dg", "#E50695", 22, 9080, null, "Database a grafo (GraphQL)", "Grafi", "NoSQL"),
        new("Amazon DynamoDB", "Dy", "#4053D6", 50, 443, null, "Chiave-valore gestito su AWS", "NoSQL", "Cloud"),
        new("Azure Cosmos DB", "Co", "#2F7FC1", 45, 443, null, "Database multi-modello su Azure", "NoSQL", "Cloud"),
        new("Firebase Firestore", "Fs", "#DD2C00", 42, 443, null, "Database documentale di Firebase", "NoSQL", "Cloud"),
        new("OpenSearch", "Os", "#005EB8", 33, 9200, null, "Ricerca e analisi (fork di Elasticsearch)", "Ricerca full-text", "NoSQL"),
        new("Meilisearch", "Me", "#FF5CAA", 27, 7700, null, "Motore di ricerca istantanea", "Ricerca full-text"),
        new("Qdrant", "Qd", "#DC244C", 26, 6333, null, "Database vettoriale", "Vettoriali", "NoSQL"),
        new("Milvus", "Mv", "#00A1EA", 25, 19530, null, "Database vettoriale", "Vettoriali", "NoSQL"),
    ];
}
