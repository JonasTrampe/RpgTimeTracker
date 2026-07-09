namespace RpgTimeTracker.Shared.Models.Network;

/// <summary>
///     Shared media upper limit for BOTH host AND client. Must live in ONE place: if it were
///     defined separately in both projects, they could drift apart - the host would
///     accept/send a larger medium that the client then silently discards (media.begin with
///     TotalLength over its own, smaller limit is ignored without comment), without any
///     error appearing anywhere.
/// </summary>
public static class MediaLimits
{
    /// <summary>
    ///     Just under 2 GiB instead of exactly 2*1024^3: the file sits entirely as a byte[] in
    ///     memory (int-indexed), so a .NET array can never hold more than int.MaxValue
    ///     (~2.147 GB) elements anyway.
    /// </summary>
    public const long MaxMediaBytes = 1900L * 1024 * 1024;
}