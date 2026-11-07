using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.Dialects;

public sealed class ConnectionProfileStore : IConnectionProfileStore
{
    // NON modificare: è la chiave aggiuntiva con cui sono cifrate le password già salvate (nome storico del progetto).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Mosaic" + "DB/profile-secret/v1");
    private readonly string _filePath = Navifox.Core.AppPaths.File("connections.json");

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<ConnectionProfile>();
        await using var stream = File.OpenRead(_filePath);
        var records = await JsonSerializer.DeserializeAsync(stream, ConnectionStoreJsonContext.Default.ListStoredConnectionRecord, cancellationToken) ?? [];
        return records.Select(record => record.ToProfile(
            Unprotect(record.EncryptedPassword),
            Unprotect(record.EncryptedConnectionString),
            Unprotect(record.EncryptedSshPassword),
            Unprotect(record.EncryptedSshPrivateKeyPassphrase))).ToArray();
    }

    public async Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var records = profiles.Select(profile => StoredConnectionRecord.FromProfile(
            profile, Protect(profile.Password), Protect(profile.ConnectionStringOverride), Protect(profile.SshPassword),
            Protect(profile.SshPrivateKeyPassphrase))).ToList();
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, records, ConnectionStoreJsonContext.Default.ListStoredConnectionRecord, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, _filePath, true);
    }

    private static string Protect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("I segreti dei profili richiedono DPAPI su Windows.");
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("La decifratura dei profili richiede DPAPI su Windows.");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
    }
}

public sealed record StoredConnectionRecord(
    Guid Id,
    string Name,
    string Folder,
    bool IsFavorite,
    DatabaseKind Kind,
    string Host,
    int Port,
    string UserName,
    string EncryptedPassword,
    string DefaultDatabase,
    string SslMode,
    string AdvancedParameters,
    string EncryptedConnectionString,
    bool UseSshTunnel,
    string SshHost,
    int SshPort,
    string SshUserName,
    string EncryptedSshPassword,
    bool SshUsePrivateKey,
    string SshPrivateKeyPath,
    string EncryptedSshPrivateKeyPassphrase,
    string SshHostKeyFingerprint,
    string? Driver = null)
{
    public static StoredConnectionRecord FromProfile(ConnectionProfile p, string password, string connectionString, string sshPassword, string sshPassphrase) => new(
        p.Id, p.Name, p.Folder, p.IsFavorite, p.Kind, p.Host, p.Port, p.UserName, password, p.DefaultDatabase,
        p.SslMode, p.AdvancedParameters, connectionString, p.UseSshTunnel, p.SshHost, p.SshPort, p.SshUserName,
        sshPassword, p.SshUsePrivateKey, p.SshPrivateKeyPath, sshPassphrase, p.SshHostKeyFingerprint, p.Driver);

    public ConnectionProfile ToProfile(string password, string connectionString, string sshPassword, string sshPassphrase) => new()
    {
        Id = Id, Name = Name, Folder = Folder, IsFavorite = IsFavorite, Kind = Kind, Host = Host, Port = Port,
        UserName = UserName, Password = password, DefaultDatabase = DefaultDatabase, SslMode = SslMode,
        AdvancedParameters = AdvancedParameters, ConnectionStringOverride = connectionString, UseSshTunnel = UseSshTunnel,
        SshHost = SshHost, SshPort = SshPort, SshUserName = SshUserName, SshPassword = sshPassword,
        SshUsePrivateKey = SshUsePrivateKey, SshPrivateKeyPath = SshPrivateKeyPath,
        SshPrivateKeyPassphrase = sshPassphrase, SshHostKeyFingerprint = SshHostKeyFingerprint, Driver = Driver
    };
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<StoredConnectionRecord>))]
internal partial class ConnectionStoreJsonContext : JsonSerializerContext
{
}
