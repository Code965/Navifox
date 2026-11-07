using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Utente del server: MySQL «nome»@«host», PostgreSQL ruolo.</summary>
public sealed record ServerUser(string Name, string Host, string Attributes)
{
    public string Display => Host.Length == 0 ? Name : $"{Name}@{Host}";
}

/// <summary>
/// Utenti e privilegi: elenco, nuovo utente, password, eliminazione, permessi per database (tutti / lettura e scrittura / sola lettura / revoca)
/// e i GRANT attuali dell'utente selezionato.
/// </summary>
public partial class UsersTabViewModel : WorkspaceTabViewModel
{
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IDialogService _dialogs;

    public UsersTabViewModel(IDatabaseAccessService databaseAccess, IDialogService dialogs, ConnectionProfile profile)
    {
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        Profile = profile;
        _ = LoadAsync();
    }

    public ConnectionProfile Profile { get; }
    public bool IsMySql => Profile.Kind == DatabaseKind.MySql;
    public override string TabTitle => $"Utenti ({Profile.Name})";
    public override string TabGlyph => "users";
    public override ConnectionProfile? TabConnection => Profile;

    public ObservableCollection<ServerUser> Users { get; } = [];
    public ObservableCollection<string> Databases { get; } = [];
    public ObservableCollection<string> Grants { get; } = [];
    public IReadOnlyList<string> Presets { get; } = ["Tutti i privilegi", "Lettura e scrittura", "Sola lettura"];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUser))] private ServerUser? _selectedUser;
    [ObservableProperty] private string? _grantDatabase;
    [ObservableProperty] private string _preset = "Lettura e scrittura";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public bool HasUser => SelectedUser is not null;

    partial void OnSelectedUserChanged(ServerUser? value) => _ = LoadGrantsAsync();

    private string Account(ServerUser user) => IsMySql ? $"{Quote(user.Name)}@{Quote(user.Host)}" : _databaseAccess.Provider(Profile).QuoteIdentifier(user.Name);
    private static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
    private string Database(string name) => _databaseAccess.Provider(Profile).QuoteIdentifier(name);

    private async Task<QueryResult?> QueryAsync(string sql, string? database = null)
    {
        var results = await _databaseAccess.ExecuteBatchAsync(Profile, database ?? _databaseAccess.Provider(Profile).MaintenanceDatabase, sql, 5000, CancellationToken.None);
        if (results.FirstOrDefault(r => r.Error is not null) is { } failed) throw failed.Error!;
        return results.LastOrDefault()?.Result;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var selected = SelectedUser;
            var result = await QueryAsync(IsMySql
                ? "SELECT User, Host, CONCAT_WS(' · ', IF(account_locked = 'Y', 'bloccato', NULL), IF(password_expired = 'Y', 'password scaduta', NULL), plugin) FROM mysql.user ORDER BY User, Host"
                : "SELECT rolname, '', concat_ws(' · ', CASE WHEN rolsuper THEN 'superutente' END, CASE WHEN rolcanlogin THEN 'login' ELSE 'gruppo (no login)' END, CASE WHEN rolcreatedb THEN 'crea database' END, CASE WHEN rolcreaterole THEN 'crea ruoli' END, CASE WHEN rolvaliduntil IS NOT NULL THEN 'scade ' || rolvaliduntil::date END) FROM pg_roles WHERE rolname !~ '^pg_' ORDER BY rolname");
            Users.Clear();
            foreach (var row in result?.Rows ?? []) Users.Add(new ServerUser(Convert.ToString(row[0]) ?? string.Empty, Convert.ToString(row[1]) ?? string.Empty, Convert.ToString(row[2]) ?? string.Empty));
            Databases.Clear();
            foreach (var database in await _databaseAccess.ListDatabasesAsync(Profile, CancellationToken.None)) Databases.Add(database);
            GrantDatabase ??= Databases.FirstOrDefault(d => d is not ("information_schema" or "mysql" or "performance_schema" or "sys" or "postgres"));
            SelectedUser = Users.FirstOrDefault(u => u == selected) ?? Users.FirstOrDefault();
            StatusMessage = $"{Users.Count} {(Users.Count == 1 ? "utente" : "utenti")} su {Profile.Name}.";
        }
        catch (Exception exception) { StatusMessage = $"Impossibile leggere gli utenti (servono i privilegi di amministratore): {exception.Message}"; }
        finally { IsBusy = false; }
    }

    private int _grantsGeneration;

    private async Task LoadGrantsAsync()
    {
        var generation = ++_grantsGeneration;
        if (SelectedUser is not { } user)
        {
            Grants.Clear();
            return;
        }
        var lines = new List<string>();
        try
        {
            if (IsMySql)
            {
                var result = await QueryAsync($"SHOW GRANTS FOR {Account(user)}");
                lines.AddRange((result?.Rows ?? []).Select(row => Convert.ToString(row[0]) ?? string.Empty));
            }
            else
            {
                var literal = _databaseAccess.Provider(Profile).FormatLiteral(user.Name);
                // Solo i permessi concessi esplicitamente al ruolo (non quelli di PUBLIC).
                var databases = await QueryAsync($"""
                    SELECT 'Database ' || d.datname || ': ' || string_agg(a.privilege_type, ', ' ORDER BY a.privilege_type)
                    FROM pg_database d CROSS JOIN LATERAL aclexplode(d.datacl) a JOIN pg_roles r ON r.oid = a.grantee
                    WHERE r.rolname = {literal} GROUP BY d.datname ORDER BY d.datname
                    """);
                lines.AddRange((databases?.Rows ?? []).Select(row => Convert.ToString(row[0]) ?? string.Empty));
                var member = await QueryAsync($"SELECT 'Membro di: ' || string_agg(b.rolname, ', ') FROM pg_auth_members m JOIN pg_roles b ON b.oid = m.roleid JOIN pg_roles u ON u.oid = m.member WHERE u.rolname = {literal} HAVING count(*) > 0");
                lines.AddRange((member?.Rows ?? []).Select(row => Convert.ToString(row[0]) ?? string.Empty));
                if (GrantDatabase is { } database)
                {
                    var tables = await QueryAsync($"SELECT 'Tabelle di ' || current_database() || ': ' || string_agg(DISTINCT privilege_type, ', ') FROM information_schema.role_table_grants WHERE grantee = {literal} HAVING count(*) > 0", database);
                    lines.AddRange((tables?.Rows ?? []).Select(row => Convert.ToString(row[0]) ?? string.Empty));
                }
                if (lines.Count == 0) lines.Add("Nessun permesso esplicito (può collegarsi ai database aperti a PUBLIC).");
            }
        }
        catch (Exception exception) { lines.Add($"Impossibile leggere i privilegi: {exception.Message}"); }
        if (generation != _grantsGeneration) return;
        Grants.Clear();
        foreach (var line in lines) Grants.Add(line);
    }

    private async Task RunAsync(string title, IReadOnlyList<string> statements, string done, string? database = null)
    {
        if (!await SqlGuard.ConfirmProductionAsync(_dialogs, Profile, $"{title}:\n{string.Join("\n", statements.Select(s => "  " + Mask(s)))}")) return;
        IsBusy = true;
        try
        {
            await _databaseAccess.ExecuteStatementsAsync(Profile, database ?? _databaseAccess.Provider(Profile).MaintenanceDatabase, statements, CancellationToken.None);
            StatusMessage = done;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            await _dialogs.ShowMessageAsync(title, (exception.InnerException ?? exception).Message, isError: true);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Nei messaggi la password non compare.</summary>
    private static string Mask(string sql) => System.Text.RegularExpressions.Regex.Replace(sql, @"(IDENTIFIED BY|PASSWORD)\s+'(?:[^']|'')*'", "$1 '••••'", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    [RelayCommand]
    private async Task NewUserAsync()
    {
        var name = await _dialogs.PromptAsync("Nuovo utente", IsMySql ? "Nome utente (es. app oppure app@localhost; senza host = da qualsiasi host «%»):" : "Nome del ruolo:", string.Empty, "Avanti");
        if (string.IsNullOrWhiteSpace(name)) return;
        var host = "%";
        if (IsMySql && name.Contains('@'))
        {
            host = name[(name.IndexOf('@') + 1)..].Trim();
            name = name[..name.IndexOf('@')];
        }
        var password = await _dialogs.PromptAsync("Nuovo utente", $"Password per «{name.Trim()}»:", string.Empty, "Crea utente", isPassword: true);
        if (password is null) return;
        var user = new ServerUser(name.Trim(), IsMySql ? host : string.Empty, string.Empty);
        await RunAsync("Nuovo utente",
            [IsMySql ? $"CREATE USER {Account(user)} IDENTIFIED BY {Quote(password)}" : $"CREATE ROLE {Account(user)} LOGIN PASSWORD {Quote(password)}"],
            $"Utente «{user.Display}» creato. Ora concedigli i permessi su un database.");
        SelectedUser = Users.FirstOrDefault(u => u.Name == user.Name && (!IsMySql || u.Host == user.Host));
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        if (SelectedUser is not { } user) return;
        var password = await _dialogs.PromptAsync("Cambia password", $"Nuova password per «{user.Display}»:", string.Empty, "Cambia password", isPassword: true);
        if (password is null) return;
        await RunAsync("Cambia password", [IsMySql ? $"ALTER USER {Account(user)} IDENTIFIED BY {Quote(password)}" : $"ALTER ROLE {Account(user)} PASSWORD {Quote(password)}"],
            $"Password di «{user.Display}» cambiata.");
    }

    [RelayCommand]
    private async Task DeleteUserAsync()
    {
        if (SelectedUser is not { } user) return;
        if (!await _dialogs.ConfirmAsync("Elimina utente", $"Eliminare l’utente «{user.Display}»? Chi lo usa non potrà più collegarsi.", "Elimina", danger: true)) return;
        if (IsMySql)
        {
            await RunAsync("Elimina utente", [$"DROP USER {Account(user)}"], $"Utente «{user.Display}» eliminato.");
            return;
        }
        // PostgreSQL: un ruolo con permessi o oggetti non si può eliminare. In ogni database i suoi oggetti passano all'utente
        // corrente (REASSIGN OWNED) e i suoi permessi vengono tolti (DROP OWNED), poi si elimina il ruolo.
        if (!await SqlGuard.ConfirmProductionAsync(_dialogs, Profile, $"Stai per eliminare il ruolo «{user.Name}» e i suoi permessi in tutti i database.")) return;
        IsBusy = true;
        try
        {
            var who = Account(user);
            var databases = await QueryAsync("SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate");
            var skipped = new List<string>();
            foreach (var row in databases?.Rows ?? [])
            {
                var database = Convert.ToString(row[0]) ?? string.Empty;
                try { await _databaseAccess.ExecuteStatementsAsync(Profile, database, [$"REASSIGN OWNED BY {who} TO CURRENT_USER", $"DROP OWNED BY {who}"], CancellationToken.None); }
                catch (Exception exception) { skipped.Add($"{database}: {(exception.InnerException ?? exception).Message}"); }
            }
            await _databaseAccess.ExecuteStatementsAsync(Profile, _databaseAccess.Provider(Profile).MaintenanceDatabase, [$"DROP ROLE {who}"], CancellationToken.None);
            StatusMessage = $"Ruolo «{user.Name}» eliminato.";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            await _dialogs.ShowMessageAsync("Elimina utente", (exception.InnerException ?? exception).Message, isError: true);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleLockAsync()
    {
        if (SelectedUser is not { } user) return;
        var locked = user.Attributes.Contains("bloccato") || user.Attributes.Contains("no login");
        await RunAsync(locked ? "Sblocca utente" : "Blocca utente",
            [IsMySql ? $"ALTER USER {Account(user)} ACCOUNT {(locked ? "UNLOCK" : "LOCK")}" : $"ALTER ROLE {Account(user)} {(locked ? "LOGIN" : "NOLOGIN")}"],
            $"Utente «{user.Display}» {(locked ? "sbloccato" : "bloccato")}.");
    }

    [RelayCommand]
    private async Task GrantAsync()
    {
        if (SelectedUser is not { } user || GrantDatabase is not { } database) return;
        var who = Account(user);
        if (IsMySql)
        {
            var privileges = Preset switch
            {
                "Tutti i privilegi" => "ALL PRIVILEGES",
                "Sola lettura" => "SELECT, SHOW VIEW",
                _ => "SELECT, INSERT, UPDATE, DELETE, EXECUTE, SHOW VIEW, CREATE TEMPORARY TABLES, LOCK TABLES",
            };
            await RunAsync("Concedi permessi", [$"GRANT {privileges} ON {Database(database)}.* TO {who}"], $"«{Preset}» su «{database}» concessi a «{user.Display}».");
            return;
        }
        var tables = Preset switch { "Tutti i privilegi" => "ALL PRIVILEGES", "Sola lettura" => "SELECT", _ => "SELECT, INSERT, UPDATE, DELETE" };
        var sequences = Preset == "Sola lettura" ? "SELECT" : "USAGE, SELECT";
        await RunAsync("Concedi permessi",
        [
            $"GRANT CONNECT{(Preset == "Tutti i privilegi" ? ", CREATE, TEMP" : string.Empty)} ON DATABASE {Database(database)} TO {who}",
            $"GRANT USAGE{(Preset == "Tutti i privilegi" ? ", CREATE" : string.Empty)} ON SCHEMA public TO {who}",
            $"GRANT {tables} ON ALL TABLES IN SCHEMA public TO {who}",
            $"GRANT {sequences} ON ALL SEQUENCES IN SCHEMA public TO {who}",
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT {tables} ON TABLES TO {who}",
        ], $"«{Preset}» su «{database}» (schema public, tabelle attuali e future) concessi a «{user.Display}».", database);
    }

    [RelayCommand]
    private async Task RevokeAsync()
    {
        if (SelectedUser is not { } user || GrantDatabase is not { } database) return;
        if (!await _dialogs.ConfirmAsync("Revoca permessi", $"Togliere a «{user.Display}» tutti i permessi su «{database}»?", "Revoca", danger: true)) return;
        var who = Account(user);
        if (IsMySql)
        {
            await RunAsync("Revoca permessi", [$"REVOKE ALL PRIVILEGES ON {Database(database)}.* FROM {who}"], $"Permessi su «{database}» revocati a «{user.Display}».");
            return;
        }
        await RunAsync("Revoca permessi",
        [
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM {who}",
            $"REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {who}",
            $"REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM {who}",
            $"REVOKE ALL ON SCHEMA public FROM {who}",
            $"REVOKE ALL ON DATABASE {Database(database)} FROM {who}",
        ], $"Permessi su «{database}» revocati a «{user.Display}».", database);
    }

    partial void OnGrantDatabaseChanged(string? value)
    {
        if (!IsMySql) _ = LoadGrantsAsync();
    }
}
