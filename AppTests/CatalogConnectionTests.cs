using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Navifox.App.Services;
using Navifox.Core;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.AppTests;

public sealed class CatalogConnectionTests
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("database_app")]
    public async Task PostgreSqlApreIlCatalogoSulDatabaseConfigurato(string database)
    {
        var (service, probe) = Create(DatabaseKind.PostgreSql);
        var profile = new ConnectionProfile { Kind = DatabaseKind.PostgreSql, Name = "Supabase", Environment = "Produzione", DefaultDatabase = database, ReadOnly = true };
        Assert.Equal(["postgres", "database_app"], await service.ListDatabasesAsync(profile, CancellationToken.None));
        Assert.Equal(profile, Assert.Single(probe.OpenedProfiles));
        Assert.Equal(ConnectionState.Closed, Assert.Single(probe.Connections).State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PostgreSqlSenzaDatabaseConfiguratoUsaQuelloDiManutenzione(string database)
    {
        var (service, probe) = Create(DatabaseKind.PostgreSql);
        var profile = new ConnectionProfile { Kind = DatabaseKind.PostgreSql, DefaultDatabase = database };
        await service.ListDatabasesAsync(profile, CancellationToken.None);
        Assert.Equal("postgres", Assert.Single(probe.OpenedProfiles).DefaultDatabase);
        Assert.Equal(database, profile.DefaultDatabase);
    }

    [Fact]
    public async Task AncheCodificheEOpzioniPostgreSqlUsanoUnDatabaseValido()
    {
        var (service, probe) = Create(DatabaseKind.PostgreSql);
        var profile = new ConnectionProfile { Kind = DatabaseKind.PostgreSql, DefaultDatabase = "database_app" };
        await service.ListCharsetsAsync(profile, CancellationToken.None);
        await service.GetDatabaseOptionsAsync(profile, "altro_database", CancellationToken.None);
        Assert.Equal(["database_app", "database_app"], probe.OpenedProfiles.Select(p => p.DefaultDatabase));
    }

    [Theory]
    [InlineData(DatabaseKind.MySql)]
    [InlineData(DatabaseKind.SqlServer)]
    [InlineData(DatabaseKind.Sqlite)]
    public async Task GliAltriProviderMantengonoLaConnessioneAlCatalogoSenzaDatabase(DatabaseKind kind)
    {
        var (service, probe) = Create(kind);
        var profile = new ConnectionProfile { Kind = kind, DefaultDatabase = "database_app" };
        await service.ListDatabasesAsync(profile, CancellationToken.None);
        Assert.Equal("", Assert.Single(probe.OpenedProfiles).DefaultDatabase);
    }

    private static (DatabaseAccessService Service, CatalogProbe Probe) Create(DatabaseKind kind)
    {
        var provider = DispatchProxy.Create<IDialectProvider, CatalogProbe>();
        var probe = (CatalogProbe)provider;
        probe.Kind = kind;
        return (new DatabaseAccessService([provider]), probe);
    }

    public class CatalogProbe : DispatchProxy
    {
        public DatabaseKind Kind { get; set; }
        public List<ConnectionProfile> OpenedProfiles { get; } = [];
        public List<DbConnection> Connections { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Kind" => Kind,
            "get_MaintenanceDatabase" => "postgres",
            "ConnectAsync" => OpenAsync((ConnectionProfile)args![0]!, (CancellationToken)args[1]!),
            "ListDatabasesAsync" => Task.FromResult<IReadOnlyList<string>>(["postgres", "database_app"]),
            "ListCharsetsAsync" => Task.FromResult<IReadOnlyList<CharsetInfo>>([]),
            "GetDatabaseOptionsAsync" => Task.FromResult(new DatabaseOptions((string)args![1]!)),
            _ => throw new NotSupportedException(method?.Name)
        };

        private async Task<DbConnection> OpenAsync(ConnectionProfile profile, CancellationToken token)
        {
            OpenedProfiles.Add(profile);
            // Riproduce il rifiuto del pooler quando manca il database nella richiesta iniziale.
            if (Kind == DatabaseKind.PostgreSql && string.IsNullOrWhiteSpace(profile.DefaultDatabase))
                throw new InvalidOperationException("Il pooler richiede un database valido.");
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(token);
            Connections.Add(connection);
            return connection;
        }
    }
}
