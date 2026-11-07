using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Data.Common;
using Navifox.Core.Models;
using Renci.SshNet;

namespace Navifox.Dialects;

/// <summary>
/// Uso di un tunnel SSH. Il tunnel vero è condiviso tra tutte le connessioni dello stesso profilo (stessa porta locale,
/// quindi anche il pool di connessioni ADO.NET funziona) e resta aperto 2 minuti dopo l'ultimo utilizzo.
/// </summary>
public sealed class SshTunnelLease : IDisposable
{
    private static readonly ConcurrentDictionary<string, SharedTunnel> Tunnels = new();
    private static readonly SemaphoreSlim OpenLock = new(1, 1);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly SharedTunnel _tunnel;
    private int _disposed;

    private SshTunnelLease(SharedTunnel tunnel) => _tunnel = tunnel;

    public int LocalPort => _tunnel.LocalPort;

    public static async Task<SshTunnelLease> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.SshHostKeyFingerprint))
            throw new InvalidOperationException("Inserisci l’impronta SHA-256 della chiave host SSH per verificare il server.");
        if (profile.SshUsePrivateKey && string.IsNullOrWhiteSpace(profile.SshPrivateKeyPath))
            throw new InvalidOperationException("Seleziona il file della chiave privata SSH.");

        var key = KeyOf(profile);
        await OpenLock.WaitAsync(cancellationToken);
        try
        {
            if (Tunnels.TryGetValue(key, out var existing) && existing.IsAlive)
            {
                existing.Acquire();
                return new SshTunnelLease(existing);
            }
            existing?.Close();
            var tunnel = await SharedTunnel.OpenAsync(profile, cancellationToken);
            tunnel.Acquire();
            Tunnels[key] = tunnel;
            return new SshTunnelLease(tunnel);
        }
        finally { OpenLock.Release(); }
    }

    /// <summary>Chiude tutti i tunnel aperti (es. alla chiusura dell'applicazione).</summary>
    public static void CloseAll()
    {
        foreach (var tunnel in Tunnels.Values) tunnel.Close();
        Tunnels.Clear();
    }

    private static string KeyOf(ConnectionProfile p) =>
        string.Join('\u001f', p.SshHost, p.SshPort, p.SshUserName, p.SshUsePrivateKey, p.SshPrivateKeyPath, p.Host, p.EffectivePort,
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.SshPassword + "\u001f" + p.SshPrivateKeyPassphrase + "\u001f" + p.SshHostKeyFingerprint))));

    public ConnectionProfile UseLocalEndpoint(ConnectionProfile profile) => profile with { Host = "127.0.0.1", Port = LocalPort };

    public void Attach(DbConnection connection) => connection.StateChange += OnConnectionStateChanged;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _tunnel.Release(IdleTimeout);
    }

    private void OnConnectionStateChanged(object? sender, System.Data.StateChangeEventArgs args)
    {
        if (args.CurrentState == System.Data.ConnectionState.Closed) Dispose();
    }

    /// <summary>Client SSH + porta inoltrata condivisi, con conteggio degli utilizzi.</summary>
    private sealed class SharedTunnel
    {
        private readonly SshClient _client;
        private readonly ForwardedPortLocal _forward;
        private int _users;
        private Timer? _idle;
        private bool _closed;

        private SharedTunnel(SshClient client, ForwardedPortLocal forward)
        {
            _client = client;
            _forward = forward;
        }

        public int LocalPort => checked((int)_forward.BoundPort);
        public bool IsAlive => !_closed && _client.IsConnected && _forward.IsStarted;

        public static async Task<SharedTunnel> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            AuthenticationMethod authentication = profile.SshUsePrivateKey
                ? new PrivateKeyAuthenticationMethod(profile.SshUserName, new PrivateKeyFile(profile.SshPrivateKeyPath, profile.SshPrivateKeyPassphrase))
                : new PasswordAuthenticationMethod(profile.SshUserName, profile.SshPassword);
            var connectionInfo = new ConnectionInfo(profile.SshHost, profile.SshPort, profile.SshUserName, authentication)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            var client = new SshClient(connectionInfo) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
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
                return new SharedTunnel(client, forward);
            }
            catch
            {
                forward.Dispose();
                client.Dispose();
                throw;
            }
        }

        public void Acquire()
        {
            lock (this)
            {
                _users++;
                _idle?.Dispose();
                _idle = null;
            }
        }

        public void Release(TimeSpan idleTimeout)
        {
            lock (this)
            {
                if (--_users > 0 || _closed) return;
                _idle?.Dispose();
                _idle = new Timer(_ =>
                {
                    lock (this)
                    {
                        if (_users == 0) Close();
                    }
                }, null, idleTimeout, Timeout.InfiniteTimeSpan);
            }
        }

        public void Close()
        {
            lock (this)
            {
                if (_closed) return;
                _closed = true;
                _idle?.Dispose();
                try
                {
                    if (_forward.IsStarted) _forward.Stop();
                    if (_client.IsConnected) _client.Disconnect();
                }
                catch { /* chiusura comunque */ }
                _forward.Dispose();
                _client.Dispose();
            }
        }
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
