using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RpgTimeTracker.Shared.Services.Rpc;

/// <summary>Ausgehende Notification mit stark typisierten Params.</summary>
public sealed class RpcNotification<TParams>
{
    public string Jsonrpc { get; set; } = "2.0";
    public string Method { get; set; } = string.Empty;
    public TParams? Params { get; set; }
}

/// <summary>Eingehende Notification, Params zunächst als rohes JSON (Methodenname entscheidet den Zieltyp).</summary>
public sealed class RpcNotificationRaw
{
    public string Jsonrpc { get; set; } = "2.0";
    public string Method { get; set; } = string.Empty;
    public JsonElement Params { get; set; }
}

/// <summary>Marker für Notifications ohne Nutzdaten (z.B. clock.started).</summary>
public sealed class RpcEmptyParams
{
    public static readonly RpcEmptyParams Instance = new();
}

public static class RpcMessage
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] Serialize<TParams>(string method, TParams @params)
    {
        var envelope = new RpcNotification<TParams> { Method = method, Params = @params };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    /// <summary>Liefert null bei kaputtem/unerwartetem JSON, statt zu werfen (unvertrauenswürdige Gegenseite).</summary>
    public static RpcNotificationRaw? TryParseRaw(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<RpcNotificationRaw>(utf8Json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static TParams? GetParams<TParams>(this RpcNotificationRaw raw)
    {
        try
        {
            return raw.Params.Deserialize<TParams>(JsonOptions);
        }
        catch
        {
            return default;
        }
    }
}