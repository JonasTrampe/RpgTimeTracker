using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RpgTimeTracker.Shared.Services.Network;

/// <summary>
///     Längenpräfixiertes Binär-Frame für die TCP-Verbindung zwischen SL-App und Spieler-Client:
///     [1 Byte Frame-Typ][4 Bytes Payload-Länge, big-endian][Payload].
/// </summary>
public static class NetworkFrame
{
    /// <summary>Payload = UTF8-JSON einer RpcNotification (siehe RpgTimeTracker.Shared.Services.Rpc).</summary>
    public const byte TypeRpc = 1;

    /// <summary>
    ///     Payload = roher Ausschnitt der aktuell gestreamten Mediendatei (siehe media.begin-RPC
    ///     für Metadaten/Gesamtlänge). Chunks kommen in Reihenfolge, kein eigener Header nötig,
    ///     da pro Verbindung immer nur ein Medium gleichzeitig läuft.
    /// </summary>
    public const byte TypeMediaChunk = 2;

    private const int HeaderLength = 5;

    public static async Task WriteAsync(Stream stream, byte frameType, ReadOnlyMemory<byte> payload,
        CancellationToken token = default)
    {
        var header = new byte[HeaderLength];
        header[0] = frameType;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)payload.Length);

        await stream.WriteAsync(header, token).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Liest genau ein Frame. Gibt null zurück, wenn die Verbindung sauber geschlossen wurde
    ///     oder ein Frame die Längengrenze überschreitet (dann sollte die Verbindung getrennt werden).
    /// </summary>
    /// <param name="onDataReceived">
    ///     Optional: wird nach jedem Teil-Read aufgerufen (auch mitten in einem großen Media-Frame).
    ///     Damit kann ein Aufrufer einen Idle-Timeout auf "letzte Aktivität auf der Leitung" statt
    ///     nur "letztes vollständiges Frame" basieren - sonst würde ein großes, aber gesund
    ///     laufendes Video einen Idle-Watchdog fälschlich auslösen.
    /// </param>
    public static async Task<(byte Type, byte[] Payload)?> ReadAsync(Stream stream, int maxPayloadLength,
        CancellationToken token, Action? onDataReceived = null)
    {
        var header = new byte[HeaderLength];
        if (!await ReadExactAsync(stream, header, token, onDataReceived).ConfigureAwait(false)) return null;

        var type = header[0];
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        if (length > (uint)maxPayloadLength) return null;

        var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, token, onDataReceived).ConfigureAwait(false))
            return null;

        return (type, payload);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token,
        Action? onDataReceived)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read <= 0) return false;
            offset += read;
            onDataReceived?.Invoke();
        }

        return true;
    }
}