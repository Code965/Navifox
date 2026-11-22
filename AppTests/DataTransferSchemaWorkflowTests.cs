using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Navifox.App.Services;
using Navifox.App.ViewModels;
using Navifox.Core;
using Navifox.Core.Models;
using Navifox.Dialects;
using Xunit;

namespace Navifox.AppTests;

public sealed partial class WorkflowTests
{
    private static ConnectionProfile SchemaProfile(string name = "PostgreSQL") => new()
        { Kind = DatabaseKind.PostgreSql, Name = name, Host = "localhost", DefaultDatabase = "app" };

    private static (IDatabaseAccessService Access, TransferSchemaProbe Probe) SchemaAccess()
    {
        var access = DispatchProxy.Create<IDatabaseAccessService, TransferSchemaProbe>();
        return (access, (TransferSchemaProbe)access);
    }

    [Fact]
    public async Task AprireDataTransferDallAlberoPreselezionaLoSchemaDelNodo()
    {
        var (access, _) = SchemaAccess();
        var profile = SchemaProfile();
        var dialogs = new TestDialogs();
        var window = new MainWindowViewModel(null!, access, null!, null!, dialogs, null!);
        window.Profiles.Add(profile);
        var node = new TreeNodeViewModel("Tabelle", "table", TreeNodeKind.Category, profile, "app", "portfolio", "Tabelle");
        await window.OpenDataTransferCommand.ExecuteAsync(node);
        Assert.NotNull(dialogs.LastTransfer);
        Assert.Equal("portfolio", dialogs.LastTransfer.SourceSchema);
    }

