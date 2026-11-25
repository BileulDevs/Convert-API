namespace ConvertApi.Core.Formats;

/// <summary>
/// Identifies a file from its first bytes (magic numbers), never from its name or the
/// Content-Type the client sent. Covers the binary formats of the catalog; document
/// formats (ZIP-based or plain text) need a deeper look and are not detected yet.
/// This is a first filter: some signatures are short (BMP, MPEG frame sync), so the worker
/// must still force the input format when it runs a conversion engine.
/// </summary>
public static class FileSignatureDetector
{
    /// <summary>How many leading bytes <see cref="Detect"/> needs to see.</summary>
    public const int HeaderLength = 64;

    /// <returns>The format id, or null when the content matches no known signature.</returns>
    public static string? Detect(ReadOnlySpan<byte> header) =>
        DetectImage(header) ?? DetectContainer(header) ?? DetectAudio(header);

    private static string? DetectImage(ReadOnlySpan<byte> h)
    {
        if (h.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "png";
        }

        if (h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "jpg";
        }

        if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8))
        {
            return "gif";
        }

        if (h.StartsWith((ReadOnlySpan<byte>)[0x49, 0x49, 0x2A, 0x00]) || h.StartsWith((ReadOnlySpan<byte>)[0x4D, 0x4D, 0x00, 0x2A]))
        {
            return "tiff";
        }

        return h.StartsWith("BM"u8) ? "bmp" : null;
    }

    private static string? DetectContainer(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 12 && h.StartsWith("RIFF"u8))
        {
            return h.Slice(8, 4) switch
            {
                var kind when kind.SequenceEqual("WEBP"u8) => "webp",
                var kind when kind.SequenceEqual("WAVE"u8) => "wav",
                var kind when kind.SequenceEqual("AVI "u8) => "avi",
                _ => null,
            };
        }

        if (h.Length >= 12 && h.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return h.Slice(8, 4) switch
            {
                var brand when brand.SequenceEqual("M4A "u8) => "m4a",
                var brand when brand.SequenceEqual("qt  "u8) => "mov",
                _ => "mp4",
            };
        }

        if (h.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
        {
            // Matroska and WebM share the EBML header; the DocType near the start tells them apart.
            return h.IndexOf("webm"u8) >= 0 ? "webm" : "mkv";
        }

        return null;
    }

    private static string? DetectAudio(ReadOnlySpan<byte> h)
    {
        if (h.StartsWith("fLaC"u8))
        {
            return "flac";
        }

        if (h.StartsWith("OggS"u8))
        {
            return "ogg";
        }

        if (h.StartsWith("ID3"u8))
        {
            return "mp3";
        }

        if (h.Length >= 2 && h[0] == 0xFF)
        {
            // MPEG audio frame sync: layer III is MP3, layer "00" is an AAC ADTS stream.
            if ((h[1] & 0xE6) == 0xE2)
            {
                return "mp3";
            }

            if ((h[1] & 0xF6) == 0xF0)
            {
                return "aac";
            }
        }

        return null;
    }
}
