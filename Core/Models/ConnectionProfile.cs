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

    public int EffectivePort => Port > 0 ? Port : Kind switch
    {
        DatabaseKind.MySql => 3306,
        DatabaseKind.PostgreSql => 5432,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
    };
}
