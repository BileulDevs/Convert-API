namespace ConvertApi.Core.Conversion;

/// <summary>
/// One conversion engine (FFmpeg, an image library, LibreOffice...). Adding an engine means
/// adding an implementation; nothing else in the API or the worker changes.
/// </summary>
public interface IFileConverter
{
    bool CanConvert(string sourceFormat, string targetFormat);

    /// <summary>Reads <see cref="ConversionRequest.InputPath"/> and writes <see cref="ConversionRequest.OutputPath"/>.</summary>
    /// <exception cref="ConversionFailedException">The input cannot be converted (corrupt, unsupported content...).</exception>
    Task ConvertAsync(ConversionRequest request, CancellationToken cancellationToken);
}

/// <summary>Local files the worker prepared for one conversion; formats are canonical ids such as "png".</summary>
public sealed record ConversionRequest(string InputPath, string OutputPath, string SourceFormat, string TargetFormat);

/// <summary>The engine refused the input. The job fails with this message, which the client will read.</summary>
public sealed class ConversionFailedException(string message) : Exception(message);
