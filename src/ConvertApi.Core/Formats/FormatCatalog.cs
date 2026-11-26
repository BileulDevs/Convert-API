using System.Collections.Frozen;

namespace ConvertApi.Core.Formats;

/// <summary>
/// The conversions the service supports: any input of a family can become any output of that family.
/// Format ids are lower case without a dot, as stored on a job.
/// </summary>
public static class FormatCatalog
{
    private static readonly FrozenDictionary<string, FormatFamily> SourceFamilies = new Dictionary<string, FormatFamily>
    {
        ["mp3"] = FormatFamily.Audio,
        ["wav"] = FormatFamily.Audio,
        ["flac"] = FormatFamily.Audio,
        ["aac"] = FormatFamily.Audio,
        ["ogg"] = FormatFamily.Audio,
        ["m4a"] = FormatFamily.Audio,
        ["mp4"] = FormatFamily.Video,
        ["mkv"] = FormatFamily.Video,
        ["webm"] = FormatFamily.Video,
        ["mov"] = FormatFamily.Video,
        ["avi"] = FormatFamily.Video,
        ["png"] = FormatFamily.Image,
        ["jpg"] = FormatFamily.Image,
        ["webp"] = FormatFamily.Image,
        ["gif"] = FormatFamily.Image,
        ["bmp"] = FormatFamily.Image,
        ["tiff"] = FormatFamily.Image,
        ["docx"] = FormatFamily.Document,
        ["odt"] = FormatFamily.Document,
        ["md"] = FormatFamily.Document,
        ["html"] = FormatFamily.Document,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<FormatFamily, string[]> Targets = new Dictionary<FormatFamily, string[]>
    {
        // Video outputs include animated GIF and audio extraction.
        [FormatFamily.Audio] = ["aac", "flac", "mp3", "ogg", "wav"],
        [FormatFamily.Video] = ["gif", "mp3", "mp4", "wav", "webm"],
        [FormatFamily.Image] = ["bmp", "gif", "jpg", "png", "webp"],
        [FormatFamily.Document] = ["html", "md", "pdf"],
    }.ToFrozenDictionary();

    /// <summary>
    /// The input formats uploads are accepted in today, sorted: every format the catalog lists
    /// except documents, which have no detection or engine yet.
    /// </summary>
    /// <remarks>shortcut: documents join once their detection and conversion engines exist (phase 5).</remarks>
    public static IReadOnlyList<string> AcceptedSources { get; } =
        [.. SourceFamilies.Where(entry => entry.Value != FormatFamily.Document).Select(entry => entry.Key).Order(StringComparer.Ordinal)];

    /// <summary>The family of an input format, or null when the service does not accept it.</summary>
    public static FormatFamily? SourceFamilyOf(string source) =>
        SourceFamilies.TryGetValue(source, out var family) ? family : null;

    /// <summary>The formats a source can be converted to, sorted; empty for an unknown source.</summary>
    public static IReadOnlyList<string> TargetsFor(string source) =>
        SourceFamilyOf(source) is { } family ? Targets[family] : [];

    public static bool IsSupported(string source, string target) =>
        TargetsFor(source).Contains(target, StringComparer.Ordinal);
}
