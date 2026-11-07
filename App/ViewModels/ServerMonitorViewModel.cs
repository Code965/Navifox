using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public partial class MonitorServer(ConnectionProfile profile) : ObservableObject
{
    public ConnectionProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string Kind => Profile.Kind.DisplayName();
    public string Endpoint => Profile.Endpoint;
    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private string _version = "—";
    [ObservableProperty] private string _uptime = "—";
    [ObservableProperty] private string _connections = "—";
    [ObservableProperty] private string _running = "—";
    [ObservableProperty] private string _queriesPerSecond = "—";
    [ObservableProperty] private string _slowQueries = "—";
    [ObservableProperty] private string _traffic = "—";
    [ObservableProperty] private double _connectionUsage;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private bool _hasError;
    public long? LastQueries { get; set; }
    public DateTime LastSample { get; set; }

    // Storico (ultimi 60 campioni) per i mini-grafici.
    private readonly List<double> _qps = [];
    private readonly List<double> _connectionSamples = [];
    [ObservableProperty] private IReadOnlyList<Navifox.App.Controls.ChartPoint>? _qpsHistory;
    [ObservableProperty] private IReadOnlyList<Navifox.App.Controls.ChartPoint>? _connectionHistory;
    [ObservableProperty] private string _qpsPeak = string.Empty;

    public void AddSample(double? qps, double? connections)
    {
        static IReadOnlyList<Navifox.App.Controls.ChartPoint> Push(List<double> list, double value)
        {
            list.Add(value);
            if (list.Count > 60) list.RemoveAt(0);
            return list.Select((v, i) => new Navifox.App.Controls.ChartPoint(i.ToString(), v)).ToArray();
        }
        if (qps is { } q)
        {
            QpsHistory = Push(_qps, q);
            QpsPeak = $"picco {_qps.Max():N1} q/s";
        }
        if (connections is { } c) ConnectionHistory = Push(_connectionSamples, c);
    }
}

/// <summary>Query più lente (statistiche del server: performance_schema / pg_stat_statements).</summary>
public sealed record SlowQueryRow(string ServerName, string Query, long Calls, double AverageMs, double TotalMs, double MaxMs, long Rows, string Database)
{
    public string AverageLabel => $"{AverageMs:N1} ms";
    public string TotalLabel => TotalMs >= 1000 ? $"{TotalMs / 1000:N1} s" : $"{TotalMs:N0} ms";
    public string MaxLabel => $"{MaxMs:N1} ms";
    public string QueryShort => Query.Length > 400 ? Query[..400].Replace('\n', ' ') + "…" : Query.Replace('\n', ' ');
}

public sealed record ProcessRow(MonitorServer Server, long Id, string User, string Host, string Db, string Command, long Time, string State, string Info)
{
    public string ServerName => $"{Server.Name} · {Navifox.App.Controls.DialectVisuals.DialectName(Server.Profile)}";
    public bool IsIdle => Command.Equals("Sleep", StringComparison.OrdinalIgnoreCase) || State.StartsWith("idle", StringComparison.OrdinalIgnoreCase) && !State.Contains("transaction");
    /// <summary>Query in esecuzione da almeno 10 s (esclude sessioni inattive e processi di servizio).</summary>
    public bool IsLong => !IsIdle && Time >= 10 && Info.Length > 0
                          && (Command is "Query" or "Execute" || State.StartsWith("active", StringComparison.OrdinalIgnoreCase));
    public string TimeLabel => Time >= 3600 ? $"{Time / 3600}h {Time % 3600 / 60}m" : Time >= 60 ? $"{Time / 60}m {Time % 60}s" : $"{Time}s";
    public string InfoShort => Info.Length > 300 ? Info[..300].Replace('\n', ' ') + "…" : Info.Replace('\n', ' ');
}

public sealed record ServerSetting(string ServerName, string Name, string Value);


/// <summary>Scheda «Server Monitor»: processi, variabili e stato di uno o più server.</summary>
public partial class ServerMonitorViewModel : WorkspaceTabViewModel
{
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _timer = new();
    private List<ProcessRow> _processes = [];
    private List<ServerSetting> _variables = [];
    private List<ServerSetting> _status = [];
    private bool _refreshing;

