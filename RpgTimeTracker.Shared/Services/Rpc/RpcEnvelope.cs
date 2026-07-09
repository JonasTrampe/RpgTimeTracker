using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RpgTimeTracker.Shared.Services.Rpc;

/// <summary>Outgoing notification with strongly-typed params.</summary>
public sealed class RpcNotification<TParams>
{
    public string Jsonrpc { get; set; } = "2.0";
    public string Method { get; set; } = string.Empty;
    public TParams? Params { get; set; }
}

/// <summary>Incoming notification, params initially as raw JSON (method name determines the target type).</summary>
public sealed class RpcNotificationRaw
{
    public string Jsonrpc { get; set; } = "2.0";
    public string Method { get; set; } = string.Empty;
    public JsonElement Params { get; set; }
}

/// <summary>Marker for notifications without payload data (e.g. clock.started).</summary>
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

    /// <summary>Returns null on broken/unexpected JSON instead of throwing (untrusted remote party).</summary>
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