namespace RpgTimeTracker.Shared.Models.Rpc;

/// <summary>
///     Protokoll-Version, ausgetauscht in session.hello/session.helloRejected (siehe RpcMethods) -
///     erhöhen, wenn sich das Wire-Format so ändert, dass ein älterer Client/Host es nicht mehr
///     korrekt verarbeiten könnte. Ein Host lehnt eine Verbindung ab, statt sie mit einer
///     abweichenden Version stillschweigend zu akzeptieren - das ergibt eine klare Fehlermeldung
///     statt undefiniertem Verhalten, sobald mehrere Client-/Host-Builds im Umlauf sind.
/// </summary>
public static class ProtocolInfo
{
    public const int Version = 1;
}
