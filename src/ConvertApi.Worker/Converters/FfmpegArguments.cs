using System.Globalization;
using ConvertApi.Core.Conversion;

namespace ConvertApi.Worker.Converters;

/// <summary>The FFmpeg command line for one conversion, built from fixed values only.</summary>
internal static class FfmpegArguments
{
    /// <summary>FFmpeg refuses any single allocation larger than this (1 GB).</summary>
    private const string MaxAllocationBytes = "1073741824";

    // GIF up to 480 px wide at 10 fps, with a palette computed from the clip.
    private const string GifFilter =
        "fps=10,scale='min(480,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse";

    // At most Full HD, keeping the aspect ratio, with the even dimensions H.264 and VP9 need.
    private const string VideoSizeFilter =
        "scale='min(1920,iw)':'min(1080,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2";

    public static IEnumerable<string> For(ConversionRequest request, FfmpegLimits limits)
    {
        var threads = limits.Threads.ToString(CultureInfo.InvariantCulture);
        var demuxer = DemuxerFor(request.SourceFormat);

        List<string> arguments =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-max_alloc", MaxAllocationBytes,
            "-filter_threads", "1",
            "-threads", threads,
            "-protocol_whitelist", "file",
        ];

        // MP4/MOV can reference other files ("dref" atoms): never follow them.
        if (demuxer == "mov")
        {
            arguments.AddRange(["-enable_drefs", "0"]);
        }

        arguments.AddRange(
        [
            "-f", demuxer,
            "-i", request.InputPath,
            "-map_metadata", "-1",
            "-map_chapters", "-1",
            .. OutputFor(request.TargetFormat),
            "-threads", threads,
            "-t", limits.MaxMediaDuration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-fs", limits.MaxOutputBytes.ToString(CultureInfo.InvariantCulture),
            request.OutputPath,
        ]);

        return arguments;
    }

    /// <summary>Forces the input format: FFmpeg never guesses from the content.</summary>
    private static string DemuxerFor(string format) => format switch
    {
        "mp4" or "mov" or "m4a" => "mov",
        "mkv" or "webm" => "matroska",
        "avi" => "avi",
        "mp3" => "mp3",
        "wav" => "wav",
        "flac" => "flac",
        "ogg" => "ogg",
        "aac" => "aac",
        _ => throw new ConversionFailedException($"{format} is not an audio or video format."),
    };

    private static string[] OutputFor(string format) => format switch
    {
        "mp3" => ["-map", "0:a:0", "-c:a", "libmp3lame", "-q:a", "2", "-id3v2_version", "0", "-write_id3v1", "0", "-f", "mp3"],
        "wav" => ["-map", "0:a:0", "-c:a", "pcm_s16le", "-f", "wav"],
        "flac" => ["-map", "0:a:0", "-c:a", "flac", "-f", "flac"],
        "ogg" => ["-map", "0:a:0", "-c:a", "libvorbis", "-q:a", "5", "-f", "ogg"],
        "aac" => ["-map", "0:a:0", "-c:a", "aac", "-b:a", "192k", "-f", "adts"],
        "mp4" =>
        [
            "-map", "0:v:0", "-map", "0:a:0?", "-vf", VideoSizeFilter,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", "-f", "mp4",
        ],
        "webm" =>
        [
            "-map", "0:v:0", "-map", "0:a:0?", "-vf", VideoSizeFilter,
            "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "33", "-deadline", "good", "-cpu-used", "5", "-row-mt", "1",
            "-c:a", "libopus", "-b:a", "128k", "-f", "webm",
        ],
        "gif" => ["-map", "0:v:0", "-an", "-vf", GifFilter, "-loop", "0", "-f", "gif"],
        _ => throw new ConversionFailedException($"Audio and video cannot be converted to {format}."),
    };
}
