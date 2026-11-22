using Navifox.App.Services;
using Navifox.App.ViewModels;
using Navifox.Core.Models;
using Navifox.Dialects;
using Xunit;

namespace Navifox.AppTests;

public sealed partial class WorkflowTests
{
    /// <summary>Opt-in: solo PostgreSQL locale, un database nuovo con nome casuale, rimosso nel finally.</summary>
    [Theory]
    [InlineData("public", "portfolio")]
    [InlineData("portfolio Dati", "archivio\"2026")]
    [InlineData("dati.originali", "archivio d'oggi")]
    public async Task TrasferimentoPostgreSqlFraSchemiInDatabaseTemporaneo(string sourceSchema, string targetSchema)
    {
        if (Environment.GetEnvironmentVariable("NAVIFOX_PG_IT") != "1") return;
        // La fixture usa una cartella isolata: si leggono i profili locali cifrati senza modificare l'originale.
        var saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Navifox", "connections.json");
        if (File.Exists(saved)) File.Copy(saved, Path.Combine(fixture.Root, "connections.json"), overwrite: true);
        var profile = (await new ConnectionProfileStore().LoadAsync()).FirstOrDefault(p => p.Kind == DatabaseKind.PostgreSql && p.Host is "localhost" or "127.0.0.1" && !p.ReadOnly);
        Assert.NotNull(profile);
        var provider = new PostgreSqlDialectProvider();
        var access = new DatabaseAccessService([provider]);
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = budget.Token;
        var database = "navifox_schema_it_" + Guid.NewGuid().ToString("N");
        await using var admin = await provider.ConnectAsync(profile with { DefaultDatabase = provider.MaintenanceDatabase }, token);
        var created = false;
        try
        {
            await provider.ExecuteAsync(admin, provider.BuildCreateDatabaseSql(new(database)), 0, token);
            created = true;
            var test = profile with { Id = Guid.NewGuid(), Name = "Test schemi temporaneo", DefaultDatabase = database, Environment = "DEV" };
            var from = provider.QuoteIdentifier(sourceSchema);
            var to = provider.QuoteIdentifier(targetSchema);
            await using (var connection = await provider.ConnectAsync(test, token))
            {
                foreach (var sql in provider.SplitScript($"""
                    CREATE SCHEMA IF NOT EXISTS {from};
                    CREATE SCHEMA {to};
                    CREATE TYPE {from}.mood AS ENUM ('ok', 'no');
                    CREATE TABLE {from}.parent(id SERIAL PRIMARY KEY, note TEXT DEFAULT 'public.keep', state {from}.mood DEFAULT 'ok');
                    CREATE INDEX "idx ON distract" ON {from}.parent(lower(note)) WHERE note <> 'public.note';
                    CREATE TABLE {from}.child(id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, parent_id INTEGER REFERENCES {from}.parent(id), label TEXT, computed TEXT GENERATED ALWAYS AS (upper(label)) STORED);
                    INSERT INTO {from}.parent(id,note) VALUES (20,'public.test');
                    INSERT INTO {from}.child(id,parent_id,label) OVERRIDING SYSTEM VALUE VALUES (30,20,'ciao');
                    """)) await provider.ExecuteAsync(connection, sql, 0, token);
                var before = (await provider.ExecuteAsync(connection, "SHOW search_path", 1, token)).Rows[0][0];
                var ddl = await provider.GetTransferDdlAsync(connection, database, sourceSchema, targetSchema, "parent", token);
                Assert.Contains($"CREATE TABLE {to}.\"parent\"", ddl.Create);
                Assert.Contains("public.keep", ddl.Create);
                Assert.Equal(before, (await provider.ExecuteAsync(connection, "SHOW search_path", 1, token)).Rows[0][0]);
            }
            var transfer = new DataTransferViewModel(access, [test], test, database, target: test, targetDatabase: database, sourceSchema: sourceSchema, targetSchema: targetSchema);
            await WaitFor(() => !transfer.IsSourceLoading && !transfer.IsTargetLoading);
            Assert.Equal(2, transfer.Tables.Count);
            await transfer.StartCommand.ExecuteAsync(null);
            Assert.True(transfer.StatusMessage.StartsWith("Completato:"), transfer.StatusMessage + "\n" + transfer.LogText);
            await using (var connection = await provider.ConnectAsync(test, token))
            {
                var copied = (await provider.ExecuteAsync(connection, $"SELECT id,parent_id,label,computed FROM {to}.child", 5, token)).Rows;
                Assert.Equal(new object?[] { 30, 20, "ciao", "CIAO" }, Assert.Single(copied));
                Assert.Equal(1, await provider.CountRowsAsync(connection, sourceSchema, "parent", null, token));
                Assert.Equal(1, await provider.CountRowsAsync(connection, sourceSchema, "child", null, token));
                var parent = (await provider.ExecuteAsync(connection, $"INSERT INTO {to}.parent(note) VALUES ('nuovo') RETURNING id", 1, token)).Rows[0][0];
                Assert.Equal(21, parent);
                Assert.Equal(31, (await provider.ExecuteAsync(connection, $"INSERT INTO {to}.child(parent_id,label) VALUES (21,'dopo') RETURNING id", 1, token)).Rows[0][0]);
                var keySchema = (await provider.ExecuteAsync(connection, $"SELECT n.nspname FROM pg_constraint c JOIN pg_class t ON t.oid=c.confrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE c.conrelid={provider.FormatLiteral(to + ".child")}::regclass AND c.contype='f'", 1, token)).Rows[0][0];
                Assert.Equal(targetSchema, keySchema);
                // La sequenza SERIAL della sorgente non viene usata dalla copia.
                Assert.Equal(1, (await provider.ExecuteAsync(connection, $"INSERT INTO {from}.parent(note) VALUES ('origine') RETURNING id", 1, token)).Rows[0][0]);
            }
        }
        finally
        {
            if (created)
            {
                Assert.StartsWith("navifox_schema_it_", database);
                await provider.ExecuteAsync(admin, $"DROP DATABASE {provider.QuoteIdentifier(database)} WITH (FORCE)", 0, CancellationToken.None);
            }
        }
    }
}
