using ConvertApi.Core.Formats;

namespace ConvertApi.UnitTests.Formats;

public sealed class FormatCatalogTests
{
    [Theory]
    [InlineData("mp4", "mp3")]
    [InlineData("mkv", "webm")]
    [InlineData("mov", "gif")]
    [InlineData("flac", "mp3")]
    [InlineData("m4a", "ogg")]
    [InlineData("png", "jpg")]
    [InlineData("tiff", "webp")]
    [InlineData("png", "png")]
    [InlineData("docx", "pdf")]
    [InlineData("md", "html")]
    public void IsSupported_AcceptsTheSpecPairs(string source, string target)
    {
        Assert.True(FormatCatalog.IsSupported(source, target));
    }

    [Theory]
    [InlineData("png", "mp3")]
    [InlineData("mp3", "mp4")]
    [InlineData("m4a", "m4a")]
    [InlineData("mp4", "avi")]
    [InlineData("jpg", "tiff")]
    [InlineData("pdf", "docx")]
    [InlineData("exe", "png")]
    public void IsSupported_RejectsEverythingElse(string source, string target)
    {
        Assert.False(FormatCatalog.IsSupported(source, target));
    }

    [Theory]
    [InlineData("mp3", FormatFamily.Audio)]
    [InlineData("mp4", FormatFamily.Video)]
    [InlineData("gif", FormatFamily.Image)]
    [InlineData("docx", FormatFamily.Document)]
    public void SourceFamilyOf_ReturnsTheFamilyOfAnInputFormat(string format, FormatFamily family)
    {
        Assert.Equal(family, FormatCatalog.SourceFamilyOf(format));
    }

    [Fact]
    public void SourceFamilyOf_ReturnsNullForAnUnknownFormat()
    {
        Assert.Null(FormatCatalog.SourceFamilyOf("pdf"));
    }

    [Fact]
    public void TargetsFor_ListsVideoOutputsIncludingAudioExtraction()
    {
        Assert.Equal(["gif", "mp3", "mp4", "wav", "webm"], FormatCatalog.TargetsFor("mp4"));
    }

    [Fact]
    public void TargetsFor_IsEmptyForAnUnknownSource()
    {
        Assert.Empty(FormatCatalog.TargetsFor("exe"));
    }
}
