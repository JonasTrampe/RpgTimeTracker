using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace RpgTimeTracker.PlayerClient.Network;

/// <summary>
///     Über welche(n) Mechanismus/Mechanismen ein Server gefunden wurde - derselbe Host:Port
///     kann über beide Wege antworten, siehe MdnsDiscoveryService.DiscoverAsync (ein Eintrag pro
///     Host:Port, Quellen werden zusammengeführt statt den vorherigen Fund zu überschreiben).
/// </summary>
[Flags]
public enum DiscoverySource
{
    None = 0,
    Lan = 1,
    Mdns = 2
}

public sealed class DiscoveredRpgTimeTrackerServer
{
    public string Name { get; set; } = "RpgTimeTracker";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public DiscoverySource Source { get; set; }
    public string Display => string.IsNullOrWhiteSpace(Host) ? Name : $"{Name} ({Host}:{Port})";

    public string SourceLabel => Source switch
    {
        DiscoverySource.Lan | DiscoverySource.Mdns => "LAN + mDNS",
        DiscoverySource.Lan => "LAN",
        DiscoverySource.Mdns => "mDNS",
        _ => string.Empty
    };
}

public sealed class MdnsDiscoveryService
{
    private const int MdnsPort = 5353;
    private const int LanDiscoveryPort = 48551;
    private const string ServiceType = "_rpg-time-tracker._tcp.local";
    private const string Probe = "RPG_TIME_TRACKER_DISCOVER_V1";

    public async Task<IReadOnlyList<DiscoveredRpgTimeTrackerServer>> DiscoverAsync(TimeSpan timeout)
    {
        var results = new Dictionary<string, DiscoveredRpgTimeTrackerServer>(StringComparer.OrdinalIgnoreCase);

        await DiscoverViaLanBroadcastAsync(results, timeout).ConfigureAwait(false);
        await DiscoverViaMdnsAsync(results, timeout).ConfigureAwait(false);

        return MergeByPort(results.Values);
    }

    /// <summary>
    ///     Zweite, gröbere Zusammenführung über den Port: host:port ist der primäre Schlüssel beim
    ///     Sammeln (siehe DiscoverViaLanBroadcastAsync/DiscoverViaMdnsAsync), aber mDNS und
    ///     LAN-Broadcast können auf Rechnern mit mehreren Netzwerkadaptern trotzdem noch
    ///     unterschiedliche lokale IPs für denselben Host ermitteln. Da pro LAN realistisch nur ein
    ///     Host läuft, reicht "gleicher Port" als Kriterium für "derselbe Server" - Quellen werden
    ///     auch hier zusammengeführt statt eine der beiden Antworten zu verwerfen.
    /// </summary>
    private static IReadOnlyList<DiscoveredRpgTimeTrackerServer> MergeByPort(
        IEnumerable<DiscoveredRpgTimeTrackerServer> servers)
    {
        var merged = new Dictionary<int, DiscoveredRpgTimeTrackerServer>();

        foreach (var server in servers)
            if (merged.TryGetValue(server.Port, out var existing))
            {
                existing.Source |= server.Source;
                if (string.IsNullOrWhiteSpace(existing.Host)) existing.Host = server.Host;
            }
            else
            {
                merged[server.Port] = server;
            }

        return merged.Values.ToList();
    }

