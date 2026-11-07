using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public partial class ConnectionDialogViewModel(IDatabaseAccessService databaseAccess) : ObservableObject
{
    public ObservableCollection<DatabaseKind> DatabaseKinds { get; } = [DatabaseKind.MySql, DatabaseKind.PostgreSql];
    public IReadOnlyList<string> SslModes { get; } = ["Prefer", "Require", "VerifyCA", "VerifyFull", "Disable"];
    public event Action<ConnectionProfile?>? CloseRequested;

    [ObservableProperty] private string _name = "Nuova connessione";
    [ObservableProperty] private string _folder = string.Empty;

    /// <summary>Cartelle già presenti (per la casella «Cartella»).</summary>
    public ObservableCollection<string> Folders { get; } = [];

    /// <summary>Connessione in modifica (null = nuova).</summary>
    public ConnectionProfile? Editing { get; private set; }
    public bool IsEditing => Editing is not null;
    public string FinishLabel => IsEditing ? "Salva" : "Fine";

    /// <summary>Apre il dialogo sulle impostazioni di una connessione esistente.</summary>
    public void LoadFrom(ConnectionProfile profile)
    {
        var driver = DriverCatalog.Drivers.FirstOrDefault(d => d.Name == profile.Driver && d.IsSupported)
                     ?? DriverCatalog.Drivers.First(d => d.Kind == profile.Kind);
        SelectedDriver = driver;
        Next();
        Editing = profile;
        Name = profile.Name;
        Folder = profile.Folder;
        IsFavorite = profile.IsFavorite;
        Environment = profile.Environment;
        Color = profile.Color;
        SelectedKind = profile.Kind;
        Host = profile.Host;
        PortText = profile.Port.ToString();
        UserName = profile.UserName;
        Password = profile.Password;
        Database = profile.DefaultDatabase;
        SelectedSslMode = profile.SslMode;
        AdvancedParameters = profile.AdvancedParameters;
        ConnectionStringOverride = profile.ConnectionStringOverride;
        UseSshTunnel = profile.UseSshTunnel;
        SshHost = profile.SshHost;
        SshPortText = profile.SshPort.ToString();
        SshUserName = profile.SshUserName;
        SshPassword = profile.SshPassword;
        SshUsePrivateKey = profile.SshUsePrivateKey;
        SshPrivateKeyPath = profile.SshPrivateKeyPath;
        SshPrivateKeyPassphrase = profile.SshPrivateKeyPassphrase;
        SshHostKeyFingerprint = profile.SshHostKeyFingerprint;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(FinishLabel));
        OnPropertyChanged(nameof(StepTitle));
    }
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string _environment = string.Empty;
    [ObservableProperty] private string? _color;

    public IReadOnlyList<string> Environments { get; } = ["", "Sviluppo", "Test", "Produzione"];
    public IReadOnlyList<string> Colors { get; } = ["", "#E5484D", "#F76B15", "#F5B400", "#30A46C", "#12A594", "#0090FF", "#6E56CF", "#8B8D98"];

    partial void OnEnvironmentChanged(string value)
    {
        // Produzione senza colore: rosso, per riconoscerla subito.
        if (value == "Produzione" && string.IsNullOrEmpty(Color)) Color = "#E5484D";
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void PickColor(string? color) => Color = string.IsNullOrEmpty(color) ? null : color;
    [ObservableProperty] private DatabaseKind _selectedKind = DatabaseKind.MySql;
    [ObservableProperty] private string _host = "localhost";
    [ObservableProperty] private string _portText = "3306";
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _database = string.Empty;
    [ObservableProperty] private string _selectedSslMode = "Prefer";
    [ObservableProperty] private string _connectionStringOverride = string.Empty;
    [ObservableProperty] private string _advancedParameters = string.Empty;
    [ObservableProperty] private bool _useSshTunnel;
    [ObservableProperty] private string _sshHost = string.Empty;
    [ObservableProperty] private string _sshPortText = "22";
    [ObservableProperty] private string _sshUserName = string.Empty;
    [ObservableProperty] private string _sshPassword = string.Empty;
    [ObservableProperty] private bool _sshUsePrivateKey;
    [ObservableProperty] private string _sshPrivateKeyPath = string.Empty;
    [ObservableProperty] private string _sshPrivateKeyPassphrase = string.Empty;
    [ObservableProperty] private string _sshHostKeyFingerprint = string.Empty;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _connectionSucceeded;

    partial void OnSelectedKindChanged(DatabaseKind value)
    {
        PortText = value switch { DatabaseKind.MySql => "3306", DatabaseKind.SqlServer => "1433", DatabaseKind.Sqlite => "0", _ => "5432" };
        if (value == DatabaseKind.Sqlite && Host == "localhost") Host = string.Empty;
        if (value != DatabaseKind.Sqlite && string.IsNullOrWhiteSpace(Host)) Host = "localhost";
        OnPropertyChanged(nameof(IsFileBased));
        OnPropertyChanged(nameof(IsServerBased));
        OnPropertyChanged(nameof(HostLabel));
        OnPropertyChanged(nameof(HostWatermark));
        OnPropertyChanged(nameof(UserWatermark));
    }

    /// <summary>SQLite: al posto di host, porta e credenziali si sceglie il file.</summary>
    public bool IsFileBased => SelectedKind.IsFileBased();
    public bool IsServerBased => !IsFileBased;
    public string HostLabel => SelectedKind switch { DatabaseKind.Sqlite => "File del database", DatabaseKind.SqlServer => "Server (host oppure host\\ISTANZA)", _ => "Host" };
    public string HostWatermark => SelectedKind switch { DatabaseKind.Sqlite => "C:\\dati\\archivio.db (se non esiste viene creato)", DatabaseKind.SqlServer => "localhost\\SQLEXPRESS", _ => "localhost" };
    public string UserWatermark => SelectedKind == DatabaseKind.SqlServer ? "Vuoto = autenticazione di Windows" : string.Empty;

    /// <summary>La vista apre il selettore di file e restituisce il percorso scelto.</summary>
    public Func<Task<string?>>? BrowseFile { get; set; }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (BrowseFile is null) return;
        var path = await BrowseFile();
        if (string.IsNullOrWhiteSpace(path)) return;
        Host = path;
        if (Name is "Nuova connessione" or "SQLite") Name = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    // ───────────── Passo 1: scelta del driver ─────────────

    public ObservableCollection<DriverCategory> Categories { get; } = [];
    public ObservableCollection<DriverInfo> VisibleDrivers { get; } = [];

    [ObservableProperty] private int _step;
    [ObservableProperty] private string _driverSearch = string.Empty;
    [ObservableProperty] private DriverCategory? _selectedCategory;
    [ObservableProperty] private bool _sortByName;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private DriverInfo? _selectedDriver;
    [ObservableProperty] private string _driverHint = "Seleziona un driver per continuare.";

    public bool IsDriverStep => Step == 0;
    public bool IsDetailsStep => Step == 1;
    public string StepTitle => IsDriverStep ? "Seleziona il tuo database" : $"{(Editing is null ? "Impostazioni di connessione" : "Modifica connessione")} · {SelectedDriver?.Name}";
    public string StepSubtitle => IsDriverStep
        ? "Crea una nuova connessione: trova il driver del database nell’elenco qui sotto."
        : SelectedDriver?.Description ?? string.Empty;
    public bool SortByScore
    {
        get => !SortByName;
        set => SortByName = !value;
    }
    public bool HasNoDrivers => VisibleDrivers.Count == 0;

    public void InitializeCatalog()
    {
        Categories.Clear();
        foreach (var name in DriverCatalog.CategoryOrder)
        {
            var count = name == DriverCatalog.All ? DriverCatalog.Drivers.Count : DriverCatalog.Drivers.Count(driver => driver.Categories.Contains(name));
            if (count > 0) Categories.Add(new DriverCategory(name, DriverCatalog.GlyphFor(name), count));
        }
        SelectedCategory = Categories[0];
        SelectedDriver = DriverCatalog.Drivers.First(driver => driver.Kind == DatabaseKind.MySql);
    }

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsDriverStep));
        OnPropertyChanged(nameof(IsDetailsStep));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepSubtitle));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }

    partial void OnDriverSearchChanged(string value) => ApplyDriverFilter();
    partial void OnSelectedCategoryChanged(DriverCategory? value) => ApplyDriverFilter();
    partial void OnSortByNameChanged(bool value)
    {
        OnPropertyChanged(nameof(SortByScore));
        ApplyDriverFilter();
    }

    partial void OnSelectedDriverChanged(DriverInfo? value)
    {
        DriverHint = value switch
        {
            null => "Seleziona un driver per continuare.",
            { IsSupported: true } => $"{value.Name} · {value.Description}",
            _ => $"{value.Name} · driver non ancora disponibile in Navifox: arriverà in una fase successiva."
        };
    }

    private void ApplyDriverFilter()
    {
        var query = DriverSearch.Trim();
        var category = SelectedCategory?.Name ?? DriverCatalog.All;
        var drivers = DriverCatalog.Drivers.Where(driver =>
            (category == DriverCatalog.All || driver.Categories.Contains(category))
            && (query.Length == 0 || driver.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || driver.Description.Contains(query, StringComparison.OrdinalIgnoreCase)));
        drivers = SortByName
            ? drivers.OrderBy(driver => driver.Name, StringComparer.OrdinalIgnoreCase)
            : drivers.OrderByDescending(driver => driver.IsSupported).ThenByDescending(driver => driver.Popularity);
        var selected = SelectedDriver;
        VisibleDrivers.Clear();
        foreach (var driver in drivers) VisibleDrivers.Add(driver);
        if (selected is not null && VisibleDrivers.Contains(selected)) SelectedDriver = selected;
        OnPropertyChanged(nameof(HasNoDrivers));
    }

    [RelayCommand]
    private void ClearDriverSearch() => DriverSearch = string.Empty;

    public void StartWithDriver(string driverName)
    {
        if (DriverCatalog.Drivers.FirstOrDefault(d => d.Name == driverName && d.IsSupported) is not { } driver) return;
        SelectedDriver = driver;
        Next();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        if (SelectedDriver is not { Kind: { } kind } driver) return;
        var previousDriverName = _appliedDriverName;
        SelectedKind = kind;
        PortText = driver.DefaultPort.ToString();
        SelectedSslMode = driver.DefaultSslMode;
        if (string.IsNullOrWhiteSpace(UserName) || UserName == _appliedDefaultUser) UserName = driver.DefaultUser ?? string.Empty;
        if (Name is "Nuova connessione" || Name == previousDriverName) Name = driver.Name;
        _appliedDriverName = driver.Name;
        _appliedDefaultUser = driver.DefaultUser;
        StatusMessage = string.Empty;
        Step = 1;
    }

    private bool CanGoNext() => IsDriverStep && SelectedDriver?.IsSupported == true;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step = 0;

    private bool CanGoBack() => IsDetailsStep;

    private bool CanFinish() => IsDetailsStep;

    private string? _appliedDriverName;
    private string? _appliedDefaultUser;

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private async Task TestAsync(CancellationToken cancellationToken)
    {
        if (!TryBuildProfile(out var profile)) return;
        IsTesting = true;
        ConnectionSucceeded = false;
        StatusMessage = "Connessione in corso…";
        try
        {
            await databaseAccess.TestConnectionAsync(profile, cancellationToken);
            ConnectionSucceeded = true;
            StatusMessage = "Connessione riuscita.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Test annullato.";
        }
        catch (Exception exception)
        {
            var message = Redact(exception.Message, Password, SshPassword, SshPrivateKeyPassphrase, ConnectionStringOverride);
            StatusMessage = $"Connessione non riuscita ({exception.GetType().Name}): {message}";
        }
        finally { IsTesting = false; }
    }

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Save()
    {
        if (TryBuildProfile(out var profile)) CloseRequested?.Invoke(profile);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private bool TryBuildProfile(out ConnectionProfile profile)
    {
        profile = new ConnectionProfile();
        if (string.IsNullOrWhiteSpace(Name) || (string.IsNullOrWhiteSpace(Host) && string.IsNullOrWhiteSpace(ConnectionStringOverride)))
        {
            StatusMessage = IsFileBased ? "Inserisci un nome e scegli il file del database." : "Inserisci un nome e un host o una stringa di connessione.";
            return false;
        }
        if (!int.TryParse(PortText, out var port) || port is < 0 or > 65535)
        {
            StatusMessage = "La porta deve essere un numero tra 1 e 65535 (oppure 0 per il valore predefinito).";
            return false;
        }
        if (string.IsNullOrWhiteSpace(UserName) && string.IsNullOrWhiteSpace(ConnectionStringOverride) && SelectedKind is DatabaseKind.MySql or DatabaseKind.PostgreSql)
        {
            StatusMessage = "Inserisci il nome utente del database.";
            return false;
        }
        if (UseSshTunnel)
        {
            if (string.IsNullOrWhiteSpace(SshHost) || string.IsNullOrWhiteSpace(SshUserName) || string.IsNullOrWhiteSpace(SshHostKeyFingerprint))
            {
                StatusMessage = "Per il tunnel SSH servono host, utente e impronta SHA-256 della chiave host.";
                return false;
            }
            if (!int.TryParse(SshPortText, out var sshPort) || sshPort is < 1 or > 65535)
            {
                StatusMessage = "La porta SSH deve essere compresa tra 1 e 65535.";
                return false;
            }
            if (SshUsePrivateKey && !File.Exists(SshPrivateKeyPath))
            {
                StatusMessage = "Il file della chiave privata SSH non esiste.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(ConnectionStringOverride))
            {
                StatusMessage = "La stringa di connessione personalizzata non può essere combinata con il tunnel SSH.";
                return false;
            }
            profile = BuildProfile(port, sshPort);
            return true;
        }
        profile = BuildProfile(port, 22);
        return true;
    }

    private ConnectionProfile BuildProfile(int port, int sshPort) => new()
    {
        Id = Editing?.Id ?? Guid.NewGuid(),
        Name = Name.Trim(), Folder = MainWindowViewModel.NormalizeFolder(Folder), IsFavorite = IsFavorite,
        Environment = Environment ?? string.Empty, Color = string.IsNullOrEmpty(Color) ? null : Color,
        Kind = SelectedKind, Driver = SelectedDriver?.Name, Host = Host.Trim(), Port = port, UserName = UserName.Trim(), Password = Password,
        DefaultDatabase = Database.Trim(), SslMode = SelectedSslMode, AdvancedParameters = AdvancedParameters,
        ConnectionStringOverride = ConnectionStringOverride.Trim(), UseSshTunnel = UseSshTunnel,
        SshHost = SshHost.Trim(), SshPort = sshPort, SshUserName = SshUserName.Trim(), SshPassword = SshPassword,
        SshUsePrivateKey = SshUsePrivateKey, SshPrivateKeyPath = SshPrivateKeyPath.Trim(),
        SshPrivateKeyPassphrase = SshPrivateKeyPassphrase, SshHostKeyFingerprint = SshHostKeyFingerprint.Trim()
    };

    private static string Redact(string message, params string[] secrets)
    {
        foreach (var secret in secrets.Where(value => !string.IsNullOrEmpty(value)))
            message = message.Replace(secret, "••••", StringComparison.Ordinal);
        return message;
    }
}
