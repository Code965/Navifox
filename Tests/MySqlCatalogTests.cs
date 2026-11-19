using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using MySqlConnector;
using Navifox.Dialects;
using Xunit;

namespace Navifox.Tests;

public sealed class MySqlCatalogTests
{
    [Fact]
    public async Task UnCatalogoGrandeNonLeggeLeStatisticheDiTutteLeTabelle()
    {
        using var connection = new CatalogConnection(26_493);
        var objects = await new MySqlDialectProvider().ListTablesAsync(connection, "db", "geo`poi", CancellationToken.None);
        Assert.Equal(26_493, objects.Count);
        Assert.Equal("SHOW FULL TABLES FROM `geo``poi`", Assert.Single(connection.Commands));
        Assert.Equal("BASE TABLE", objects[0].Kind);
        Assert.Equal("VIEW", objects[^1].Kind);
        Assert.All(objects, item => { Assert.Null(item.EstimatedRows); Assert.Null(item.DataBytes); });
    }

    [Fact]
    public async Task LeProprietaDiUnCatalogoGrandeContanoAncheLeVisteSenzaAggregareLeStatistiche()
    {
        using var connection = new CatalogConnection(1001);
        var properties = (await new MySqlDialectProvider().GetDatabasePropertiesAsync(connection, "db", CancellationToken.None)).ToDictionary();
        Assert.Equal("utf8mb4", properties["Set di caratteri"]);
        Assert.Equal("1", properties["Viste"]);
        Assert.Equal(1000.ToString("N0"), properties["Tabelle"]);
        Assert.Equal("—", properties["Righe (stima)"]);
        Assert.Equal("—", properties["Dimensione dati"]);
        Assert.Equal("—", properties["Dimensione indici"]);
        Assert.DoesNotContain(connection.Commands, sql => sql.Contains("INFORMATION_SCHEMA.TABLES"));
    }

    [Fact]
    public async Task IMetadatiCompletiRestanoDisponibiliNeiCataloghiPiccoli()
    {
        using var connection = new CatalogConnection(1000);
        var provider = new MySqlDialectProvider();
        var table = Assert.Single(await provider.ListTablesAsync(connection, "db", "db", CancellationToken.None));
        Assert.Equal(42, table.EstimatedRows);
        Assert.Equal(4096, table.DataBytes);
        Assert.Equal("InnoDB", table.Engine);
        Assert.Equal("Commento", table.Comment);
        Assert.Equal(43, table.AutoIncrement);
        var properties = (await provider.GetDatabasePropertiesAsync(connection, "db", CancellationToken.None)).ToDictionary();
        Assert.Equal("42", properties["Righe (stima)"]);
        Assert.NotEqual("—", properties["Dimensione dati"]);
        Assert.Contains(connection.Commands, sql => sql.Contains("SUM(TABLE_TYPE"));
    }

    [Fact]
    public async Task IlRefreshRileggeLElencoSenzaRiutilizzareUnCatalogoObsoleto()
    {
        using var connection = new CatalogConnection(1001);
        var provider = new MySqlDialectProvider();
        Assert.Equal(1001, (await provider.ListTablesAsync(connection, "db", "db", CancellationToken.None)).Count);
        connection.TableCount++;
        Assert.Equal(1002, (await provider.ListTablesAsync(connection, "db", "db", CancellationToken.None)).Count);
        Assert.Equal(2, connection.Commands.Count);
    }

    [Fact]
    public async Task UnCatalogoAnnullatoNonAvviaLaLetturaDelleStatistiche()
    {
        using var connection = new CatalogConnection(1000);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MySqlDialectProvider().ListTablesAsync(connection, "db", "db", source.Token));
        Assert.Empty(connection.Commands);
    }

    private sealed class CatalogConnection(int tableCount) : DbConnection
    {
        public int TableCount { get; set; } = tableCount;
        public List<string> Commands { get; } = [];
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "db";
        public override string DataSource => "test";
        public override string ServerVersion => "5.7";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new CatalogCommand(this);

        public DbDataReader Read(string sql)
        {
            Commands.Add(sql);
            var data = new DataTable();
            if (sql.StartsWith("SHOW FULL TABLES", StringComparison.Ordinal))
            {
                data.Columns.Add("Name", typeof(string)); data.Columns.Add("Type", typeof(string));
                for (var i = 0; i < TableCount; i++) data.Rows.Add($"t{i:D5}", i == TableCount - 1 ? "VIEW" : "BASE TABLE");
            }
            else if (sql.Contains("INFORMATION_SCHEMA.SCHEMATA"))
            {
                data.Columns.Add("Charset", typeof(string)); data.Columns.Add("Collation", typeof(string));
                data.Rows.Add("utf8mb4", "utf8mb4_general_ci");
            }
            else if (sql.Contains("SUM(TABLE_TYPE"))
            {
                Assert.True(TableCount <= 1000, "Le statistiche globali non devono bloccare i cataloghi grandi.");
                for (var i = 0; i < 5; i++) data.Columns.Add($"c{i}", typeof(long));
                data.Rows.Add(1L, 0L, 4096L, 1024L, 42L);
            }
            else if (sql.Contains("INFORMATION_SCHEMA.TABLES"))
            {
                Assert.True(TableCount <= 1000, "Il catalogo grande deve usare soltanto l'elenco leggero.");
                foreach (var type in new[] { typeof(string), typeof(string), typeof(long), typeof(long), typeof(string), typeof(DateTime), typeof(string), typeof(long) })
                    data.Columns.Add($"c{data.Columns.Count}", type);
                data.Rows.Add("t", "BASE TABLE", 42L, 4096L, "InnoDB", new DateTime(2026, 11, 19), "Commento", 43L);
            }
            else throw new NotSupportedException(sql);
            return data.CreateDataReader();
        }
    }

    private sealed class CatalogCommand(CatalogConnection connection) : DbCommand
    {
        private readonly MySqlCommand _parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new MySqlParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => connection.Read(CommandText);
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(connection.Read(CommandText));
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _parameters.Dispose();
            base.Dispose(disposing);
        }
    }
}
