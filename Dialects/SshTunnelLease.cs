using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Data.Common;
using MosaicDB.Core.Models;
using Renci.SshNet;

namespace MosaicDB.Dialects;

public sealed class SshTunnelLease : IDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _forward;
    private bool _disposed;

    private SshTunnelLease(SshClient client, ForwardedPortLocal forward)
    {
        _client = client;
        _forward = forward;
    }

    public int LocalPort => checked((int)_forward.BoundPort);

    public static async Task<SshTunnelLease> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.SshHostKeyFingerprint))
            throw new InvalidOperationException("Inserisci l’impronta SHA-256 della chiave host SSH per verificare il server.");
        if (profile.SshUsePrivateKey && string.IsNullOrWhiteSpace(profile.SshPrivateKeyPath))
            throw new InvalidOperationException("Seleziona il file della chiave privata SSH.");

        AuthenticationMethod authentication = profile.SshUsePrivateKey
            ? new PrivateKeyAuthenticationMethod(profile.SshUserName, new PrivateKeyFile(profile.SshPrivateKeyPath, profile.SshPrivateKeyPassphrase))
            : new PasswordAuthenticationMethod(profile.SshUserName, profile.SshPassword);
        var connectionInfo = new ConnectionInfo(profile.SshHost, profile.SshPort, profile.SshUserName, authentication)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        var client = new SshClient(connectionInfo);
        var expectedFingerprint = NormalizeFingerprint(profile.SshHostKeyFingerprint);
        client.HostKeyReceived += (_, args) =>
        {
            var actualFingerprint = Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=');
            args.CanTrust = string.Equals(actualFingerprint, expectedFingerprint, StringComparison.Ordinal);
        };

        var localPort = ReserveEphemeralPort();
        var forward = new ForwardedPortLocal("127.0.0.1", localPort, profile.Host, checked((uint)profile.EffectivePort));
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                client.AddForwardedPort(forward);
                forward.Start();
            }, cancellationToken);
            return new SshTunnelLease(client, forward);
        }
        catch
        {
            forward.Dispose();
            client.Dispose();
            throw;
        }
    }

    public ConnectionProfile UseLocalEndpoint(ConnectionProfile profile) => profile with { Host = "127.0.0.1", Port = LocalPort };

    public void Attach(DbConnection connection) => connection.StateChange += OnConnectionStateChanged;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_forward.IsStarted) _forward.Stop();
        if (_client.IsConnected) _client.Disconnect();
        _forward.Dispose();
        _client.Dispose();
    }

    private void OnConnectionStateChanged(object? sender, System.Data.StateChangeEventArgs args)
    {
        if (args.CurrentState == System.Data.ConnectionState.Closed) Dispose();
    }

    private static uint ReserveEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return checked((uint)port);
    }

    private static string NormalizeFingerprint(string fingerprint)
    {
        var normalized = fingerprint.Trim();
        if (normalized.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) normalized = normalized[7..];
        return normalized.TrimEnd('=');
    }
}
