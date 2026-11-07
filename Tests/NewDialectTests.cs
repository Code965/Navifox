using Navifox.Core;
using Navifox.Core.Models;
using Navifox.Dialects;
using Xunit;

namespace Navifox.Tests;

/// <summary>SQL Server (generazione SQL) e SQLite (file temporaneo reale: non serve un server).</summary>
public sealed class NewDialectTests
{
    private static TableDesign Clienti(string intType, string textType, int? length) => new(null, "clienti",
        [
            new DesignColumn(null, "id", intType, NotNull: true, KeyOrder: 1, AutoIncrement: true),
            new DesignColumn(null, "nome", textType, length, NotNull: true),
        ],
        [new DesignIndex(null, "ix_clienti_nome", ["nome"])], [], [], new TableDesignOptions(), null);

    [Fact]
    public void SqlServer_CreateTable_UsaIdentityEIndici()
    {
        var sql = TableDesignSql.Build(DatabaseKind.SqlServer, "dbo", null, Clienti("int", "nvarchar", 80));
        Assert.Contains("[id] int IDENTITY(1,1) NOT NULL", sql[0]);
        Assert.Contains("CONSTRAINT [PK_clienti] PRIMARY KEY ([id])", sql[0]);
        Assert.Equal("CREATE INDEX [ix_clienti_nome] ON [dbo].[clienti] ([nome])", sql[1]);
    }

    [Fact]
    public void SqlServer_RinominaColonna_UsaSpRename()
    {
        var original = Clienti("int", "nvarchar", 80) with { OriginalName = "clienti" };
        original = original with { Columns = original.Columns.Select(c => c with { OriginalName = c.Name }).ToArray(), Indexes = [] };
        var target = original with { Columns = [original.Columns[0], original.Columns[1] with { Name = "ragione_sociale" }] };
        var sql = TableDesignSql.Build(DatabaseKind.SqlServer, "dbo", original, target);
        Assert.Equal(["EXEC sp_rename N'dbo.clienti.nome', N'ragione_sociale', N'COLUMN'"], sql);
    }

    [Fact]
    public void SqlServer_IdentitySuColonnaEsistente_NonSupportato()
    {
        var original = new TableDesign("t", "t", [new DesignColumn("id", "id", "int", NotNull: true)], [], [], [], new TableDesignOptions(), null);
        var target = original with { Columns = [original.Columns[0] with { AutoIncrement = true }] };
        Assert.Throws<InvalidOperationException>(() => TableDesignSql.Build(DatabaseKind.SqlServer, "dbo", original, target));
    }

    [Fact]
    public void SqlServer_SplitGo_TieneInteroIlCorpoDellaProcedura()
    {
        var parts = SqlScriptSplitter.Split("CREATE TABLE a(x int);\nGO\nCREATE PROCEDURE p AS BEGIN SELECT 1; SELECT 2; END\ngo\nINSERT INTO a VALUES (1); INSERT INTO a VALUES (2)", DatabaseKind.SqlServer).ToArray();
        Assert.Equal(4, parts.Length);
        Assert.StartsWith("CREATE PROCEDURE", parts[1]);
        Assert.Contains("SELECT 2", parts[1]);
    }

    [Fact]
    public void Sqlite_SplitTrigger_TieneInteroBeginEnd()
    {
        var parts = SqlScriptSplitter.Split("CREATE TABLE t(x);\nCREATE TRIGGER tr AFTER INSERT ON t BEGIN UPDATE t SET x = 1; UPDATE t SET x = 2; END;\nSELECT 1;", DatabaseKind.Sqlite).ToArray();
        Assert.Equal(3, parts.Length);
        Assert.EndsWith("END;", parts[1]);
    }

    [Fact]
    public void LimitSelect_PerDialetto()
    {
        Assert.Equal("SELECT TOP (5) * FROM t", DatabaseKind.SqlServer.LimitSelect("*", "FROM t", 5));
        Assert.Equal("SELECT * FROM t LIMIT 5", DatabaseKind.Sqlite.LimitSelect("*", "FROM t", 5));
        Assert.Equal("BEGIN TRANSACTION", DatabaseKind.SqlServer.BeginTransactionSql());
    }

    [Fact]
    public async Task Sqlite_ProgettaTabella_RicostruzioneConservaDatiEChiavi()
    {
        var provider = new SqliteDialectProvider();
        var file = Path.Combine(Path.GetTempPath(), $"navifox_test_{Guid.NewGuid():N}.db");
        try
        {
            await using var connection = await provider.ConnectAsync(new ConnectionProfile { Kind = DatabaseKind.Sqlite, Host = file }, CancellationToken.None);
            async Task Run(IEnumerable<string> statements)
            {
                foreach (var statement in statements) await provider.ExecuteAsync(connection, statement, 0, CancellationToken.None);
            }
            await Run(TableDesignSql.Build(DatabaseKind.Sqlite, "main", null, Clienti("INTEGER", "TEXT", null)));
            await Run(provider.SplitScript("INSERT INTO clienti (nome) VALUES ('Anna'); INSERT INTO clienti (nome) VALUES ('Bruno'); CREATE TABLE ordini (id INTEGER PRIMARY KEY, cliente_id INTEGER REFERENCES clienti(id))"));

            var design = await provider.GetTableDesignAsync(connection, "main", "main", "clienti", CancellationToken.None);
            Assert.Empty(TableDesignSql.Build(DatabaseKind.Sqlite, "main", design, design));
            Assert.True(design.Columns[0].AutoIncrement);

            // Nuova colonna NOT NULL con default + cambio di tipo: richiede la ricostruzione.
            var target = design with
            {
                Columns = [design.Columns[0], design.Columns[1] with { Type = "VARCHAR", Length = 100 }, new DesignColumn(null, "attivo", "INTEGER", NotNull: true, Default: "1")],
            };
            var steps = TableDesignSql.Build(DatabaseKind.Sqlite, "main", design, target);
            Assert.Contains(steps, s => s.StartsWith("CREATE TABLE \"_navifox_new_clienti\""));
            await provider.ExecuteAsync(connection, "BEGIN", 0, CancellationToken.None);
            await Run(steps);
            await provider.ExecuteAsync(connection, "COMMIT", 0, CancellationToken.None);

            var after = await provider.GetTableDesignAsync(connection, "main", "main", "clienti", CancellationToken.None);
            Assert.Equal(["id", "nome", "attivo"], after.Columns.Select(c => c.Name));
            Assert.Equal("VARCHAR", after.Columns[1].Type);
            Assert.Contains(after.Indexes, i => i.Name == "ix_clienti_nome");
            Assert.Equal(2, await provider.CountRowsAsync(connection, "main", "clienti", null, CancellationToken.None));
            Assert.Single((await provider.GetTableDesignAsync(connection, "main", "main", "ordini", CancellationToken.None)).ForeignKeys);
        }
        finally
        {
            provider.ClearPools();
            File.Delete(file);
        }
    }
}
