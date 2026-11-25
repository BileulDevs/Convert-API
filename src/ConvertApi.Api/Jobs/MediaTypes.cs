using System.Collections.Frozen;

namespace ConvertApi.Api.Jobs;

/// <summary>The Content-Type sent with each output format of the catalog.</summary>
internal static class MediaTypes
{
    private const string Fallback = "application/octet-stream";

    private static readonly FrozenDictionary<string, string> ByFormat = new Dictionary<string, string>
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["webp"] = "image/webp",
        ["gif"] = "image/gif",
        ["bmp"] = "image/bmp",
        ["mp3"] = "audio/mpeg",
        ["wav"] = "audio/wav",
        ["flac"] = "audio/flac",
        ["aac"] = "audio/aac",
        ["ogg"] = "audio/ogg",
        ["mp4"] = "video/mp4",
        ["webm"] = "video/webm",
        ["pdf"] = "application/pdf",
        ["html"] = "text/html",
        ["md"] = "text/markdown",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string Of(string format) => ByFormat.GetValueOrDefault(format, Fallback);
}