    public ServerMonitorViewModel(IDatabaseAccessService databaseAccess, IDialogService dialogs, IEnumerable<ConnectionProfile> profiles, Guid? focus)
    {
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        foreach (var profile in profiles) Servers.Add(new MonitorServer(profile) { IsChecked = focus is null || profile.Id == focus });
        foreach (var server in Servers) server.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MonitorServer.IsChecked)) _ = RefreshAsync();
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        UpdateTimer();
    }

    public override string TabTitle => "Server Monitor";
    public override string TabGlyph => "activity";

    public ObservableCollection<MonitorServer> Servers { get; } = [];
    public ObservableCollection<ProcessRow> Processes { get; } = [];
    public ObservableCollection<ServerSetting> Variables { get; } = [];
    public ObservableCollection<ServerSetting> Status { get; } = [];
    public ObservableCollection<SlowQueryRow> SlowQueries { get; } = [];
    [ObservableProperty] private string _slowQueryInfo = string.Empty;
    public IReadOnlyList<string> Intervals { get; } = ["Manuale", "2 s", "5 s", "10 s", "30 s"];

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _hideIdle;
    [ObservableProperty] private string _selectedInterval = "5 s";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EndProcessCommand), nameof(CancelQueryCommand), nameof(CopyQueryCommand))]
    private ProcessRow? _selectedProcess;

    public IEnumerable<MonitorServer> CheckedServers => Servers.Where(server => server.IsChecked);

    partial void OnFilterTextChanged(string value) => ApplyFilters();
    partial void OnHideIdleChanged(bool value) => ApplyFilters();
    partial void OnSelectedIntervalChanged(string value) => UpdateTimer();
    partial void OnSelectedTabChanged(int value) => _ = RefreshAsync();

    private void UpdateTimer()
    {
        _timer.Stop();
        if (!int.TryParse(SelectedInterval.Split(' ')[0], out var seconds)) return;
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }

    public override ValueTask CloseAsync()
    {
        _timer.Stop();
        return ValueTask.CompletedTask;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        IsBusy = true;
        try
        {
            var servers = CheckedServers.ToArray();
            var tasks = servers.Select(RefreshServerAsync).ToArray();
            var results = await Task.WhenAll(tasks);
            _processes = results.SelectMany(r => r.Processes).ToList();
            _variables = results.SelectMany(r => r.Variables).ToList();
            if (SelectedTab == 3)
            {
                List<SlowQueryRow> slow;
                lock (_slowQueries)
                {
                    slow = _slowQueries.OrderByDescending(q => q.AverageMs).ToList();
                    _slowQueries.Clear();
                }
                SlowQueries.Clear();
                foreach (var row in slow) SlowQueries.Add(row);
                if (slow.Count > 0) SlowQueryInfo = $"{slow.Count} query più lente in media (statistiche cumulative del server).";
            }
            _status = results.SelectMany(r => r.Status).ToList();
            ApplyFilters();
            StatusMessage = $"{servers.Length} server · {_processes.Count} processi · aggiornato alle {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            IsBusy = false;
            _refreshing = false;
        }
    }

    private async Task<(List<ProcessRow> Processes, List<ServerSetting> Variables, List<ServerSetting> Status)> RefreshServerAsync(MonitorServer server)
    {
        var processes = new List<ProcessRow>();
        var variables = new List<ServerSetting>();
        var status = new List<ServerSetting>();
        try
        {
            await using var connection = await _databaseAccess.OpenAsync(server.Profile, string.Empty, CancellationToken.None);
            var provider = _databaseAccess.Provider(server.Profile);
            var health = await provider.GetServerHealthAsync(connection, CancellationToken.None);
            UpdateHealth(server, health);
            if (SelectedTab == 0)
            {
                var result = await provider.GetProcessListAsync(connection, CancellationToken.None);
                int Col(string name) => result.Columns.ToList().FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                int id = Col("Id"), user = Col("User"), host = Col("Host"), db = Col("db"), command = Col("Command"), time = Col("Time"), state = Col("State"), info = Col("Info");
                string S(IReadOnlyList<object?> row, int i) => i < 0 ? string.Empty : Convert.ToString(row[i], CultureInfo.InvariantCulture) ?? string.Empty;
                foreach (var row in result.Rows)
                    processes.Add(new ProcessRow(server, long.TryParse(S(row, id), out var pid) ? pid : 0, S(row, user), S(row, host), S(row, db),
                        S(row, command), long.TryParse(S(row, time), out var t) ? t : 0, S(row, state), S(row, info)));
            }
            else if (SelectedTab == 3)
                await LoadSlowQueriesAsync(server, connection, provider);
            else if (SelectedTab == 1)
                variables.AddRange((await provider.GetServerVariablesAsync(connection, CancellationToken.None)).Select(p => new ServerSetting($"{server.Name} · {Navifox.App.Controls.DialectVisuals.DialectName(server.Profile)}", p.Key, p.Value)));
            else
                status.AddRange((await provider.GetServerStatusAsync(connection, CancellationToken.None)).Select(p => new ServerSetting($"{server.Name} · {Navifox.App.Controls.DialectVisuals.DialectName(server.Profile)}", p.Key, p.Value)));
            server.HasError = false;
            server.Error = string.Empty;
        }
        catch (Exception exception)
        {
            server.HasError = true;
            server.Error = exception.Message;
        }
        return (processes, variables, status);
    }

    private static void UpdateHealth(MonitorServer server, ServerHealth health)
    {
        server.Version = health.Version.Length > 60 ? health.Version[..60] + "…" : health.Version;
        server.Uptime = health.Uptime is { } up ? up.TotalDays >= 1 ? $"{(int)up.TotalDays} g {up.Hours} h" : $"{up.Hours} h {up.Minutes} min" : "—";
        server.Connections = health.Connections is { } c ? health.MaxConnections is { } max ? $"{c:N0} / {max:N0}" : $"{c:N0}" : "—";
        server.ConnectionUsage = health.Connections is { } used && health.MaxConnections is > 0 ? Math.Min(100, used * 100d / health.MaxConnections.Value) : 0;
        server.Running = health.Running?.ToString("N0") ?? "—";
        server.SlowQueries = health.SlowQueries?.ToString("N0") ?? "—";
        server.Traffic = health.BytesReceived is { } rx && health.BytesSent is { } tx ? $"↓ {Bytes(rx)} · ↑ {Bytes(tx)}" : "—";
        var now = DateTime.UtcNow;
        if (health.Queries is { } queries)
        {
            if (server.LastQueries is { } previous && (now - server.LastSample).TotalSeconds > 0.5)
            {
                var rate = Math.Max(0, (queries - previous) / (now - server.LastSample).TotalSeconds);
                server.QueriesPerSecond = $"{rate:N1}";
                server.AddSample(rate, health.Connections);
            }
            else if (health.Uptime is { TotalSeconds: > 0 } uptime)
                server.QueriesPerSecond = $"{queries / uptime.TotalSeconds:N1}";
            server.LastQueries = queries;
            server.LastSample = now;
        }
    }

    private readonly List<SlowQueryRow> _slowQueries = [];

    /// <summary>MySQL: performance_schema.events_statements_summary_by_digest · PostgreSQL: pg_stat_statements (se installata).</summary>
    private async Task LoadSlowQueriesAsync(MonitorServer server, System.Data.Common.DbConnection connection, Navifox.Core.IDialectProvider provider)
    {
        var mysql = server.Profile.Kind == DatabaseKind.MySql;
        if (server.Profile.Kind == DatabaseKind.Sqlite)
        {
            SlowQueryInfo = $"{server.Name}: SQLite non raccoglie statistiche delle query.";
            return;
        }
        var sql = server.Profile.Kind == DatabaseKind.SqlServer
            ? """
              SELECT TOP (50) SUBSTRING(t.text, (s.statement_start_offset / 2) + 1,
                       ((CASE s.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE s.statement_end_offset END - s.statement_start_offset) / 2) + 1),
                     s.execution_count, s.total_elapsed_time / 1000.0 / s.execution_count, s.total_elapsed_time / 1000.0, s.max_elapsed_time / 1000.0,
                     s.total_rows, COALESCE(DB_NAME(t.dbid), '')
              FROM sys.dm_exec_query_stats s CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) t
              ORDER BY s.total_elapsed_time / s.execution_count DESC
              """
            : mysql
            ? """
              SELECT DIGEST_TEXT, COUNT_STAR, AVG_TIMER_WAIT / 1000000000, SUM_TIMER_WAIT / 1000000000, MAX_TIMER_WAIT / 1000000000, SUM_ROWS_SENT, COALESCE(SCHEMA_NAME, '')
              FROM performance_schema.events_statements_summary_by_digest
              WHERE DIGEST_TEXT IS NOT NULL ORDER BY AVG_TIMER_WAIT DESC LIMIT 50
              """
            : """
              SELECT s.query, s.calls, s.mean_exec_time, s.total_exec_time, s.max_exec_time, s.rows, COALESCE(d.datname, '')
              FROM pg_stat_statements s LEFT JOIN pg_database d ON d.oid = s.dbid
              ORDER BY s.mean_exec_time DESC LIMIT 50
              """;
        try
        {
            var result = await provider.ExecuteAsync(connection, sql, 50, CancellationToken.None);
            string S(object? v) => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
            double D(object? v) => double.TryParse(S(v), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
            long L(object? v) => (long)D(v);
            var name = $"{server.Name} · {Navifox.App.Controls.DialectVisuals.DialectName(server.Profile)}";
            lock (_slowQueries)
                foreach (var row in result.Rows) _slowQueries.Add(new SlowQueryRow(name, S(row[0]), L(row[1]), D(row[2]), D(row[3]), D(row[4]), L(row[5]), S(row[6])));
        }
        catch (Exception exception)
        {
            SlowQueryInfo = server.Profile.Kind != DatabaseKind.PostgreSql
                ? $"{server.Name}: statistiche non disponibili ({exception.Message})."
                : $"{server.Name}: serve l'estensione pg_stat_statements (shared_preload_libraries = 'pg_stat_statements', poi CREATE EXTENSION pg_stat_statements). {exception.Message}";
        }
    }

    private static string Bytes(long value) => value switch
    {
        >= 1L << 30 => $"{value / (double)(1L << 30):N1} GB",
        >= 1L << 20 => $"{value / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{value / 1024d:N0} KB",
        _ => $"{value} B"
    };

    private void ApplyFilters()
    {
        var query = FilterText.Trim();
        bool Match(params string[] values) => query.Length == 0 || values.Any(v => v.Contains(query, StringComparison.OrdinalIgnoreCase));
        var selectedKey = SelectedProcess is { } s ? (s.ServerName, s.Id) : default;
        Processes.Clear();
        foreach (var row in _processes.Where(p => (!HideIdle || !p.IsIdle) && Match(p.User, p.Host, p.Db, p.Command, p.State, p.Info, p.Id.ToString()))) Processes.Add(row);
        SelectedProcess = Processes.FirstOrDefault(p => (p.ServerName, p.Id) == selectedKey);
        Variables.Clear();
        foreach (var row in _variables.Where(v => Match(v.Name, v.Value))) Variables.Add(row);
        Status.Clear();
        foreach (var row in _status.Where(v => Match(v.Name, v.Value))) Status.Add(row);
    }

    private bool HasSelection() => SelectedProcess is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task EndProcessAsync() => KillAsync(queryOnly: false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task CancelQueryAsync() => KillAsync(queryOnly: true);

    private async Task KillAsync(bool queryOnly)
    {
        if (SelectedProcess is not { } process) return;
        var what = queryOnly ? "interrompere la query del processo" : "terminare il processo";
        if (!await _dialogs.ConfirmAsync(queryOnly ? "Interrompi query" : "Termina processo",
                $"Vuoi {what} {process.Id} ({process.User}@{process.Host}) su «{process.ServerName}»?\n\n{process.InfoShort}", queryOnly ? "Interrompi" : "Termina", danger: true))
            return;
        try
        {
            var provider = _databaseAccess.Provider(process.Server.Profile);
            await using var connection = await _databaseAccess.OpenAsync(process.Server.Profile, string.Empty, CancellationToken.None);
            var sql = provider.BuildKillSql(process.Id, queryOnly);
            await provider.ExecuteAsync(connection, sql, 1, CancellationToken.None);
            SqlHistory.Record(process.Server.Profile.Name, null, sql, TimeSpan.Zero, null);
            StatusMessage = queryOnly ? $"Query del processo {process.Id} interrotta." : $"Processo {process.Id} terminato.";
        }
        catch (Exception exception) { await _dialogs.ShowMessageAsync("Operazione non riuscita", exception.Message, isError: true); }
        await RefreshAsync();
    }

    /// <summary>Testo da copiare (impostato dalla vista sugli appunti).</summary>
    public string? SelectedQueryText => SelectedProcess?.Info;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyQuery() => CopyRequested?.Invoke(SelectedProcess?.Info ?? string.Empty);

    public event Action<string>? CopyRequested;
}
