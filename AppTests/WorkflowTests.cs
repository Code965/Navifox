using Avalonia.Controls;
using Navifox.App.Services;
using Navifox.App.ViewModels;
using Navifox.Core;
using Navifox.Core.Models;
using Navifox.Dialects;
using Xunit;

namespace Navifox.AppTests;

public sealed class WorkflowFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "navifox_workflows_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("NAVIFOX_DATA_DIR");
    public SqliteDialectProvider Provider { get; } = new();
    public DatabaseAccessService Access { get; }
    public ActivityJournal Journal { get; }
    public WorkflowFixture()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("NAVIFOX_DATA_DIR", Root);
        Journal = new ActivityJournal(Path.Combine(Root, "activities.db"));
        ActivityRecording.Journal = Journal;
        Access = new DatabaseAccessService([Provider]);
    }
    public ConnectionProfile Profile(string name) => new() { Name = name, Kind = DatabaseKind.Sqlite, Host = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".db"), DefaultDatabase = "main" };
    public void Dispose()
    {
        ActivityRecording.Journal = null;
        Provider.ClearPools();
        Environment.SetEnvironmentVariable("NAVIFOX_DATA_DIR", _previous);
        Directory.Delete(Root, recursive: true);
    }
}

public sealed partial class WorkflowTests(WorkflowFixture fixture) : IClassFixture<WorkflowFixture>
{
    [Fact]
    public async Task SalvaDallEditorAggiornaELaQueryRestaRiapribileDalDatabase()
    {
        var profile = fixture.Profile("Query locali");
        var dialogs = new TestDialogs { NextName = "Autori" };
        var tab = new QueryTabViewModel(fixture.Access, profile, "main", false, [profile], dialogs) { SqlText = "SELECT 1;" };
        await tab.SaveCommand.ExecuteAsync(null);
        var saved = Assert.Single(LibraryStore.QueriesFor(profile.Id, "main"));
        Assert.Equal(saved.Id, tab.SavedQueryId);
        Assert.Equal("SELECT 1;", File.ReadAllText(saved.FilePath!));
        tab.SqlText = "SELECT 2;";
        await tab.SaveCommand.ExecuteAsync(null);
        saved = Assert.Single(LibraryStore.QueriesFor(profile.Id, "main"));
        Assert.Equal("SELECT 2;", saved.Sql);
        Assert.Equal(1, dialogs.PromptCount);
        var reopened = new QueryTabViewModel(fixture.Access, profile, "main", false, [profile], dialogs) { SqlText = saved.Sql };
        reopened.AttachSavedQuery(saved);
        Assert.Contains("Autori", reopened.TabTitle);
        Assert.Equal(tab.FilePath, reopened.FilePath);
        dialogs.NextName = "Copia autori";
        await tab.SaveAsCommand.ExecuteAsync(null);
        Assert.Equal(2, LibraryStore.QueriesFor(profile.Id, "main").Count);
        Assert.NotEqual(saved.Id, tab.SavedQueryId);
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task NonSovrascriveUnaQueryOmonimaSenzaConferma()
    {
        var profile = fixture.Profile("Query omonime");
        var dialogs = new TestDialogs { NextName = "Query condivisa" };
        var first = new QueryTabViewModel(fixture.Access, profile, "main", false, [profile], dialogs) { SqlText = "SELECT 1;" };
        await first.SaveCommand.ExecuteAsync(null);
        dialogs.Confirm = false;
        var second = new QueryTabViewModel(fixture.Access, profile, "main", false, [profile], dialogs) { SqlText = "SELECT 999;" };
        await second.SaveCommand.ExecuteAsync(null);
        Assert.Null(second.SavedQueryId);
        Assert.Equal("SELECT 1;", Assert.Single(LibraryStore.QueriesFor(profile.Id, "main")).Sql);
    }

    [Fact]
    public async Task DataTransferArchiviaTuttiIBatchCompletiERicostruisceLaDestinazione()
    {
        var source = fixture.Profile("Origine trasferimento");
        var target = fixture.Profile("Destinazione trasferimento");
        await Seed(source, 1103);
        var transfer = new DataTransferViewModel(fixture.Access, [source, target], source, "main", target: target, targetDatabase: "main", preselect: ["t"]);
        await WaitFor(() => transfer.Tables.Count == 1);
        await transfer.StartCommand.ExecuteAsync(null);
        Assert.StartsWith("Completato:", transfer.StatusMessage);
        await using (var connection = await fixture.Provider.ConnectAsync(target, CancellationToken.None))
            Assert.Equal(1103, await fixture.Provider.CountRowsAsync(connection, "main", "t", null, CancellationToken.None));
        var header = Assert.Single(fixture.Journal.Read(limit: -1), e => e.Kind == "Trasferimento dati" && e.Sql.Length == 0 && e.ProfileId == target.Id);
        Assert.Equal("Succeeded", header.Status);
        var entries = fixture.Journal.Read(operationId: header.OperationId, limit: -1);
        var inserts = entries.Where(e => e.ProfileId == target.Id && e.Sql.StartsWith("INSERT INTO", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, inserts.Length);
        Assert.Contains(inserts, e => e.Sql.Contains("payload_1102"));
        Assert.All(inserts, e => Assert.DoesNotContain("…", e.Sql));
        Assert.Contains(entries, e => e.Sql == "COMMIT" && e.ProfileId == target.Id);
        var writer = new StringWriter(); fixture.Journal.WriteScript(writer, header.OperationId, target.Id, "main");
        await using var rebuilt = await fixture.Provider.ConnectAsync(fixture.Profile("Ricostruzione"), CancellationToken.None);
        foreach (var sql in fixture.Provider.SplitScript(writer.ToString())) await fixture.Provider.ExecuteAsync(rebuilt, sql, 0, CancellationToken.None);
        Assert.Equal(1103, await fixture.Provider.CountRowsAsync(rebuilt, "main", "t", null, CancellationToken.None));
    }

    [Fact]
    public async Task EsportazioneDataTransferConservaAncheIlContenutoDelFileSql()
    {
        var source = fixture.Profile("Esportazione");
        await Seed(source, 503);
        var path = Path.Combine(fixture.Root, "export.sql");
        var transfer = new DataTransferViewModel(fixture.Access, [source], source, "main") { TargetIsFile = true, TargetFile = path };
        await WaitFor(() => transfer.Tables.Count == 1);
        await transfer.StartCommand.ExecuteAsync(null);
        Assert.StartsWith("Completato:", transfer.StatusMessage);
        Assert.Contains("payload_502", await File.ReadAllTextAsync(path));
        var header = Assert.Single(fixture.Journal.Read(limit: -1), e => e.Kind == "Esportazione dati" && e.Sql.Length == 0 && e.ProfileId == source.Id);
        Assert.Equal("Succeeded", header.Status);
        Assert.Contains(fixture.Journal.Read(operationId: header.OperationId), e => e.Status == "Exported" && e.Sql.Contains("payload_502"));
        var script = new StringWriter(); fixture.Journal.WriteScript(script, header.OperationId, source.Id, "main");
        await using var rebuilt = await fixture.Provider.ConnectAsync(fixture.Profile("Ripristino esportazione"), CancellationToken.None);
        foreach (var sql in fixture.Provider.SplitScript(script.ToString())) await fixture.Provider.ExecuteAsync(rebuilt, sql, 0, CancellationToken.None);
        Assert.Equal(503, await fixture.Provider.CountRowsAsync(rebuilt, "main", "t", null, CancellationToken.None));
    }

    private async Task Seed(ConnectionProfile profile, int rows)
    {
        await using var connection = await fixture.Provider.ConnectAsync(profile, CancellationToken.None);
        await fixture.Provider.ExecuteAsync(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY,value TEXT NOT NULL)", 0, CancellationToken.None);
        var values = Enumerable.Range(0, rows).Select(i => (IReadOnlyList<object?>)new object?[] { i, "payload_" + i }).ToArray();
        await fixture.Provider.ExecuteAsync(connection, fixture.Provider.BuildInsert("main", "t", ["id", "value"], values), 0, CancellationToken.None);
    }

    private static async Task WaitFor(Func<bool> ready)
    {
        for (var i = 0; i < 100 && !ready(); i++) await Task.Delay(20);
        Assert.True(ready(), "Caricamento della lista delle tabelle non completato.");
    }

    private sealed class TestDialogs : IDialogService
    {
        public string NextName { get; set; } = "Query";
        public bool Confirm { get; set; } = true;
        public int ConfirmCount { get; private set; }
        public string LastConfirmation { get; private set; } = "";
        public int PromptCount { get; private set; }
        public List<string> Errors { get; } = [];
        public Task<string?> PromptAsync(string title, string message, string value, string confirmText = "OK", bool isPassword = false) { PromptCount++; return Task.FromResult<string?>(NextName); }
        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false) { ConfirmCount++; LastConfirmation = message; return Task.FromResult(Confirm); }
        public Task ShowMessageAsync(string title, string message, bool isError = false) { if (isError) Errors.Add(message); return Task.CompletedTask; }
        public Task<bool> ShowDatabaseDialogAsync(DatabaseDialogViewModel viewModel) => throw new NotSupportedException();
        public Task<string?> PickOpenFileAsync(string title, params FileFilter[] filters) => throw new NotSupportedException();
        public Task<string?> PickSaveFileAsync(string title, string suggestedName, string defaultExtension, params FileFilter[] filters) => throw new NotSupportedException();
        public Task<bool> ShowDialogAsync(Window dialog) => throw new NotSupportedException();
        public Task ShowExportWizardAsync(ExportWizardViewModel viewModel) => throw new NotSupportedException();
        public Task<string?> PickFolderAsync(string title) => throw new NotSupportedException();
        public Task<bool> ShowQueryBuilderAsync(QueryBuilderViewModel viewModel) => throw new NotSupportedException();
        public Task ShowDataTransferAsync(DataTransferViewModel viewModel) => throw new NotSupportedException();
        public Task<bool> ShowOptionsAsync(OptionsViewModel viewModel) => throw new NotSupportedException();
        public Task<bool> ShowImportWizardAsync(ImportWizardViewModel viewModel) => throw new NotSupportedException();
    }
}
