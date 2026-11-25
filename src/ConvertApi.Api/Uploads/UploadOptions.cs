using System.ComponentModel.DataAnnotations;
using ConvertApi.Core.Formats;

namespace ConvertApi.Api.Uploads;

/// <summary>Upload settings, bound from the "Uploads" configuration section.</summary>
public sealed class UploadOptions
{
    public const string SectionName = "Uploads";

    private const long Megabyte = 1024 * 1024;

    /// <summary>Directory of the local file storage.</summary>
    [Required]
    public string StorageRoot { get; set; } = Path.Combine(Path.GetTempPath(), "convertapi");

    [Range(1, long.MaxValue)]
    public long MaxAudioBytes { get; set; } = 200 * Megabyte;

    [Range(1, long.MaxValue)]
    public long MaxVideoBytes { get; set; } = 500 * Megabyte;

    [Range(1, long.MaxValue)]
    public long MaxImageBytes { get; set; } = 50 * Megabyte;

    [Range(1, long.MaxValue)]
    public long MaxDocumentBytes { get; set; } = 50 * Megabyte;

    public long MaxBytesFor(FormatFamily family) => family switch
    {
        FormatFamily.Audio => MaxAudioBytes,
        FormatFamily.Video => MaxVideoBytes,
        FormatFamily.Image => MaxImageBytes,
        FormatFamily.Document => MaxDocumentBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown format family."),
    };

    /// <summary>The largest file any family accepts.</summary>
    public long LargestFileBytes => Math.Max(Math.Max(MaxAudioBytes, MaxVideoBytes), Math.Max(MaxImageBytes, MaxDocumentBytes));
}
