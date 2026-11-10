namespace Navifox.Core.Models;

public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Nuova connessione";
    public string Folder { get; init; } = "Generale";
    public bool IsFavorite { get; init; }
    public DatabaseKind Kind { get; init; }
    /// <summary>Driver scelto alla creazione (es. «MariaDB», «Supabase»); null = predefinito del tipo.</summary>
    public string? Driver { get; init; }
    /// <summary>Ambiente: "" (nessuno), "Sviluppo", "Test", "Produzione". In produzione Navifox chiede conferma prima di ogni modifica.</summary>
    public string Environment { get; init; } = string.Empty;
    /// <summary>Colore della connessione (#RRGGBB) mostrato nell'albero e sulle schede; null = nessuno.</summary>
    public string? Color { get; init; }
    public bool IsProduction => Environment == "Produzione";
    /// <summary>Sola lettura: Navifox blocca qualsiasi modifica a dati e struttura (e, dove il server lo consente, apre la sessione in sola lettura).</summary>
    public bool ReadOnly { get; init; }
    public string Host { get; init; } = "localhost";
    public int Port { get; init; }
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string DefaultDatabase { get; init; } = string.Empty;
    public string SslMode { get; init; } = "Prefer";
    public string AdvancedParameters { get; init; } = string.Empty;
    public string ConnectionStringOverride { get; init; } = string.Empty;
    public bool UseSshTunnel { get; init; }
    public string SshHost { get; init; } = string.Empty;
    public int SshPort { get; init; } = 22;
    public string SshUserName { get; init; } = string.Empty;
    public string SshPassword { get; init; } = string.Empty;
    public bool SshUsePrivateKey { get; init; }
    public string SshPrivateKeyPath { get; init; } = string.Empty;
    public string SshPrivateKeyPassphrase { get; init; } = string.Empty;
    public string SshHostKeyFingerprint { get; init; } = string.Empty;

    /// <summary>«host:porta», oppure il nome del file per SQLite.</summary>
    public string Endpoint => Kind switch
    {
        DatabaseKind.Sqlite => System.IO.Path.GetFileName(Host),
        // Istanza con nome (server\ISTANZA): la porta non si usa.
        DatabaseKind.SqlServer when Host.Contains('\\') && Port is 0 or 1433 => Host,
        _ => $"{Host}:{EffectivePort}",
    };

    /// <summary>
    /// Vero se questa connessione e <paramref name="other"/> raggiungono lo stesso database fisico
    /// (stesso tipo, server e porta, stesso database/schema): due connessioni diverse verso lo stesso server sono la stessa destinazione.
    /// </summary>
    public bool IsSameDatabase(string? database, string? schema, ConnectionProfile other, string? otherDatabase, string? otherSchema)
    {
        if (Kind != other.Kind) return false;
        if (Id == other.Id) return string.Equals(database, otherDatabase, StringComparison.Ordinal) && string.Equals(schema, otherSchema, StringComparison.Ordinal);
        if (Kind == DatabaseKind.Sqlite)
            return string.Equals(System.IO.Path.GetFullPath(System.Environment.ExpandEnvironmentVariables(Host.Trim().Trim('"'))),
                System.IO.Path.GetFullPath(System.Environment.ExpandEnvironmentVariables(other.Host.Trim().Trim('"'))), StringComparison.OrdinalIgnoreCase);
        if (UseSshTunnel || other.UseSshTunnel)
        {
            // Dietro un tunnel conta il server raggiunto attraverso l'host SSH.
            if (!string.Equals(SshHost.Trim(), other.SshHost.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        var names = Kind is DatabaseKind.MySql or DatabaseKind.SqlServer ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return string.Equals(NormalizeHost(Host), NormalizeHost(other.Host), StringComparison.OrdinalIgnoreCase)
               && EffectivePort == other.EffectivePort
               && names.Equals(database ?? string.Empty, otherDatabase ?? string.Empty)
               && names.Equals(schema ?? string.Empty, otherSchema ?? string.Empty);
    }

    private static string NormalizeHost(string host)
    {
        var value = host.Trim().TrimEnd('.');
        return value is "127.0.0.1" or "::1" or "[::1]" or "." or "(local)" ? "localhost" : value;
    }

    public int EffectivePort => Port > 0 ? Port : Kind switch
    {
        DatabaseKind.MySql => 3306,
        DatabaseKind.PostgreSql => 5432,
        DatabaseKind.SqlServer => 1433,
        DatabaseKind.Sqlite => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
    };
}