    private static async Task DiscoverViaLanBroadcastAsync(Dictionary<string, DiscoveredRpgTimeTrackerServer> results,
        TimeSpan timeout)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var bytes = Encoding.UTF8.GetBytes(Probe);
            await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, LanDiscoveryPort))
                .ConfigureAwait(false);

            using var cts = new CancellationTokenSource(timeout);
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                try
                {
                    if (received.Buffer.Length > 4096) continue;
                    var json = Encoding.UTF8.GetString(received.Buffer);
                    var dto = JsonSerializer.Deserialize<LanDiscoveryResponse>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (dto is null ||
                        dto.Version != 1 ||
                        !string.Equals(dto.AppName, "RpgTimeTracker", StringComparison.Ordinal) ||
                        dto.Port is <= 0 or > 65535)
                        continue;

                    var host = string.IsNullOrWhiteSpace(dto.Host)
                        ? received.RemoteEndPoint.Address.ToString()
                        : dto.Host;

                    var key = $"{host}:{dto.Port}";
                    if (results.TryGetValue(key, out var existingLan))
                        existingLan.Source |= DiscoverySource.Lan;
                    else
                        results[key] = new DiscoveredRpgTimeTrackerServer
                        {
                            Name = string.IsNullOrWhiteSpace(dto.Name) ? "RpgTimeTracker" : Limit(dto.Name, 80),
                            Host = Limit(host, 120),
                            Port = dto.Port,
                            Source = DiscoverySource.Lan
                        };
                    Log.Debug("LAN-Broadcast-Antwort: {Host}:{Port}", host, dto.Port);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "LAN-Broadcast-Paket ignoriert");
                }
            }
        }
        catch (Exception ex)
        {
            // Broadcast discovery is optional.
            Log.Warning(ex, "LAN-Broadcast-Discovery fehlgeschlagen");
        }
    }

    private static async Task DiscoverViaMdnsAsync(Dictionary<string, DiscoveredRpgTimeTrackerServer> results,
        TimeSpan timeout)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.ExclusiveAddressUse = false;
            // The responder replies to the mDNS multicast group (224.0.0.251:5353), not
            // back to our unicast source port - without joining the group here, the OS
            // never delivers that reply and discovered servers never show up.
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            udp.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"));

            var query = BuildPtrQuery(ServiceType);
            await udp.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), MdnsPort))
                .ConfigureAwait(false);

            using var cts = new CancellationTokenSource(timeout);
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                foreach (var server in TryParseResponse(received.Buffer, received.RemoteEndPoint.Address))
                {
                    if (server.Port <= 0 || string.IsNullOrWhiteSpace(server.Host)) continue;

                    var key = $"{server.Host}:{server.Port}";
                    // Derselbe Server kann bereits über LAN-Broadcast gefunden worden sein - dann
                    // nur die Quelle ergänzen statt den bestehenden Eintrag (samt seiner Quelle)
                    // zu überschreiben, damit "LAN + mDNS" sichtbar bleibt statt nur "mDNS".
                    if (results.TryGetValue(key, out var existing))
                    {
                        existing.Source |= DiscoverySource.Mdns;
                    }
                    else
                    {
                        server.Source = DiscoverySource.Mdns;
                        results[key] = server;
                    }

                    Log.Debug("mDNS-Antwort: {Host}:{Port}", server.Host, server.Port);
                }
            }
        }
        catch (Exception ex)
        {
            // mDNS discovery is optional.
            Log.Warning(ex, "mDNS-Discovery fehlgeschlagen");
        }
    }

    private static IEnumerable<DiscoveredRpgTimeTrackerServer> TryParseResponse(byte[] packet,
        IPAddress fallbackAddress)
    {
        var servers = new List<DiscoveredRpgTimeTrackerServer>();

        try
        {
            if (packet.Length < 12 || packet.Length > 64 * 1024) return servers;

            var reader = new DnsReader(packet);
            reader.Skip(4);
            var qd = reader.ReadUInt16();
            var an = reader.ReadUInt16();
            var ns = reader.ReadUInt16();
            var ar = reader.ReadUInt16();

            for (var i = 0; i < qd; i++)
            {
                reader.ReadName();
                reader.Skip(4);
            }

            string? instance = null;
            string? host = null;
            string? txtName = null;
            var port = 0;
            IPAddress? address = null;
            var totalRecords = an + ns + ar;

            for (var i = 0; i < totalRecords && !reader.End; i++)
            {
                var name = reader.ReadName();
                var type = reader.ReadUInt16();
                reader.Skip(2);
                reader.Skip(4);
                var rdLength = reader.ReadUInt16();
                var rdataStart = reader.Position;

                if (type == 12)
                {
                    var target = reader.ReadName();
                    if (target.Contains("_rpg-time-tracker", StringComparison.OrdinalIgnoreCase)) instance = target;
                }
                else if (type == 33)
                {
                    reader.Skip(4);
                    port = reader.ReadUInt16();
                    host = reader.ReadName();
                    instance ??= name;
                }
                else if (type == 1 && rdLength == 4)
                {
                    address = new IPAddress(reader.ReadBytes(4));
                    if (string.Equals(name, host, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(host)) host = address.ToString();
                }
                else if (type == 16)
                {
                    var parsed = ReadTxtRecordName(packet, rdataStart, rdLength);
                    if (!string.IsNullOrWhiteSpace(parsed)) txtName = parsed;
                }

                reader.Position = Math.Min(packet.Length, rdataStart + rdLength);
            }

            if (port > 0)
                servers.Add(new DiscoveredRpgTimeTrackerServer
                {
                    Name = !string.IsNullOrWhiteSpace(txtName)
                        ? Limit(txtName, 80)
                        : string.IsNullOrWhiteSpace(instance)
                            ? "RpgTimeTracker"
                            : Limit(instance.Split('.')[0], 80),
                    Host = address?.ToString() ?? fallbackAddress.ToString(),
                    Port = port
                });
        }
        catch
        {
            // Rogue mDNS packet: ignore.
        }

        return servers;
    }

    /// <summary>
    ///     TXT-RDATA ist eine Folge längenpräfixierter Strings (siehe PlayerMdnsAnnouncer.WriteTxt) -
    ///     sucht darin den ersten Eintrag der Form "name=..." und gibt den Wert dahinter zurück.
    /// </summary>
    private static string? ReadTxtRecordName(byte[] packet, int rdataStart, int rdLength)
    {
        var pos = rdataStart;
        var end = Math.Min(packet.Length, rdataStart + rdLength);

        while (pos < end)
        {
            var length = packet[pos];
            pos++;
            if (length == 0 || pos + length > end) break;

            var text = Encoding.UTF8.GetString(packet, pos, length);
            pos += length;

            if (text.StartsWith("name=", StringComparison.OrdinalIgnoreCase)) return text["name=".Length..];
        }

        return null;
    }

    private static byte[] BuildPtrQuery(string serviceType)
    {
        var writer = new DnsWriter();
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);
        writer.WriteUInt16(1);
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);
        writer.WriteName(serviceType);
        writer.WriteUInt16(12);
        writer.WriteUInt16(1);
        return writer.ToArray();
    }

    private static string Limit(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed class LanDiscoveryResponse
    {
        public string AppName { get; set; } = "RpgTimeTracker";
        public int Version { get; set; }
        public string Name { get; set; } = "RpgTimeTracker";
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    private sealed class DnsReader
    {
        private readonly byte[] _data;

        public DnsReader(byte[] data)
        {
            _data = data;
        }

        public int Position { get; set; }
        public bool End => Position >= _data.Length;

        public ushort ReadUInt16()
        {
            if (Position + 2 > _data.Length) throw new InvalidOperationException();
            var value = (ushort)((_data[Position] << 8) | _data[Position + 1]);
            Position += 2;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            if (Position + count > _data.Length) throw new InvalidOperationException();
            var bytes = _data.Skip(Position).Take(count).ToArray();
            Position += count;
            return bytes;
        }

        public void Skip(int count)
        {
            Position = Math.Min(_data.Length, Position + count);
        }

        public string ReadName()
        {
            var labels = new List<string>();
            var jumped = false;
            var jumpReturn = 0;
            var loops = 0;

            while (Position < _data.Length && loops++ < 64)
            {
                var length = _data[Position++];

                if (length == 0) break;

                if ((length & 0xC0) == 0xC0)
                {
                    if (Position >= _data.Length) break;
                    var pointer = ((length & 0x3F) << 8) | _data[Position++];
                    if (!jumped) jumpReturn = Position;
                    Position = pointer;
                    jumped = true;
                    continue;
                }

                if (Position + length > _data.Length) break;
                labels.Add(Encoding.ASCII.GetString(_data, Position, length));
                Position += length;
            }

            if (jumped) Position = jumpReturn;
            return string.Join(".", labels);
        }
    }

    private sealed class DnsWriter
    {
        private readonly List<byte> _buffer = new();

        public byte[] ToArray()
        {
            return _buffer.ToArray();
        }

        public void WriteUInt16(ushort value)
        {
            _buffer.Add((byte)(value >> 8));
            _buffer.Add((byte)value);
        }

        public void WriteName(string name)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                _buffer.Add((byte)bytes.Length);
                _buffer.AddRange(bytes);
            }

            _buffer.Add(0);
        }
    }
}