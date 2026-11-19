using System.Reflection;
using Navifox.App.Services;
using Navifox.App.ViewModels;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.AppTests;

public sealed partial class WorkflowTests
{
    [Fact]
    public async Task UnTimeoutNonRichiudeIlNodoEF5SulMessaggioRicaricaLeTabelle()
    {
        var (window, probe) = CreateTreeWindow();
        var profile = fixture.Profile("Catalogo grande") with { Kind = DatabaseKind.MySql };
        var node = new TreeNodeViewModel("Tabelle", "table", TreeNodeKind.Category, profile, "geo_poi", "geo_poi", "Tabelle");
        probe.ListTables = _ => Task.FromException<IReadOnlyList<DatabaseObjectInfo>>(new TimeoutException("Timeout del catalogo"));

        await window.RefreshNodeCommand.ExecuteAsync(node);

        Assert.True(node.IsExpanded);
        Assert.False(node.IsLoaded);
        Assert.False(node.IsLoading);
        Assert.False(window.IsBusy);
        Assert.Equal("Timeout del catalogo", node.LoadError);
        var error = Assert.Single(node.Children);
        Assert.True(error.IsPlaceholder);
        Assert.Contains("F5", error.Name);
        Assert.DoesNotContain("Caricamento…", error.Name);
        Assert.Contains("Timeout del catalogo", window.StatusMessage);

        probe.ListTables = _ => Task.FromResult<IReadOnlyList<DatabaseObjectInfo>>([new("t", "BASE TABLE")]);
        await window.RefreshNodeCommand.ExecuteAsync(error);

        Assert.True(node.IsExpanded);
        Assert.True(node.IsLoaded);
        Assert.Null(node.LoadError);
        Assert.Equal("t", Assert.Single(node.Children).Name);
        Assert.False(node.IsLoading);
        Assert.False(window.IsBusy);

        window.SelectedNode = node;
        await window.ShowObjectsForSelectedNodeCommand.ExecuteAsync(null);
        Assert.Single(window.ObjectsTab.VisibleObjects);
        probe.ListTables = _ => Task.FromException<IReadOnlyList<DatabaseObjectInfo>>(new TimeoutException("Timeout durante il refresh"));
        await window.RefreshNodeCommand.ExecuteAsync(node);
        Assert.Empty(window.ObjectsTab.VisibleObjects);
        Assert.Contains("Timeout durante il refresh", window.StatusMessage);
        Assert.True(node.IsExpanded);
    }

    [Fact]
    public async Task AncheLaConnessioneRestaApertaSeIlCatalogoFallisce()
    {
        var (window, _) = CreateTreeWindow();
        var node = new TreeNodeViewModel("Server", "server", TreeNodeKind.Connection, fixture.Profile("Errore connessione")) { IsExpanded = true };
        window.SelectedNode = node;
        await window.ShowObjectsForSelectedNodeCommand.ExecuteAsync(null);
        Assert.True(node.IsExpanded);
        Assert.Equal("Timeout del server", node.LoadError);
        Assert.Contains("F5", Assert.Single(node.Children).Name);
    }

    [Fact]
    public async Task AnnullareIlCaricamentoNonLasciaUnFalsoErroreDiTimeout()
    {
        var (window, probe) = CreateTreeWindow();
        var node = new TreeNodeViewModel("Tabelle", "table", TreeNodeKind.Category, fixture.Profile("Annullamento"), "main", "main", "Tabelle");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.ListTables = async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { throw new InvalidOperationException("Il driver ha interrotto la connessione"); }
            return [];
        };
        var loading = window.RefreshNodeCommand.ExecuteAsync(node);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.CancelOperationCommand.Execute(null);
        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(node.IsLoading);
        Assert.False(node.IsLoaded);
        Assert.Null(node.LoadError);
        Assert.Equal("Operazione annullata.", window.StatusMessage);
        Assert.False(window.IsBusy);
    }

    private (MainWindowViewModel Window, TreeAccessProbe Probe) CreateTreeWindow()
    {
        var access = DispatchProxy.Create<IDatabaseAccessService, TreeAccessProbe>();
        // Questi test esercitano i comandi di navigazione, senza finestre né servizi di modifica dei profili.
        var window = new MainWindowViewModel(null!, access, null!, null!, new TestDialogs(), null!);
        return (window, (TreeAccessProbe)access);
    }

    public class TreeAccessProbe : DispatchProxy
    {
        public Func<CancellationToken, Task<IReadOnlyList<DatabaseObjectInfo>>> ListTables { get; set; } = _ => Task.FromResult<IReadOnlyList<DatabaseObjectInfo>>([]);
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "ListTablesAsync" => ListTables((CancellationToken)args![3]!),
            "ListDatabasesAsync" => Task.FromException<IReadOnlyList<string>>(new TimeoutException("Timeout del server")),
            "GetPropertiesAsync" => Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>([]),
            _ => throw new NotSupportedException(method?.Name)
        };
    }
}
