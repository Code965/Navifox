using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Navifox.Core;
using Navifox.Core.Models;
using Navifox.Dialects;
using Xunit;

namespace Navifox.Tests;

public sealed class ActivityJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "navifox_activity_" + Guid.NewGuid().ToString("N"));
    private readonly SqliteDialectProvider _provider = new();
    private string JournalPath => Path.Combine(_directory, "activities.db");
    private ConnectionProfile Profile(string file = "target.db") => new() { Name = "Locale", Kind = DatabaseKind.Sqlite, Host = Path.Combine(_directory, file), Password = "", DefaultDatabase = "main" };
    private async Task<DbConnection> Connect(ActivityJournal journal, ConnectionProfile profile)
    {
        var connection = await _provider.ConnectAsync(profile, CancellationToken.None);
        ActivityRecording.Register(connection, profile, journal);
        return connection;
    }

    [Fact]
    public async Task ConservaSqlCompletoOrdineTransazioniEValoriTrasferiti()
    {
        var journal = new ActivityJournal(JournalPath);
        var profile = Profile();
        Guid operationId;
        await using (var connection = await Connect(journal, profile))
        using (var operation = ActivityRecording.BeginOperation(profile, "main", "Trasferimento dati", "origine → destinazione", journal)!)
        {
            operationId = operation.Id;
            foreach (var sql in new[] { "CREATE TABLE t(id INTEGER, value TEXT)", "BEGIN", "INSERT INTO t VALUES(1,'O''Brien'),(2,'seconda riga')", "COMMIT" })
                await _provider.ExecuteAsync(connection, sql, 0, CancellationToken.None);
            operation.Complete();
        }
        var entries = new ActivityJournal(JournalPath).Read(operationId: operationId, limit: -1, ascending: true);
        Assert.Equal(5, entries.Count);
        Assert.All(entries, e => Assert.Equal("Succeeded", e.Status));
        Assert.Equal(entries.Select(e => e.Sequence).OrderBy(n => n), entries.Select(e => e.Sequence));
        Assert.Contains(entries, e => e.Sql.Contains("O''Brien") && e.Sql.Contains("seconda riga"));
        Assert.All(entries, e => Assert.Equal(profile.Id, e.ProfileId));
        var writer = new StringWriter();
        journal.WriteScript(writer, operationId, profile.Id, "main");
        await using var rebuilt = await _provider.ConnectAsync(Profile("rebuilt.db"), CancellationToken.None);
        foreach (var sql in _provider.SplitScript(writer.ToString())) await _provider.ExecuteAsync(rebuilt, sql, 0, CancellationToken.None);
        Assert.Equal(2, await _provider.CountRowsAsync(rebuilt, "main", "t", null, CancellationToken.None));
    }

    [Fact]
    public async Task RegistraErroriAnnullamentiEParametriDellaGriglia()
    {
        var journal = new ActivityJournal(JournalPath);
        await using var connection = await Connect(journal, Profile());
        await _provider.ExecuteAsync(connection, "CREATE TABLE t(id INTEGER)", 0, CancellationToken.None);
        await Assert.ThrowsAsync<SqliteException>(() => _provider.ExecuteAsync(connection, "INSERT INTO tabella_assente VALUES(1)", 0, CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.ExecuteAsync(connection, "SELECT 1", 5, cancelled.Token));
        await _provider.QueryPagedAsync(connection, "main", "t", 2, 10, CancellationToken.None);
        var entries = journal.Read(limit: -1);
        Assert.Contains(entries, e => e.Status == "Failed" && e.Error is not null && e.Sql.Contains("tabella_assente"));
        Assert.Contains(entries, e => e.Status == "Cancelled");
        var paged = Assert.Single(entries, e => e.Parameters.Contains("@pageSize"));
        using var parameters = JsonDocument.Parse(paged.Parameters);
        Assert.Contains(parameters.RootElement.EnumerateArray(), p => p.GetProperty("Name").GetString() == "@offset" && p.GetProperty("Value").GetInt32() == 20);
    }

    [Fact]
    public async Task RollbackRestaNelloScriptEScartaiDatiAncheNellaRicostruzione()
    {
        var journal = new ActivityJournal(JournalPath);
        var profile = Profile();
        using var operation = ActivityRecording.BeginOperation(profile, "main", "Trasferimento dati", "trasferimento annullato", journal)!;
        await using var connection = await Connect(journal, profile);
        foreach (var sql in new[] { "CREATE TABLE t(id INTEGER)", "BEGIN", "INSERT INTO t VALUES(1)", "ROLLBACK" })
            await _provider.ExecuteAsync(connection, sql, 0, CancellationToken.None);
        operation.Fail(new OperationCanceledException());
        var writer = new StringWriter(); journal.WriteScript(writer, operation.Id, profile.Id, "main");
        await using var rebuilt = await _provider.ConnectAsync(Profile("rebuilt.db"), CancellationToken.None);
        foreach (var sql in _provider.SplitScript(writer.ToString())) await _provider.ExecuteAsync(rebuilt, sql, 0, CancellationToken.None);
        Assert.Equal(0, await _provider.CountRowsAsync(rebuilt, "main", "t", null, CancellationToken.None));
        Assert.Contains(journal.Read(operationId: operation.Id), e => e.Sql.Length == 0 && e.Status == "Cancelled");
    }

    [Fact]
    public async Task SeNonPuoRegistrareNonEsegueLaModifica()
    {
        var journal = new ActivityJournal(JournalPath);
        await using var connection = await Connect(journal, Profile());
        await _provider.ExecuteAsync(connection, "CREATE TABLE t(id INTEGER)", 0, CancellationToken.None);
        File.Move(JournalPath, JournalPath + ".backup");
        Directory.CreateDirectory(JournalPath);
        await Assert.ThrowsAsync<IOException>(() => _provider.ExecuteAsync(connection, "INSERT INTO t VALUES(1)", 0, CancellationToken.None));
        using var raw = connection.CreateCommand(); raw.CommandText = "SELECT count(*) FROM t";
        Assert.Equal(0L, (long)(await raw.ExecuteScalarAsync())!);
    }

    [Fact]
    public void ImportaTuttoLoStoricoUnaVoltaESaltaSoloLeRigheDanneggiate()
    {
        var journal = new ActivityJournal(JournalPath);
        var path = Path.Combine(_directory, "history.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, 3005).Select(i => JsonSerializer.Serialize(new { Time = DateTime.Now, Connection = "Vecchia", Database = "db", Sql = $"SELECT {i}", Milliseconds = 1, Error = (string?)null })).Prepend("riga danneggiata"));
        journal.ImportLegacy(path); journal.ImportLegacy(path);
        Assert.Equal(3005, journal.Read(limit: -1).Count);
        Assert.Contains(journal.Read("SELECT 0", limit: -1), e => e.Sql == "SELECT 0");
        Assert.Contains("riga danneggiata", File.ReadAllText(path));
    }

    [Fact]
    public void RecuperaSessioniInterrotteSenzaMarcareQuelleAncoraAttive()
    {
        var journal = new ActivityJournal(JournalPath);
        var sequence = journal.Begin(Profile(), "main", Guid.NewGuid(), "SQL", "test", "SELECT 1");
        using (var raw = new SqliteConnection("Data Source=" + JournalPath))
        {
            raw.Open(); using var command = raw.CreateCommand();
            command.CommandText = "UPDATE sessions SET process_id=-1 WHERE id=$session";
            command.Parameters.AddWithValue("$session", journal.SessionId.ToString()); command.ExecuteNonQuery();
        }
        var restarted = new ActivityJournal(JournalPath);
        Assert.Equal("Uncertain", Assert.Single(restarted.Read()).Status);
        var running = restarted.Begin(Profile(), "main", Guid.NewGuid(), "SQL", "ancora attivo");
        Assert.Equal("Running", new ActivityJournal(JournalPath).Read().Single(e => e.Sequence == running).Status);
    }

    [Theory]
    [InlineData(DatabaseKind.MySql, "CREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END")]
    [InlineData(DatabaseKind.SqlServer, "CREATE PROCEDURE p AS BEGIN SELECT 1; SELECT 2; END")]
    [InlineData(DatabaseKind.PostgreSql, "CREATE FUNCTION f() RETURNS integer AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql")]
    [InlineData(DatabaseKind.Sqlite, "SELECT 1 -- commento finale")]
    public void EsportaScriptConSeparatoriCorretti(DatabaseKind kind, string sql)
    {
        var journal = new ActivityJournal(JournalPath);
        var profile = Profile() with { Kind = kind };
        var op = Guid.NewGuid();
        foreach (var text in new[] { sql, "SELECT 2" }) journal.Finish(journal.Begin(profile, "main", op, "SQL", "test", text), "Succeeded", 1);
        var writer = new StringWriter(); journal.WriteScript(writer, op, profile.Id, "main");
        Assert.Equal(2, SqlScriptSplitter.Split(writer.ToString(), kind).Count());
    }

    [Fact]
    public async Task ArchiviaAncheLoSqlEsportatoSuFileSenzaConfonderloConComandiEseguiti()
    {
        var journal = new ActivityJournal(JournalPath);
        var profile = Profile();
        using var operation = ActivityRecording.BeginOperation(profile, "main", "Esportazione dati", "esportazione", journal)!;
        var writer = new StringWriter();
        await ActivityRecording.WriteScriptAsync(writer, "INSERT INTO t VALUES ('dato completo');");
        operation.Complete();
        Assert.Contains(journal.Read(operationId: operation.Id), e => e.Status == "Exported" && e.Sql.Contains("dato completo"));
        Assert.Equal(2, journal.Read(onlyTransfers: true).Count);
    }

    [Fact]
    public async Task RaggruppaLeTransazioniDellaStessaConnessioneAncheSenzaUnoScopeEsplicito()
    {
        var journal = new ActivityJournal(JournalPath);
        await using var connection = await Connect(journal, Profile());
        foreach (var sql in new[] { "BEGIN", "SELECT 1", "ROLLBACK" }) await _provider.ExecuteAsync(connection, sql, 5, CancellationToken.None);
        Assert.Single(journal.Read().Select(e => e.OperationId).Distinct());
        Assert.Equal(3, journal.Read().Count);
    }

    [Fact]
    public async Task ScriptDatabaseIncludeSessioniDiverseSenzaRieseguireLeEsportazioniSuFile()
    {
        var firstSession = new ActivityJournal(JournalPath);
        var profile = Profile();
        firstSession.Finish(firstSession.Begin(profile, "main", Guid.NewGuid(), "SQL", "creazione", "CREATE TABLE t(id INTEGER)"), "Succeeded", 1);
        firstSession.Finish(firstSession.Begin(profile, "main", Guid.NewGuid(), "SQL", "primo inserimento", "INSERT INTO t VALUES(1)"), "Succeeded", 1);
        using (var export = ActivityRecording.BeginOperation(profile, "main", "Esportazione dati", "file di prova", firstSession)!)
        {
            await ActivityRecording.WriteScriptAsync(new StringWriter(), "DROP TABLE t; CREATE TABLE t(id INTEGER);");
            export.Complete();
        }
        var secondSession = new ActivityJournal(JournalPath);
        secondSession.Finish(secondSession.Begin(profile, "main", Guid.NewGuid(), "SQL", "secondo inserimento", "INSERT INTO t VALUES(2)"), "Succeeded", 1);
        var script = new StringWriter(); secondSession.WriteScript(script, null, profile.Id, "main");
        await using var rebuilt = await _provider.ConnectAsync(profile, CancellationToken.None);
        foreach (var sql in _provider.SplitScript(script.ToString())) await _provider.ExecuteAsync(rebuilt, sql, 0, CancellationToken.None);
        Assert.Equal(2, await _provider.CountRowsAsync(rebuilt, "main", "t", null, CancellationToken.None));
    }

    public void Dispose()
    {
        _provider.ClearPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
