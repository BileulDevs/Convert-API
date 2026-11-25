using ConvertApi.Core.Conversion;
using ConvertApi.Core.Formats;
using ImageMagick;

namespace ConvertApi.Worker.Converters;

/// <summary>
/// Converts between the image formats of the catalog with Magick.NET (ImageMagick, shipped
/// inside the NuGet package). Untrusted input is handled defensively:
/// <list type="bullet">
/// <item>files are passed as streams, never as paths, so no "coder:" prefix can be interpreted;</item>
/// <item>the input is decoded only as the format its signature showed;</item>
/// <item>the pixels of every frame that will be decoded are counted from the headers first;</item>
/// <item>metadata (EXIF, GPS, XMP, ICC) is stripped from the output.</item>
/// </list>
/// </summary>
public sealed class ImageConverter : IFileConverter
{
    /// <summary>Pixels allowed across all decoded frames: about 50 megapixels.</summary>
    public const ulong DefaultMaxPixels = 50_000_000;

    private const ulong MaxSide = 30_000;
    private const ulong MaxFrames = 100;
    private const uint JpegQuality = 85;

    private readonly ulong _maxPixels;

    static ImageConverter() => ApplyResourceLimits(TimeSpan.FromMinutes(10), Path.GetTempPath());

    public ImageConverter(ulong maxPixels = DefaultMaxPixels)
    {
        _maxPixels = maxPixels;
    }

    /// <summary>
    /// Process-wide ImageMagick limits, a last line of defence behind the header checks.
    /// The time limit is what stops a pathological decode: the job's cancellation cannot
    /// interrupt native code.
    /// </summary>
    public static void ApplyResourceLimits(TimeSpan maxDuration, string temporaryDirectory)
    {
        ResourceLimits.Width = MaxSide;
        ResourceLimits.Height = MaxSide;
        ResourceLimits.ListLength = MaxFrames;
        ResourceLimits.Area = 64_000_000;
        ResourceLimits.Memory = 1024UL * 1024 * 1024;
        ResourceLimits.Disk = 4UL * 1024 * 1024 * 1024;
        ResourceLimits.Thread = 2;
        ResourceLimits.Time = (ulong)Math.Ceiling(maxDuration.TotalSeconds);

        // Pixel cache spills go next to the job files, not to an unbounded system folder.
        Directory.CreateDirectory(temporaryDirectory);
        MagickNET.SetTempDirectory(temporaryDirectory);
    }

    public bool CanConvert(string sourceFormat, string targetFormat) =>
        FormatCatalog.SourceFamilyOf(sourceFormat) == FormatFamily.Image
        && FormatCatalog.IsSupported(sourceFormat, targetFormat);

    public async Task ConvertAsync(ConversionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceFormat = MagickFormatOf(request.SourceFormat);
        var keepsAnimation = request.TargetFormat is "gif" or "webp";
        var settings = new MagickReadSettings
        {
            Format = sourceFormat,

            // A still target only needs the first frame: never decode the others.
            FrameIndex = keepsAnimation ? null : 0,
            FrameCount = keepsAnimation ? null : 1,
        };

        try
        {
            EnsureReasonableSize(request.InputPath, settings, keepsAnimation);

            using var frames = new MagickImageCollection();
            await using (var input = File.OpenRead(request.InputPath))
            {
                await frames.ReadAsync(input, settings, cancellationToken);
            }

            if (frames.Count == 0 || frames[0].Format != sourceFormat)
            {
                throw new ConversionFailedException($"The file is not really a {request.SourceFormat} image.");
            }

            await using var output = File.Create(request.OutputPath);
            await WriteAsync(frames, request.TargetFormat, output, cancellationToken);
        }
        catch (MagickException exception)
        {
            File.Delete(request.OutputPath);
            throw new ConversionFailedException(
                $"The image could not be read as {request.SourceFormat}: it is corrupt or not really a {request.SourceFormat} file.",
                exception);
        }
        catch
        {
            File.Delete(request.OutputPath);
            throw;
        }
    }

    private void EnsureReasonableSize(string path, MagickReadSettings settings, bool allFrames)
    {
        using var input = File.OpenRead(path);
        var headers = MagickImageInfo.ReadCollection(input, settings).ToList();
        var counted = allFrames ? headers : headers.Take(1);

        // Coalescing an animation draws every frame on the full canvas, so count the canvas.
        var pixels = counted.Aggregate(0UL, (sum, frame) => sum + ((ulong)frame.Width * frame.Height));
        if (allFrames && headers.Count > 1)
        {
            pixels = Math.Max(pixels, (ulong)headers.Count * headers.Max(f => (ulong)f.Width * f.Height));
        }

        if (pixels > _maxPixels)
        {
            throw new ConversionFailedException(
                $"The image holds {pixels} pixels across its frames: images are limited to {_maxPixels} pixels.");
        }
    }

    private static async Task WriteAsync(MagickImageCollection frames, string targetFormat, Stream output, CancellationToken cancellationToken)
    {
        var format = MagickFormatOf(targetFormat);

        // GIF and WebP keep an animation; other targets take the first frame.
        if (frames.Count > 1 && targetFormat is "gif" or "webp")
        {
            frames.Coalesce();
            foreach (var frame in frames)
            {
                Clean(frame);
            }

            await frames.WriteAsync(output, format, cancellationToken);
            return;
        }

        var image = frames[0];
        Clean(image);
        if (targetFormat is "jpg" or "bmp")
        {
            // These formats have no transparency: flatten onto white rather than black.
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }

        if (targetFormat == "jpg")
        {
            image.Quality = JpegQuality;
        }

        await image.WriteAsync(output, format, cancellationToken);
    }

    /// <summary>Applies the EXIF orientation, then drops every profile and comment.</summary>
    private static void Clean(IMagickImage<byte> image)
    {
        image.AutoOrient();
        image.Strip();
    }

    private static MagickFormat MagickFormatOf(string format) => format switch
    {
        "png" => MagickFormat.Png,
        "jpg" => MagickFormat.Jpeg,
        "gif" => MagickFormat.Gif,
        "bmp" => MagickFormat.Bmp,
        "tiff" => MagickFormat.Tiff,
        "webp" => MagickFormat.WebP,
        _ => throw new ConversionFailedException($"{format} is not an image format."),
    };
}
