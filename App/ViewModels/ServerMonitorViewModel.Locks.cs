using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Sessione in attesa di un blocco e sessione che la blocca.</summary>
public sealed record LockRow(MonitorServer Server, long WaitingPid, string WaitingUser, string WaitingQuery, long BlockingPid, string BlockingUser, string BlockingQuery, long WaitSeconds, string Resource)
{
    public string ServerName => Server.Name;
    public string WaitLabel => WaitSeconds >= 60 ? $"{WaitSeconds / 60}m {WaitSeconds % 60}s" : $"{WaitSeconds}s";
    public string WaitingShort => Short(WaitingQuery);
    public string BlockingShort => Short(BlockingQuery);
    private static string Short(string text) => text.Length > 200 ? text[..200].Replace('\n', ' ') + "…" : text.Replace('\n', ' ');
}

/// <summary>Blocchi e deadlock (chi blocca chi, con terminazione della sessione) e avvisi del Monitor.</summary>
public partial class ServerMonitorViewModel
{
    public const int LocksTabIndex = 3;
    public const int SlowQueriesTabIndex = 4;

    public ObservableCollection<LockRow> Locks { get; } = [];
    [ObservableProperty] private string _lockInfo = string.Empty;
    [ObservableProperty] private string _deadlockInfo = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KillBlockingCommand))]
    private LockRow? _selectedLock;

    private readonly List<LockRow> _lockRows = [];
    private readonly List<string> _deadlocks = [];

    private static string? LocksSql(DatabaseKind kind) => kind switch
    {
        DatabaseKind.MySql => """
            SELECT waiting_pid, '' AS waiting_user, COALESCE(waiting_query, ''), blocking_pid, '' AS blocking_user, COALESCE(blocking_query, ''),
                   COALESCE(wait_age_secs, 0), COALESCE(locked_table, '')
            FROM sys.innodb_lock_waits
            """,
        DatabaseKind.PostgreSql => """
            SELECT w.pid, COALESCE(w.usename, ''), COALESCE(w.query, ''), b.pid, COALESCE(b.usename, ''), COALESCE(b.query, ''),
                   COALESCE(EXTRACT(EPOCH FROM now() - w.query_start)::bigint, 0),
                   COALESCE(w.wait_event_type || ': ' || w.wait_event, '')
            FROM pg_stat_activity w
            CROSS JOIN LATERAL unnest(pg_blocking_pids(w.pid)) AS bp(pid)
            JOIN pg_stat_activity b ON b.pid = bp.pid
            """,
        DatabaseKind.SqlServer => """
            SELECT r.session_id, COALESCE(ws.login_name, ''), COALESCE(wt.text, ''), r.blocking_session_id, COALESCE(bs.login_name, ''), COALESCE(bt.text, ''),
                   r.wait_time / 1000, COALESCE(r.wait_resource, '')
            FROM sys.dm_exec_requests r
            JOIN sys.dm_exec_sessions ws ON ws.session_id = r.session_id
            LEFT JOIN sys.dm_exec_sessions bs ON bs.session_id = r.blocking_session_id
            LEFT JOIN sys.dm_exec_connections bc ON bc.session_id = r.blocking_session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) wt
            OUTER APPLY sys.dm_exec_sql_text(bc.most_recent_sql_handle) bt
            WHERE r.blocking_session_id <> 0
            """,
        _ => null
    };

    private async Task LoadLocksAsync(MonitorServer server, System.Data.Common.DbConnection connection, Navifox.Core.IDialectProvider provider)
    {
        var kind = server.Profile.Kind;
        if (LocksSql(kind) is not { } sql)
        {
            lock (_deadlocks) _deadlocks.Add($"{server.Name}: SQLite non ha sessioni concorrenti da monitorare.");
            return;
        }
        var result = await provider.ExecuteAsync(connection, sql, 500, CancellationToken.None);
        string S(IReadOnlyList<object?> row, int i) => Convert.ToString(row[i], CultureInfo.InvariantCulture) ?? string.Empty;
        long L(IReadOnlyList<object?> row, int i) => long.TryParse(S(row, i).Split('.')[0], out var n) ? n : 0;
        lock (_lockRows)
            foreach (var row in result.Rows)
                _lockRows.Add(new LockRow(server, L(row, 0), S(row, 1), S(row, 2), L(row, 3), S(row, 4), S(row, 5), L(row, 6), S(row, 7)));

        // Deadlock: l'ultimo rilevato (MySQL) o il contatore cumulativo (PostgreSQL, SQL Server).
        string? deadlock = null;
        try
        {
            switch (kind)
            {
                case DatabaseKind.MySql:
                    var status = await provider.ExecuteAsync(connection, "SHOW ENGINE INNODB STATUS", 1, CancellationToken.None);
                    var text = status.Rows.Count > 0 ? S(status.Rows[0], status.Columns.Count - 1) : string.Empty;
                    var start = text.IndexOf("LATEST DETECTED DEADLOCK", StringComparison.Ordinal);
                    if (start >= 0)
                    {
                        var end = text.IndexOf("TRANSACTIONS\n", start, StringComparison.Ordinal);
                        deadlock = $"{server.Name} · ultimo deadlock rilevato:\n" + text[start..(end > start ? end : Math.Min(text.Length, start + 4000))].Trim('-', '\n', ' ');
                    }
                    else deadlock = $"{server.Name}: nessun deadlock dall'avvio del server.";
                    break;
                case DatabaseKind.PostgreSql:
                    var pg = await provider.ExecuteAsync(connection, "SELECT datname, deadlocks FROM pg_stat_database WHERE deadlocks > 0 ORDER BY deadlocks DESC", 50, CancellationToken.None);
                    deadlock = pg.Rows.Count == 0 ? $"{server.Name}: nessun deadlock registrato."
                        : $"{server.Name} · deadlock dall'ultimo azzeramento delle statistiche: " + string.Join(", ", pg.Rows.Select(r => $"{S(r, 0)} {S(r, 1)}"));
                    break;
                case DatabaseKind.SqlServer:
                    var ss = await provider.ExecuteAsync(connection, "SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = 'Number of Deadlocks/sec' AND instance_name = '_Total'", 1, CancellationToken.None);
                    deadlock = ss.Rows.Count == 0 ? null : $"{server.Name} · deadlock dall'avvio del servizio: {S(ss.Rows[0], 0)}";
                    break;
            }
        }
        catch (Exception error) { deadlock = $"{server.Name}: informazioni sui deadlock non disponibili ({error.Message})."; }
        if (deadlock is not null) lock (_deadlocks) _deadlocks.Add(deadlock);
    }

    private void PublishLocks()
    {
        List<LockRow> rows;
        lock (_lockRows) { rows = _lockRows.OrderByDescending(r => r.WaitSeconds).ToList(); _lockRows.Clear(); }
        List<string> deadlocks;
        lock (_deadlocks) { deadlocks = [.. _deadlocks]; _deadlocks.Clear(); }
        Locks.Clear();
        foreach (var row in rows) Locks.Add(row);
        LockInfo = rows.Count == 0 ? "Nessuna sessione in attesa di un blocco." : $"{rows.Count} sessioni in attesa · seleziona una riga e termina la sessione che blocca.";
        DeadlockInfo = string.Join("\n\n", deadlocks);
    }

    private bool HasLockSelection() => SelectedLock is not null;

    [RelayCommand(CanExecute = nameof(HasLockSelection))]
    private async Task KillBlockingAsync()
    {
        if (SelectedLock is not { } row) return;
        if (!await _dialogs.ConfirmAsync("Termina la sessione che blocca",
                $"Terminare la sessione {row.BlockingPid}{(row.BlockingUser.Length > 0 ? $" ({row.BlockingUser})" : string.Empty)} su «{row.ServerName}»?\n\nLe sue modifiche non confermate verranno annullate.\n\n{row.BlockingShort}", "Termina", danger: true))
            return;
        try
        {
            var provider = _databaseAccess.Provider(row.Server.Profile);
            await using var connection = await _databaseAccess.OpenAsync(row.Server.Profile, string.Empty, CancellationToken.None);
            var sql = provider.BuildKillSql(row.BlockingPid, false);
            await provider.ExecuteAsync(connection, sql, 1, CancellationToken.None);
            SqlHistory.Record(row.Server.Profile.Name, null, sql, TimeSpan.Zero, null);
            StatusMessage = $"Sessione {row.BlockingPid} terminata.";
        }
        catch (Exception exception) { await _dialogs.ShowMessageAsync("Operazione non riuscita", exception.Message, isError: true); }
        await RefreshAsync();
    }

    // ───────────── Avvisi ─────────────

    /// <summary>Avviso da mostrare come notifica (titolo, testo). La finestra principale lo visualizza.</summary>
    public static event Action<string, string>? AlertRaised;

    [ObservableProperty] private decimal? _alertConnectionsPercent = AppSettings.Current.MonitorAlertConnectionsPercent;
    [ObservableProperty] private decimal? _alertSlowSeconds = AppSettings.Current.MonitorAlertSlowSeconds;
    [ObservableProperty] private string _alertText = string.Empty;
    private readonly HashSet<string> _activeAlerts = [];

    partial void OnAlertConnectionsPercentChanged(decimal? value)
    {
        AppSettings.Current.MonitorAlertConnectionsPercent = (int)Math.Clamp(value ?? 0, 0, 100);
        AppSettings.Current.Save();
    }

    partial void OnAlertSlowSecondsChanged(decimal? value)
    {
        AppSettings.Current.MonitorAlertSlowSeconds = (int)Math.Clamp(value ?? 0, 0, 86_400);
        AppSettings.Current.Save();
    }

    private bool WantsSlowAlerts => (AlertSlowSeconds ?? 0) > 0;

    /// <summary>Controlla le soglie: connessioni oltre la percentuale indicata e query in esecuzione da più di N secondi.
    /// Ogni condizione genera un solo avviso finché non rientra.</summary>
    private void CheckAlerts(IReadOnlyList<MonitorServer> servers, IReadOnlyList<ProcessRow> processes)
    {
        var current = new HashSet<string>();
        var messages = new List<string>();
        if ((AlertConnectionsPercent ?? 0) is > 0 and var percent)
            foreach (var server in servers.Where(s => !s.HasError && s.ConnectionUsage >= (double)percent))
            {
                var key = $"{server.Profile.Id}|connessioni";
                current.Add(key);
                var message = $"{server.Name}: connessioni al {server.ConnectionUsage:N0}% ({server.Connections}).";
                messages.Add(message);
                if (_activeAlerts.Add(key)) AlertRaised?.Invoke("Server Monitor · connessioni", message);
            }
        if ((AlertSlowSeconds ?? 0) is > 0 and var seconds)
            foreach (var process in processes.Where(p => !p.IsIdle && p.Info.Length > 0 && p.Time >= (long)seconds))
            {
                var key = $"{process.Server.Profile.Id}|lenta|{process.Id}";
                current.Add(key);
                var message = $"{process.Server.Name}: query in esecuzione da {process.TimeLabel} (processo {process.Id}) · {process.InfoShort[..Math.Min(120, process.InfoShort.Length)]}";
                messages.Add(message);
                if (_activeAlerts.Add(key)) AlertRaised?.Invoke("Server Monitor · query lenta", message);
            }
        _activeAlerts.IntersectWith(current);
        AlertText = string.Join("\n", messages.Take(4)) + (messages.Count > 4 ? $"\n… e altri {messages.Count - 4} avvisi" : string.Empty);
    }
}