    [Fact]
    public async Task CopiaEIncollaTraSchemiPreimpostaEntrambiISchemiAncheNellaStessaConnessione()
    {
        var (access, _) = SchemaAccess();
        var profile = SchemaProfile();
        var dialogs = new TestDialogs();
        var window = new MainWindowViewModel(null!, access, null!, null!, dialogs, null!);
        window.Profiles.Add(profile);
        var source = new TreeNodeViewModel("public_table", "table", TreeNodeKind.Table, profile, "app", "public", "Tabelle");
        var target = new TreeNodeViewModel("Tabelle", "table", TreeNodeKind.Category, profile, "app", "portfolio", "Tabelle");
        window.CopyTablesCommand.Execute(source);
        await window.PasteTablesCommand.ExecuteAsync(target);
        var transfer = dialogs.LastTransfer;
        Assert.NotNull(transfer);
        Assert.Equal("public", transfer.SourceSchema);
        Assert.Equal("portfolio", transfer.TargetSchema);
        Assert.Equal(profile, transfer.SourceProfile);
        Assert.Equal(profile, transfer.TargetProfile);
        Assert.True(Assert.Single(transfer.Tables).IsSelected);
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task TrasferimentoTraSchemiDellaStessaConnessioneUsaLeScelteNelRiepilogoENelleScritture()
    {
        var (access, probe) = SchemaAccess();
        var profile = SchemaProfile();
        var transfer = new DataTransferViewModel(access, [profile], profile, "app", target: profile, targetDatabase: "app", sourceSchema: "portfolio", targetSchema: "public")
            { CopyData = false, CreateTables = true };
        await WaitFor(() => !transfer.IsSourceLoading && !transfer.IsTargetLoading);
        Assert.Equal(["public", "portfolio", "archive"], transfer.SourceSchemas);
        Assert.Equal("portfolio", transfer.SourceSchema);
        Assert.Equal("public", transfer.TargetSchema);
        Assert.Equal("portfolio_table", Assert.Single(transfer.Tables).Name);
        transfer.NextCommand.Execute(null);
        Assert.Equal(2, transfer.Step);
        transfer.NextCommand.Execute(null);
        Assert.Equal(3, transfer.Step);
        Assert.Equal("portfolio.portfolio_table", Assert.Single(transfer.SummaryRows).Source);
        Assert.Equal("public.portfolio_table", Assert.Single(transfer.SummaryRows).Target);
        Assert.Contains("app / portfolio", transfer.Summary);
        await transfer.StartCommand.ExecuteAsync(null);
        Assert.StartsWith("Completato:", transfer.StatusMessage);
        Assert.Equal(("portfolio", "public"), Assert.Single(probe.ProviderProbe.Transfers));
        Assert.Contains(probe.ProviderProbe.Executed, sql => sql.StartsWith("DROP TABLE IF EXISTS \"public\".\"portfolio_table\""));
        Assert.DoesNotContain(probe.ProviderProbe.Executed, sql => sql.StartsWith("DROP TABLE IF EXISTS \"portfolio\"."));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoStessoSchemaRestaProtettoAncheAttraversoDueProfili(bool alias)
    {
        var (access, probe) = SchemaAccess();
        var source = SchemaProfile();
        var target = alias ? SchemaProfile("Altro profilo dello stesso server") : source;
        var transfer = new DataTransferViewModel(access, [source, target], source, "app", target: target, targetDatabase: "app", sourceSchema: "portfolio", targetSchema: "portfolio");
        await WaitFor(() => !transfer.IsSourceLoading && !transfer.IsTargetLoading);
        transfer.NextCommand.Execute(null);
        Assert.Equal(1, transfer.Step);
        Assert.Contains("coincidono", transfer.StatusMessage);
        await transfer.StartCommand.ExecuteAsync(null);
        Assert.Empty(probe.Opened);
        Assert.Empty(probe.ProviderProbe.Executed);
    }

    [Fact]
    public async Task ScambiaOrigineEDestinazioneConservaAncheGliSchemiNelloStessoDatabase()
    {
        var (access, _) = SchemaAccess();
        var profile = SchemaProfile();
        var transfer = new DataTransferViewModel(access, [profile], profile, "app", target: profile, targetDatabase: "app", sourceSchema: "portfolio", targetSchema: "archive");
        await WaitFor(() => !transfer.IsSourceLoading && !transfer.IsTargetLoading);
        transfer.SwapCommand.Execute(null);
        await WaitFor(() => !transfer.IsSourceLoading && !transfer.IsTargetLoading);
        Assert.Equal("app", transfer.SourceDatabase);
        Assert.Equal("app", transfer.TargetDatabase);
        Assert.Equal("archive", transfer.SourceSchema);
        Assert.Equal("portfolio", transfer.TargetSchema);
        Assert.Equal("archive_table", Assert.Single(transfer.Tables).Name);
    }

    [Fact]
    public async Task UnVecchioElencoNonSostituisceLeTabelleDellaNuovaConnessioneConLoStessoDatabaseESchema()
    {
        var (access, probe) = SchemaAccess();
        var first = SchemaProfile("Prima");
        var second = SchemaProfile("Seconda");
        var old = new TaskCompletionSource<IReadOnlyList<DatabaseObjectInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.ReadTables = (profile, _, _) => profile.Id == first.Id ? old.Task
            : Task.FromResult<IReadOnlyList<DatabaseObjectInfo>>([new("nuova_tabella", "TABLE")]);
        var transfer = new DataTransferViewModel(access, [first, second], first, "app", sourceSchema: "portfolio");
        Assert.True(transfer.IsSourceLoading);
        transfer.SourceProfile = second;
        await WaitFor(() => !transfer.IsSourceLoading);
        old.SetResult([new("vecchia_tabella", "TABLE")]);
        await Task.Delay(50);
        Assert.Equal("nuova_tabella", Assert.Single(transfer.Tables).Name);
    }

    [Fact]
    public async Task UnVecchioCaricamentoDiSchemiNonContaminaUnAltroDatabase()
    {
        var (access, probe) = SchemaAccess();
        var old = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.ReadSchemas = database => database == "app" ? old.Task : Task.FromResult<IReadOnlyList<string>>(["nuovo_schema"]);
        var profile = SchemaProfile();
        var transfer = new DataTransferViewModel(access, [profile], profile, "app");
        transfer.SourceDatabase = "altro";
        await WaitFor(() => !transfer.IsSourceLoading);
        old.SetResult(["schema_vecchio"]);
        await Task.Delay(50);
        Assert.Equal(["nuovo_schema"], transfer.SourceSchemas);
        Assert.Equal("nuovo_schema", transfer.SourceSchema);
        Assert.Equal("nuovo_schema_table", Assert.Single(transfer.Tables).Name);
    }

    [Fact]
    public async Task SchemaNonDisponibileImpedisceDiAvviareUnaCopiaInPublicPerErrore()
    {
        var (access, probe) = SchemaAccess();
        probe.ReadSchemas = _ => Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("Schemi non accessibili"));
        var profile = SchemaProfile();
        var transfer = new DataTransferViewModel(access, [profile], profile, "app", sourceSchema: "portfolio");
        await WaitFor(() => !transfer.IsSourceLoading && !transfer.IsTargetLoading);
        Assert.Null(transfer.SourceSchema);
        Assert.Empty(transfer.Tables);
        await transfer.StartCommand.ExecuteAsync(null);
        Assert.Empty(probe.Opened);
        Assert.Empty(probe.ProviderProbe.Executed);
    }

    [Fact]
    public async Task EsportazioneSqlLeggeLoSchemaSceltoAncheSenzaUnaDestinazioneDatabase()
    {
        var (access, probe) = SchemaAccess();
        var profile = SchemaProfile();
        var file = Path.Combine(fixture.Root, Guid.NewGuid() + ".sql");
        var transfer = new DataTransferViewModel(access, [profile], profile, "app", sourceSchema: "portfolio")
            { TargetIsFile = true, TargetFile = file, CopyData = false };
        await WaitFor(() => !transfer.IsSourceLoading);
        await transfer.StartCommand.ExecuteAsync(null);
        Assert.StartsWith("Completato:", transfer.StatusMessage);
        Assert.Equal("portfolio", Assert.Single(probe.ProviderProbe.ExportedSchemas));
        Assert.Contains("\"portfolio\".\"portfolio_table\"", await File.ReadAllTextAsync(file));
    }

    public class TransferSchemaProbe : DispatchProxy
    {
        public Func<ConnectionProfile, string, string, Task<IReadOnlyList<DatabaseObjectInfo>>> ReadTables { get; set; } =
            (_, _, schema) => Task.FromResult<IReadOnlyList<DatabaseObjectInfo>>([new(schema + "_table", "TABLE")]);
        public Func<string, Task<IReadOnlyList<string>>> ReadSchemas { get; set; } =
            _ => Task.FromResult<IReadOnlyList<string>>(["public", "portfolio", "archive"]);
        public IDialectProvider Dialect { get; } = DispatchProxy.Create<IDialectProvider, TransferDialectProbe>();
        public TransferDialectProbe ProviderProbe => (TransferDialectProbe)Dialect;
        public List<string> Opened { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "ListDatabasesAsync" => Task.FromResult<IReadOnlyList<string>>(["app", "altro"]),
            "ListSchemasAsync" => ReadSchemas((string)args![1]!),
            "ListTablesAsync" => ReadTables((ConnectionProfile)args![0]!, (string)args[1]!, (string)args[2]!),
            "GetPropertiesAsync" => Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>([]),
            "Provider" => Dialect,
            "OpenAsync" => Open((string)args![1]!),
            _ => throw new NotSupportedException(method?.Name)
        };
        private async Task<DbConnection> Open(string database)
        {
            Opened.Add(database);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            return connection;
        }
    }

    public class TransferDialectProbe : DispatchProxy
    {
        private readonly PostgreSqlDialectProvider _sql = new();
        public List<(string Source, string Target)> Transfers { get; } = [];
        public List<string> ExportedSchemas { get; } = [];
        public List<string> Executed { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_UsesMySqlScriptSyntax": return false;
                case "get_Kind": return DatabaseKind.PostgreSql;
                case "QuoteIdentifier": return _sql.QuoteIdentifier((string)args![0]!);
                case "SplitScript": return _sql.SplitScript((string)args![0]!);
                case "DumpHeader": return _sql.DumpHeader((string)args![0]!);
                case "DumpFooter": return "";
                case "GetTransferDdlAsync":
                    Transfers.Add(((string)args![2]!, (string)args[3]!));
                    return Task.FromResult(new TableDdl($"CREATE TABLE {_sql.DumpName((string)args[3]!, (string)args[4]!)} (id integer);", []));
                case "GetDdlAsync":
                    ExportedSchemas.Add((string)args![2]!);
                    return Task.FromResult(new TableDdl($"CREATE TABLE {_sql.DumpName((string)args[2]!, (string)args[3]!)} (id integer);", []));
                case "ExecuteAsync":
                    Executed.Add((string)args![1]!);
                    return Task.FromResult(new QueryResult([], [], 0, false));
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }
}
