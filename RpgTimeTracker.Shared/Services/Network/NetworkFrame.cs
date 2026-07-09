using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RpgTimeTracker.Shared.Services.Network;

/// <summary>
///     Length-prefixed binary frame for the TCP connection between GM app and player client:
///     [1 byte frame type][4 bytes payload length, big-endian][payload].
/// </summary>
public static class NetworkFrame
{
    /// <summary>Payload = UTF8 JSON of an RpcNotification (see RpgTimeTracker.Shared.Services.Rpc).</summary>
    public const byte TypeRpc = 1;

    /// <summary>
    ///     Payload = raw chunk of the currently streamed media file (see the media.begin RPC
    ///     for metadata/total length). Chunks arrive in order, no separate header needed,
    ///     since only one medium runs at a time per connection.
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
    ///     Reads exactly one frame. Returns null if the connection was closed cleanly
    ///     or a frame exceeds the length limit (in which case the connection should be dropped).
    /// </summary>
    /// <param name="onDataReceived">
    ///     Optional: called after each partial read (even in the middle of a large media frame).
    ///     This lets a caller base an idle timeout on "last activity on the wire" instead of
    ///     just "last complete frame" - otherwise a large but healthily
    ///     running video would falsely trigger an idle watchdog.
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