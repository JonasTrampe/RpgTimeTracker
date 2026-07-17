namespace RpgTimeTracker.Shared.Models.Rpc;

/// <summary>
///     Protocol version, exchanged in session.hello/session.helloRejected (see RpcMethods) -
///     increment when the wire format changes such that an older client/host could no longer
///     process it correctly. A host rejects a connection instead of silently accepting it with
///     a differing version - this results in a clear error message instead of undefined
///     behavior once multiple client/host builds are in circulation.
/// </summary>
public static class ProtocolInfo
{
    public const int Version = 8;
}