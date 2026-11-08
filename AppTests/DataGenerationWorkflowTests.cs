using Navifox.App.Services;
using Navifox.App.ViewModels;
using Navifox.Core.DataGen;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.AppTests;

public sealed partial class WorkflowTests
{
    private async Task<IReadOnlyList<IReadOnlyList<object?>>> ReadData(ConnectionProfile profile, string sql)
    {
        await using var connection = await fixture.Provider.ConnectAsync(profile, CancellationToken.None);
        return (await fixture.Provider.ExecuteAsync(connection, sql, 10000, CancellationToken.None)).Rows;
    }

    [Fact]
    public async Task GeneratoreAnteprimaNonScriveEInserimentoRispettaAutomaticiEDefault()
    {
        var profile = fixture.Profile("Dati di prova");
        await Execute(profile, "CREATE TABLE clienti(id INTEGER PRIMARY KEY, nome VARCHAR(30) NOT NULL, email VARCHAR(40) NOT NULL UNIQUE, tag TEXT NOT NULL DEFAULT 'predefinito', upper_name TEXT GENERATED ALWAYS AS (upper(nome)) STORED)",
            "INSERT INTO clienti(id,nome,email) VALUES(40,'Esistente','esistente@example.test')");
        var dialogs = new TestDialogs();
        var model = new DataGeneratorViewModel(fixture.Access, dialogs, profile, "main", "main", "clienti") { RowCount = 405, RandomNulls = false };
        await model.InitializeAsync();
        Assert.True(model.CanRun, model.StatusMessage);
        Assert.True(model.Columns.Single(c => c.Name == "id").IsAutomatic);
        Assert.True(model.Columns.Single(c => c.Name == "upper_name").IsAutomatic);
        var tag = model.Columns.Single(c => c.Name == "tag");
        tag.SelectedKind = GeneratorColumnChoice.Options.Single(o => o.Kind == FakeKind.Skip);
        await model.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(10, model.PreviewRows.Count);
        Assert.Equal(new[] { "nome", "email" }, model.PreviewColumns.Select(c => c.Name));
        Assert.Single(await ReadData(profile, "SELECT id FROM clienti"));
        var firstSql = model.SqlPreview;
        await model.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(firstSql, model.SqlPreview);
        await model.GenerateCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Errors);
        Assert.Equal(405, model.GeneratedRows);
        var rows = await ReadData(profile, "SELECT id,nome,email,tag,upper_name FROM clienti ORDER BY id");
        Assert.Equal(406, rows.Count);
        Assert.Equal("Esistente", rows[0][1]);
        Assert.Equal(41L, rows[1][0]);
        Assert.Equal(406, rows.Select(r => r[2]).Distinct().Count());
        Assert.All(rows, r => { Assert.Equal("predefinito", r[3]); Assert.Equal(((string)r[1]!).ToUpperInvariant(), r[4]); });
        var activity = Assert.Single(fixture.Journal.Read(limit: -1), e => e.Kind == "Generazione dati" && e.ProfileId == profile.Id && e.Sql.Length == 0);
        Assert.Equal("Succeeded", activity.Status);
        var statements = fixture.Journal.Read(operationId: activity.OperationId, limit: -1);
        Assert.Equal(3, statements.Count(e => e.Sql.StartsWith("INSERT INTO")));
        Assert.Contains(statements, e => e.Sql == "COMMIT");
    }

    [Fact]
    public async Task GeneratoreSequenzeEChiaviEsterneCompositeUsanoValoriEsistenti()
    {
        var profile = fixture.Profile("Relazioni generate");
        await Execute(profile, "CREATE TABLE parent(a INTEGER, b INTEGER, PRIMARY KEY(a,b)) WITHOUT ROWID",
            "INSERT INTO parent VALUES(1,11),(2,22),(3,33)",
            "CREATE TABLE child(id INTEGER PRIMARY KEY, a INTEGER NOT NULL, b INTEGER NOT NULL, FOREIGN KEY(a,b) REFERENCES parent(a,b)) WITHOUT ROWID",
            "INSERT INTO child VALUES(80,1,11)");
        var service = new DataGenerationService(fixture.Access);
        var columns = await service.PrepareAsync(profile, "main", "main", "child", CancellationToken.None);
        Assert.Equal(81, columns.Single(c => c.Column.Name == "id").SequenceStart);
        Assert.All(columns.Where(c => c.Column.Name != "id"), c => Assert.Equal(FakeKind.ForeignKey, c.Kind));
        await service.InsertAsync(profile, "main", "main", "child", columns, 230, 7, false, null, CancellationToken.None);
        var rows = await ReadData(profile, "SELECT id,a,b FROM child ORDER BY id");
        Assert.Equal(231, rows.Count);
        Assert.Equal(310L, rows[^1][0]);
        Assert.All(rows, r => Assert.Equal((long)r[1]! * 11, r[2]));
        Assert.Empty(await ReadData(profile, "PRAGMA foreign_key_check"));
        await service.RefreshAsync(profile, "main", "main", "child", columns, CancellationToken.None);
        Assert.Equal(311, columns.Single(c => c.Column.Name == "id").SequenceStart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratoreAnnullaAncheIBatchGiaScrittiSuErroreOInterruzione(bool cancel)
    {
        var profile = fixture.Profile("Rollback generazione");
        await Execute(profile, cancel ? "CREATE TABLE t(id INTEGER PRIMARY KEY) WITHOUT ROWID" : "CREATE TABLE t(id INTEGER PRIMARY KEY CHECK(id <= 250)) WITHOUT ROWID");
        var service = new DataGenerationService(fixture.Access);
        var columns = await service.PrepareAsync(profile, "main", "main", "t", CancellationToken.None);
        using var token = new CancellationTokenSource();
        var progress = new ImmediateProgress(done => { if (cancel && done >= 200) token.Cancel(); });
        await Assert.ThrowsAnyAsync<Exception>(() => service.InsertAsync(profile, "main", "main", "t", columns, 500, 42, false, progress, token.Token));
        Assert.Empty(await ReadData(profile, "SELECT * FROM t"));
        var activity = Assert.Single(fixture.Journal.Read(limit: -1), e => e.Kind == "Generazione dati" && e.ProfileId == profile.Id && e.Sql.Length == 0);
        Assert.NotEqual("Succeeded", activity.Status);
        Assert.Contains(fixture.Journal.Read(operationId: activity.OperationId, limit: -1), e => e.Sql == "ROLLBACK" && e.Status == "Succeeded");
    }

    [Fact]
    public async Task GeneratoreRispettaSolaLetturaERifiutoProduzioneEInserisceRigheSoloDefault()
    {
        var profile = fixture.Profile("Protezioni generatore");
        await Execute(profile, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT NOT NULL DEFAULT 'ok')");
        var service = new DataGenerationService(fixture.Access);
        var columns = await service.PrepareAsync(profile, "main", "main", "t", CancellationToken.None);
        columns.Single(c => c.Column.Name == "value").Kind = FakeKind.Skip;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InsertAsync(profile with { ReadOnly = true }, "main", "main", "t", columns, 2, 1, false, null, CancellationToken.None));
        var dialogs = new TestDialogs { Confirm = false };
        var model = new DataGeneratorViewModel(fixture.Access, dialogs, profile with { Environment = "Produzione" }, "main", "main", "t");
        await model.InitializeAsync();
        await model.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.ConfirmCount);
        Assert.Empty(await ReadData(profile, "SELECT * FROM t"));
        Assert.Equal(3, await service.InsertAsync(profile, "main", "main", "t", columns, 3, 1, false, null, CancellationToken.None));
        Assert.All(await ReadData(profile, "SELECT value FROM t"), r => Assert.Equal("ok", r[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrasferimentoMascheraCopiaEFilePreservandoOrigineRelazioniEColonneCalcolate(bool file)
    {
        var source = fixture.Profile("Origine personale");
        var target = fixture.Profile("Copia mascherata");
        await Execute(source,
            "CREATE TABLE parent(id INTEGER PRIMARY KEY, email VARCHAR(50) NOT NULL UNIQUE, nome VARCHAR(20) NOT NULL)",
            "INSERT INTO parent VALUES(1,'chiave@azienda.it','Mario')",
            "CREATE TABLE child(id INTEGER PRIMARY KEY, email VARCHAR(50) REFERENCES parent(email), nome VARCHAR(20), telefono VARCHAR(20), computed TEXT GENERATED ALWAYS AS (upper(nome)) STORED)",
            "INSERT INTO child(id,email,nome,telefono) VALUES(2,'chiave@azienda.it','Mario',NULL)",
            "CREATE TABLE people(id INTEGER PRIMARY KEY, email VARCHAR(30) NOT NULL UNIQUE, nome VARCHAR(20))");
        await using (var connection = await fixture.Provider.ConnectAsync(source, CancellationToken.None))
        {
            var rows = Enumerable.Range(1, 503).Select(i => (IReadOnlyList<object?>)new object?[] { i, $"persona{i}@azienda.it", "Mario" }).ToArray();
            await fixture.Provider.ExecuteAsync(connection, fixture.Provider.BuildInsert("main", "people", ["id", "email", "nome"], rows), 0, CancellationToken.None);
        }
        var path = Path.Combine(fixture.Root, Guid.NewGuid() + ".sql");
        var dialogs = new TestDialogs();
        var transfer = new DataTransferViewModel(fixture.Access, [source, target], source, "main", dialogs, target, "main")
            { AnonymizeData = true, TargetIsFile = file, TargetFile = path };
        await WaitFor(() => transfer.Tables.Count == 3);
        await transfer.StartCommand.ExecuteAsync(null);
        Assert.True(transfer.StatusMessage.StartsWith("Completato:"), transfer.StatusMessage + "\n" + transfer.LogText);
        Assert.Equal(1, dialogs.ConfirmCount);
        Assert.Contains("people: email, nome", dialogs.LastConfirmation);
        if (file)
        {
            var sql = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("persona503@azienda.it", sql);
            await using var connection = await fixture.Provider.ConnectAsync(target, CancellationToken.None);
            foreach (var statement in fixture.Provider.SplitScript(sql)) await fixture.Provider.ExecuteAsync(connection, statement, 0, CancellationToken.None);
        }
        var parent = Assert.Single(await ReadData(target, "SELECT id,email,nome FROM parent"));
        var child = Assert.Single(await ReadData(target, "SELECT id,email,nome,telefono,computed FROM child"));
        Assert.Equal(1L, parent[0]);
        Assert.Equal(2L, child[0]);
        Assert.Equal("chiave@azienda.it", parent[1]);
        Assert.Equal(parent[1], child[1]);
        Assert.NotEqual("Mario", parent[2]);
        Assert.Equal(parent[2], child[2]);
        Assert.Null(child[3]);
        Assert.Equal(((string)child[2]!).ToUpperInvariant(), child[4]);
        var people = await ReadData(target, "SELECT email,nome FROM people");
        Assert.Equal(503, people.Count);
        Assert.Equal(503, people.Select(r => r[0]).Distinct().Count());
        Assert.All(people, r => { Assert.EndsWith("@example.test", (string)r[0]!); Assert.InRange(((string)r[0]!).Length, 1, 30); });
        Assert.Equal("persona1@azienda.it", (await ReadData(source, "SELECT email FROM people WHERE id=1"))[0][0]);
        Assert.Equal("Mario", (await ReadData(source, "SELECT nome FROM parent"))[0][0]);
        Assert.Empty(await ReadData(target, "PRAGMA foreign_key_check"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RifiutoMascheramentoNonSovrascriveDestinazioneOFile(bool file)
    {
        var source = fixture.Profile("Rifiuto origine");
        var target = fixture.Profile("Rifiuto destinazione");
        await Execute(source, "CREATE TABLE t(id INTEGER PRIMARY KEY,nome TEXT)", "INSERT INTO t VALUES(1,'Mario')");
        await Execute(target, "CREATE TABLE t(id INTEGER PRIMARY KEY,nome TEXT)", "INSERT INTO t VALUES(99,'Da conservare')");
        var path = Path.Combine(fixture.Root, Guid.NewGuid() + ".sql");
        await File.WriteAllTextAsync(path, "Da conservare");
        var transfer = new DataTransferViewModel(fixture.Access, [source, target], source, "main", new TestDialogs { Confirm = false }, target, "main")
            { AnonymizeData = true, TargetIsFile = file, TargetFile = path };
        await WaitFor(() => transfer.Tables.Count == 1);
        await transfer.StartCommand.ExecuteAsync(null);
        Assert.Contains("annullat", transfer.StatusMessage);
        Assert.Equal("Da conservare", (await ReadData(target, "SELECT nome FROM t"))[0][0]);
        Assert.Equal("Da conservare", await File.ReadAllTextAsync(path));
    }

    private sealed class ImmediateProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
