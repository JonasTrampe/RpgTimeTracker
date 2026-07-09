using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace RpgTimeTracker.Network;

/// <summary>
///     Simple LAN broadcast responder used as a robust fallback where mDNS is blocked or unsupported.
///     Read-only: replies only with host/port metadata for the player display server.
/// </summary>
public sealed class LanDiscoveryResponder : IDisposable
{
    public const int DiscoveryPort = 48551;
    private const string Probe = "RPG_TIME_TRACKER_DISCOVER_V1";
    private readonly string _serverName;

    private readonly int _serverPort;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private UdpClient? _udp;

    public LanDiscoveryResponder(int serverPort, string serverName = "RpgTimeTracker")
    {
        _serverPort = serverPort;
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "RpgTimeTracker" : serverName;
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _udp?.Dispose();
        }
        catch
        {
        }

        _cts?.Dispose();
    }

    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.EnableBroadcast = true;
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            Log.Information("LAN-Discovery-Responder gestartet auf UDP-Port {DiscoveryPort}", DiscoveryPort);

            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    continue;
                }

                string message;
                try
                {
                    message = Encoding.UTF8.GetString(result.Buffer);
                }
                catch
                {
                    continue;
                }

                if (!string.Equals(message.Trim(), Probe, StringComparison.Ordinal)) continue;

                var payload = new LanDiscoveryResponse
                {
                    AppName = "RpgTimeTracker",
                    Version = 1,
                    Name = _serverName,
                    Host = GetLocalAddressFor(result.RemoteEndPoint.Address) ??
                           result.RemoteEndPoint.Address.ToString(),
                    Port = _serverPort
                };

                var json = JsonSerializer.Serialize(payload);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint).ConfigureAwait(false);
                Log.Debug("LAN-Discovery-Anfrage von {RemoteEndPoint} beantwortet ({Host}:{Port})",
                    result.RemoteEndPoint, payload.Host, payload.Port);
            }
        }
        catch (Exception ex)
        {
            // Discovery is optional. Manual connection still works.
            Log.Warning(ex,
                "LAN-Discovery-Responder nicht verfügbar (Port {DiscoveryPort} evtl. belegt) - manuelle Verbindung funktioniert weiterhin",
                DiscoveryPort);
        }
    }

    private static string? GetLocalAddressFor(IPAddress remoteAddress)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(remoteAddress, 9));
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    private sealed class LanDiscoveryResponse
    {
        public string AppName { get; set; } = "RpgTimeTracker";
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "RpgTimeTracker";
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
    }
}