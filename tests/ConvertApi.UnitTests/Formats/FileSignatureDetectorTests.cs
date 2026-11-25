using System.Text;
using ConvertApi.Core.Formats;

namespace ConvertApi.UnitTests.Formats;

public sealed class FileSignatureDetectorTests
{
    public static TheoryData<string, byte[]> KnownHeaders => new()
    {
        { "png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00] },
        { "jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10] },
        { "gif", Ascii("GIF89a\x01\x00") },
        { "gif", Ascii("GIF87a\x01\x00") },
        { "bmp", Ascii("BM\x36\x00\x00\x00") },
        { "tiff", [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00] },
        { "tiff", [0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x08] },
        { "webp", Ascii("RIFF\x24\x00\x00\x00WEBPVP8 ") },
        { "wav", Ascii("RIFF\x24\x00\x00\x00WAVEfmt ") },
        { "avi", Ascii("RIFF$\u0000\u0000\u0000AVI LIST") },
        { "flac", Ascii("fLaC\x00\x00\x00\x22") },
        { "ogg", Ascii("OggS\x00\x02\x00\x00") },
        { "mp3", Ascii("ID3\x04\x00\x00\x00\x00") },
        { "mp3", [0xFF, 0xFB, 0x90, 0x64] },
        { "mp3", [0xFF, 0xF3, 0x90, 0x64] },
        { "aac", [0xFF, 0xF1, 0x50, 0x80] },
        { "mp4", Ascii("\u0000\u0000\u0000 ftypisom\u0000\u0000\u0002\u0000") },
        { "mp4", Ascii("\u0000\u0000\u0000\u0018ftypmp42\u0000\u0000\u0000\u0000") },
        { "m4a", Ascii("\u0000\u0000\u0000 ftypM4A \u0000\u0000\u0000\u0000") },
        { "mov", Ascii("\u0000\u0000\u0000\u0014ftypqt  \u0000\u0000\u0000\u0000") },
        { "webm", Ascii("\x1A\x45\xDF\xA3\x9F\x42\x86\x81\x01\x42\x82\x84webm") },
        { "mkv", Ascii("\x1A\x45\xDF\xA3\xA3\x42\x86\x81\x01\x42\x82\x88matroska") },
    };

    [Theory]
    [MemberData(nameof(KnownHeaders))]
    public void Detect_RecognizesTheFormatFromItsSignature(string expected, byte[] header)
    {
        Assert.Equal(expected, FileSignatureDetector.Detect(header));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("<html><body>")]
    [InlineData("RIFF\x24\x00\x00\x00XXXX")]
    [InlineData("\u0000\u0000\u0000 ftyp")]
    public void Detect_ReturnsNullForUnknownContent(string content)
    {
        Assert.Null(FileSignatureDetector.Detect(Ascii(content)));
    }

    [Fact]
    public void HeaderLength_IsEnoughForEverySignature()
    {
        Assert.True(FileSignatureDetector.HeaderLength >= 32);
    }

    private static byte[] Ascii(string value) => Encoding.Latin1.GetBytes(value);
}
