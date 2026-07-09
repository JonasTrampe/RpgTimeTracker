using System;
using System.Collections.Generic;
using System.IO;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Shared.Services;

/// <summary>Maps file extensions to a MediaKind (image/video/audio) and a MIME type.</summary>
public static class MediaTypeHelper
{
    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp"
    };

    private static readonly Dictionary<string, string> VideoMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/x-m4v",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".mov"] = "video/quicktime",
        [".avi"] = "video/x-msvideo"
    };

    private static readonly Dictionary<string, string> AudioMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".aiff"] = "audio/aiff",
        [".aif"] = "audio/aiff",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac"
    };

    public static IReadOnlyList<string> ImageExtensions { get; } = new List<string>(ImageMimeTypes.Keys);
    public static IReadOnlyList<string> VideoExtensions { get; } = new List<string>(VideoMimeTypes.Keys);
    public static IReadOnlyList<string> AudioExtensions { get; } = new List<string>(AudioMimeTypes.Keys);

    public static bool TryGetKind(string filePath, out MediaKind kind, out string mimeType)
    {
        var ext = Path.GetExtension(filePath);

        if (ImageMimeTypes.TryGetValue(ext, out var imageMime))
        {
            kind = MediaKind.Image;
            mimeType = imageMime;
            return true;
        }

        if (VideoMimeTypes.TryGetValue(ext, out var videoMime))
        {
            kind = MediaKind.Video;
            mimeType = videoMime;
            return true;
        }

        if (AudioMimeTypes.TryGetValue(ext, out var audioMime))
        {
            kind = MediaKind.Audio;
            mimeType = audioMime;
            return true;
        }

        kind = MediaKind.None;
        mimeType = string.Empty;
        return false;
    }
}