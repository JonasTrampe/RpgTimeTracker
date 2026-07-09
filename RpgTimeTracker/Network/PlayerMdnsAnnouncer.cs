using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace RpgTimeTracker.Network;

/// <summary>
///     Minimal mDNS responder for _rpg-time-tracker._tcp.local.
///     It only announces the read-only player TCP service.
/// </summary>
public sealed class PlayerMdnsAnnouncer : IDisposable
{
    private const string ServiceType = "_rpg-time-tracker._tcp.local";
    private const string InstanceName = "RpgTimeTracker._rpg-time-tracker._tcp.local";
    private const string HostName = "rpg-time-tracker.local";
    private const int MdnsPort = 5353;
    private readonly string _serverName;

    private readonly int _servicePort;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private UdpClient? _udp;

    public PlayerMdnsAnnouncer(int servicePort, string serverName = "RpgTimeTracker")
    {
        _servicePort = servicePort;
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
            _udp?.DropMulticastGroup(IPAddress.Parse("224.0.0.251"));
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
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.ExclusiveAddressUse = false;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            _udp.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"));

            await SendAnnouncementAsync().ConfigureAwait(false);
            Log.Information("mDNS announcement started (port {ServicePort})", _servicePort);

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
                catch (Exception ex)
                {
                    Log.Debug(ex, "mDNS receive skipped");
                    continue;
                }

                if (IsQueryForOurService(result.Buffer)) await SendAnnouncementAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // mDNS is optional. The TCP server still works via manual connection.
            Log.Warning(ex,
                "mDNS announcement unavailable (port {MdnsPort} possibly in use) - manual connection still works",
                MdnsPort);
        }
    }

    private async Task SendAnnouncementAsync()
    {
        if (_udp is null) return;

        var packet = BuildResponsePacket();
        try
        {
            await _udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), MdnsPort))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "mDNS announcement could not be sent");
        }
    }

    private static bool IsQueryForOurService(byte[] packet)
    {
        try
        {
            var ascii = Encoding.ASCII.GetString(packet);
            return ascii.Contains("_rpg-time-tracker", StringComparison.OrdinalIgnoreCase) ||
                   ascii.Contains("_services", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private byte[] BuildResponsePacket()
    {
        var writer = new DnsWriter();
        writer.WriteUInt16(0); // transaction id
        writer.WriteUInt16(0x8400); // response + authoritative
        writer.WriteUInt16(0); // questions
        writer.WriteUInt16(4); // answers
        writer.WriteUInt16(0); // authority
        writer.WriteUInt16(0); // additional

        // PTR: _rpg-time-tracker._tcp.local -> RpgTimeTracker._rpg-time-tracker._tcp.local
        writer.WriteName(ServiceType);
        writer.WriteUInt16(12);
        writer.WriteUInt16(1);
        writer.WriteUInt32(120);
        using (writer.BeginRData())
        {
            writer.WriteName(InstanceName);
        }

        // SRV: instance -> host:port
        writer.WriteName(InstanceName);
        writer.WriteUInt16(33);
        writer.WriteUInt16(1);
        writer.WriteUInt32(120);
        using (writer.BeginRData())
        {
            writer.WriteUInt16(0); // priority
            writer.WriteUInt16(0); // weight
            writer.WriteUInt16((ushort)_servicePort);
            writer.WriteName(HostName);
        }

        // TXT
        writer.WriteName(InstanceName);
        writer.WriteUInt16(16);
        writer.WriteUInt16(1);
        writer.WriteUInt32(120);
        using (writer.BeginRData())
        {
            writer.WriteTxt("app=RpgTimeTracker");
            writer.WriteTxt("mode=readonly");
            writer.WriteTxt("version=1");
            writer.WriteTxt($"name={_serverName}");
        }

        // A record
        writer.WriteName(HostName);
        writer.WriteUInt16(1);
        writer.WriteUInt16(1);
        writer.WriteUInt32(120);
        using (writer.BeginRData())
        {
            var ip = GetBestLocalIPv4() ?? IPAddress.Loopback;
            writer.WriteBytes(ip.GetAddressBytes());
        }

        return writer.ToArray();
    }

    /// <summary>
    ///     Must return the same IP as LanDiscoveryResponder.GetLocalAddressFor, otherwise the same
    ///     host would report two different addresses via mDNS and LAN broadcast - the client would
    ///     then be unable to merge the two discoveries into the same server (see MdnsDiscoveryService).
    ///     A plain interface enumeration ("first Up/non-loopback") is not reliable enough for this:
    ///     virtual adapters (Hyper-V/WSL/Docker/VPN) are also "Up" on many machines and are often
    ///     enumerated before the real LAN adapter. Instead, as with the unicast LAN responder, a
    ///     connected UDP socket is used to determine the local address the operating system actually
    ///     chooses for outbound traffic (UDP "Connect" does not send a single packet, it only resolves
    ///     the route locally).
    /// </summary>
    /// <summary>
    ///     Public so that MainWindowViewModel can reuse the same lookup for the "server running
    ///     at address:port" display in the status bar, instead of duplicating it.
    /// </summary>
    public static IPAddress? GetBestLocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(IPAddress.Parse("8.8.8.8"), 65530);
            if (socket.LocalEndPoint is IPEndPoint { Address: var address } && !IPAddress.IsLoopback(address))
                return address;
        }
        catch
        {
            // No internet routing available (e.g. completely isolated LAN) - fallback below.
        }

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                        return ua.Address;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private sealed class DnsWriter
    {
        private readonly List<byte> _buffer = [];

        public byte[] ToArray()
        {
            return _buffer.ToArray();
        }

        public void WriteUInt16(ushort value)
        {
            _buffer.Add((byte)(value >> 8));
            _buffer.Add((byte)value);
        }

        public void WriteUInt32(uint value)
        {
            _buffer.Add((byte)(value >> 24));
            _buffer.Add((byte)(value >> 16));
            _buffer.Add((byte)(value >> 8));
            _buffer.Add((byte)value);
        }

        public void WriteBytes(byte[] bytes)
        {
            _buffer.AddRange(bytes);
        }

        public void WriteName(string name)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                if (bytes.Length > 63) continue;
                _buffer.Add((byte)bytes.Length);
                _buffer.AddRange(bytes);
            }

            _buffer.Add(0);
        }

        public void WriteTxt(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 255) return;
            _buffer.Add((byte)bytes.Length);
            _buffer.AddRange(bytes);
        }

        public RDataScope BeginRData()
        {
            var lengthPosition = _buffer.Count;
            WriteUInt16(0);
            return new RDataScope(this, lengthPosition);
        }

        public sealed class RDataScope : IDisposable
        {
            private readonly int _lengthPosition;
            private readonly int _startPosition;
            private readonly DnsWriter _writer;
            private bool _disposed;

            public RDataScope(DnsWriter writer, int lengthPosition)
            {
                _writer = writer;
                _lengthPosition = lengthPosition;
                _startPosition = lengthPosition + 2;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                var length = _writer._buffer.Count - _startPosition;
                _writer._buffer[_lengthPosition] = (byte)(length >> 8);
                _writer._buffer[_lengthPosition + 1] = (byte)length;
            }
        }
    }
}