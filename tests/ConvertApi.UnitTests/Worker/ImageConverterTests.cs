using ConvertApi.Core.Conversion;
using ConvertApi.Core.Formats;
using ConvertApi.Worker.Converters;
using ImageMagick;

namespace ConvertApi.UnitTests.Worker;

public sealed class ImageConverterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "convertapi-tests", Guid.NewGuid().ToString("N"));
    private readonly ImageConverter _converter = new();

    public ImageConverterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("png", "jpg")]
    [InlineData("png", "webp")]
    [InlineData("jpg", "png")]
    [InlineData("webp", "gif")]
    [InlineData("gif", "bmp")]
    [InlineData("tiff", "png")]
    [InlineData("bmp", "jpg")]
    [InlineData("png", "png")]
    public async Task Convert_ProducesAnImageOfTheTargetFormatWithTheSameSize(string source, string target)
    {
        var request = await RequestAsync(source, target, width: 40, height: 30);

        await _converter.ConvertAsync(request, Ct);

        var bytes = await File.ReadAllBytesAsync(request.OutputPath, Ct);
        Assert.Equal(target, FileSignatureDetector.Detect(bytes));
        var info = new MagickImageInfo(bytes);
        Assert.Equal((40u, 30u), (info.Width, info.Height));
    }

    [Fact]
    public async Task ToJpg_TransparentAreasBecomeWhite_NotBlack()
    {
        var request = await RequestAsync("png", "jpg", width: 10, height: 10, fill: MagickColors.Transparent);

        await _converter.ConvertAsync(request, Ct);

        using var result = new MagickImage(await File.ReadAllBytesAsync(request.OutputPath, Ct));
        var pixel = result.GetPixels().GetPixel(5, 5).ToColor();
        Assert.NotNull(pixel);
        Assert.True(pixel.R > 240 && pixel.G > 240 && pixel.B > 240, $"Expected white, got {pixel}.");
    }

    [Fact]
    public async Task AFileThatIsNotReallyOfItsDetectedFormat_IsRejected()
    {
        // JPEG content handed over as PNG: the converter only decodes what was detected.
        var jpeg = await RequestAsync("jpg", "png", width: 10, height: 10);
        var lying = jpeg with { SourceFormat = "png" };

        await Assert.ThrowsAsync<ConversionFailedException>(() => _converter.ConvertAsync(lying, Ct));
        Assert.False(File.Exists(lying.OutputPath));
    }

    [Fact]
    public async Task ACorruptImage_IsRejected()
    {
        var input = Path.Combine(_directory, "corrupt.png");
        await File.WriteAllBytesAsync(input, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3], Ct);
        var request = new ConversionRequest(input, Path.Combine(_directory, "out.jpg"), "png", "jpg");

        var error = await Assert.ThrowsAsync<ConversionFailedException>(() => _converter.ConvertAsync(request, Ct));

        Assert.Contains("png", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImageWithTooManyPixels_IsRejectedBeforeBeingDecoded()
    {
        var converter = new ImageConverter(maxPixels: 100);
        var request = await RequestAsync("png", "jpg", width: 20, height: 20);

        var error = await Assert.ThrowsAsync<ConversionFailedException>(() => converter.ConvertAsync(request, Ct));

        Assert.Contains("pixels", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(request.OutputPath));
    }

    [Fact]
    public async Task AnSvgDisguisedAsPng_IsNeverInterpreted()
    {
        var input = Path.Combine(_directory, "evil.png");
        await File.WriteAllTextAsync(
            input,
            """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><image href="file:///etc/passwd"/></svg>""",
            Ct);
        var request = new ConversionRequest(input, Path.Combine(_directory, "out.jpg"), "png", "jpg");

        await Assert.ThrowsAsync<ConversionFailedException>(() => _converter.ConvertAsync(request, Ct));
        Assert.False(File.Exists(request.OutputPath));
    }

    [Fact]
    public async Task ManyFramesAddingUpPastTheLimit_AreRejected_WhenTheTargetKeepsThem()
    {
        var converter = new ImageConverter(maxPixels: 50_000);
        var request = await AnimationAsync(frames: 10, side: 100, target: "gif");

        var error = await Assert.ThrowsAsync<ConversionFailedException>(() => converter.ConvertAsync(request, Ct));

        Assert.Contains("pixels", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForAStillTarget_OnlyTheFirstFrameIsDecoded()
    {
        var converter = new ImageConverter(maxPixels: 50_000);
        var request = await AnimationAsync(frames: 10, side: 100, target: "png");

        await converter.ConvertAsync(request, Ct);

        using var result = new MagickImageCollection(await File.ReadAllBytesAsync(request.OutputPath, Ct));
        Assert.Single(result);
    }

    [Fact]
    public async Task AnAnimatedGif_StaysAnimated()
    {
        var request = await AnimationAsync(frames: 3, side: 20, target: "gif");

        await _converter.ConvertAsync(request, Ct);

        using var result = new MagickImageCollection(await File.ReadAllBytesAsync(request.OutputPath, Ct));
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Metadata_IsStrippedFromTheResult()
    {
        var input = Path.Combine(_directory, "photo.jpg");
        using (var photo = new MagickImage(MagickColors.CornflowerBlue, 20, 20))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Software, "Uploader's camera");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            photo.SetProfile(exif);
            await photo.WriteAsync(input, MagickFormat.Jpeg, Ct);
        }

        var request = new ConversionRequest(input, Path.Combine(_directory, "out.jpg"), "jpg", "jpg");

        await _converter.ConvertAsync(request, Ct);

        using var result = new MagickImage(await File.ReadAllBytesAsync(request.OutputPath, Ct));
        Assert.Null(result.GetExifProfile());
    }

    [Theory]
    [InlineData("png", "jpg", true)]
    [InlineData("tiff", "webp", true)]
    [InlineData("png", "tiff", false)]
    [InlineData("png", "mp3", false)]
    [InlineData("mp4", "gif", false)]
    [InlineData("mp3", "wav", false)]
    public void CanConvert_CoversTheImagePairsOfTheCatalog(string source, string target, bool expected)
    {
        Assert.Equal(expected, _converter.CanConvert(source, target));
    }

    private async Task<ConversionRequest> RequestAsync(string source, string target, uint width, uint height, MagickColor? fill = null)
    {
        var input = Path.Combine(_directory, $"in-{Guid.NewGuid():N}.{source}");
        using (var image = new MagickImage(fill ?? MagickColors.CornflowerBlue, width, height))
        {
            await image.WriteAsync(input, FormatOf(source), Ct);
        }

        return new ConversionRequest(input, Path.Combine(_directory, $"out-{Guid.NewGuid():N}.{target}"), source, target);
    }

    private async Task<ConversionRequest> AnimationAsync(int frames, uint side, string target)
    {
        var input = Path.Combine(_directory, $"anim-{Guid.NewGuid():N}.gif");
        using (var animation = new MagickImageCollection())
        {
            for (var i = 0; i < frames; i++)
            {
                animation.Add(new MagickImage(i % 2 == 0 ? MagickColors.Red : MagickColors.Blue, side, side));
            }

            await animation.WriteAsync(input, MagickFormat.Gif, Ct);
        }

        return new ConversionRequest(input, Path.Combine(_directory, $"out-{Guid.NewGuid():N}.{target}"), "gif", target);
    }

    private static MagickFormat FormatOf(string format) => format switch
    {
        "png" => MagickFormat.Png,
        "jpg" => MagickFormat.Jpeg,
        "gif" => MagickFormat.Gif,
        "bmp" => MagickFormat.Bmp,
        "tiff" => MagickFormat.Tiff,
        _ => MagickFormat.WebP,
    };
}
