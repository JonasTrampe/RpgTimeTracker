namespace RpgTimeTracker.Shared.Models.Network;

/// <summary>
///     Gemeinsame Medien-Obergrenze für Host UND Client. Muss an EINER Stelle stehen: wäre sie in
///     beiden Projekten separat definiert, könnten sie auseinanderlaufen - der Host würde ein
///     größeres Medium akzeptieren/versenden, das der Client dann still verwirft (media.begin mit
///     TotalLength über seinem eigenen, kleineren Limit wird kommentarlos ignoriert), ohne dass
///     irgendwo eine Fehlermeldung entsteht.
/// </summary>
public static class MediaLimits
{
    /// <summary>
    ///     Knapp unter 2 GiB statt exakt 2*1024^3: die Datei liegt komplett als byte[] im Speicher
    ///     (int-indiziert), ein .NET-Array kann also ohnehin nie mehr als int.MaxValue (~2,147 GB)
    ///     Elemente fassen.
    /// </summary>
    public const long MaxMediaBytes = 1900L * 1024 * 1024;
}