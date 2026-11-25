using System.ComponentModel;
using System.Diagnostics;
using ConvertApi.Core.Conversion;
using ConvertApi.Core.Formats;

namespace ConvertApi.Worker.Converters;

/// <summary>
/// Converts audio and video by running FFmpeg as a separate process. Untrusted input is
/// handled defensively:
/// <list type="bullet">
/// <item>arguments are passed as a list, never through a shell, and paths must be absolute;</item>
/// <item>the input is read only as the format its signature showed, and may open no other file or URL;</item>
/// <item>the output is capped in duration, size and resolution while it is produced;</item>
/// <item>metadata and chapters are not copied to the output.</item>
/// </list>
/// </summary>
public sealed class FfmpegConverter(
    string ffmpegPath = "ffmpeg",
    int threads = 2,
    TimeSpan? maxMediaDuration = null,
    long maxOutputBytes = FfmpegConverter.DefaultMaxOutputBytes) : IFileConverter
{
    public const long DefaultMaxOutputBytes = 2L * 1024 * 1024 * 1024;

    private const int MaxErrorLength = 4_000;
    private static readonly TimeSpan DefaultMaxMediaDuration = TimeSpan.FromHours(2);

    /// <summary>The only variables FFmpeg gets: none holds a secret. SystemRoot lets it start on Windows.</summary>
    private static readonly HashSet<string> InheritedVariables =
        new(["PATH", "TMPDIR", "TEMP", "TMP", "SystemRoot"], StringComparer.OrdinalIgnoreCase);

    private readonly FfmpegLimits _limits = new(threads, maxMediaDuration ?? DefaultMaxMediaDuration, maxOutputBytes);

    public bool CanConvert(string sourceFormat, string targetFormat) =>
        FormatCatalog.SourceFamilyOf(sourceFormat) is FormatFamily.Audio or FormatFamily.Video
        && FormatCatalog.IsSupported(sourceFormat, targetFormat);

    /// <summary>
    /// How FFmpeg is started for <paramref name="request"/>: arguments as a list (never a shell
    /// string) and an environment stripped to the system basics. FFmpeg reads untrusted files, so
    /// it must not inherit the worker's secrets, such as the database connection string.
    /// </summary>
    public ProcessStartInfo CreateStartInfo(ConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in FfmpegArguments.For(request, _limits))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var kept = startInfo.Environment.Where(variable => InheritedVariables.Contains(variable.Key)).ToList();
        startInfo.Environment.Clear();
        foreach (var (name, value) in kept)
        {
            startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    public async Task ConvertAsync(ConversionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAbsolute(request.InputPath);
        EnsureAbsolute(request.OutputPath);

        var startInfo = CreateStartInfo(request);
        try
        {
            var errors = await RunAsync(startInfo, cancellationToken);
            if (errors is not null)
            {
                throw new ConversionFailedException(
                    $"The file could not be converted: it is corrupt, has no usable stream, or is not really a {request.SourceFormat} file.",
                    new InvalidOperationException(errors));
            }

            // -fs stops writing at the limit and still exits successfully: a full-size output was cut short.
            if (new FileInfo(request.OutputPath).Length >= _limits.MaxOutputBytes)
            {
                throw new ConversionFailedException("The converted file is larger than the service allows.");
            }
        }
        catch
        {
            File.Delete(request.OutputPath);
            throw;
        }
    }

    /// <summary>A relative path starting with '-' would be read as an option.</summary>
    private static void EnsureAbsolute(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"FFmpeg only gets absolute paths, not '{path}'.", nameof(path));
        }
    }

    /// <returns>Null on success, otherwise the end of FFmpeg's error output.</returns>
    private static async Task<string?> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new Win32Exception("FFmpeg did not start.");
        }
        catch (Win32Exception missing)
        {
            throw new ConversionFailedException("Audio and video conversion is not available right now.", missing);
        }

        using (process)
        {
            try
            {
                // Both pipes must be drained, or FFmpeg blocks once a buffer fills.
                var errors = ReadTailAsync(process.StandardError, MaxErrorLength);
                var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
                await process.WaitForExitAsync(cancellationToken);
                await output;
                var errorText = await errors;
                return process.ExitCode == 0 ? null : errorText;
            }
            finally
            {
                // Canceled, timed out or failed: never leave FFmpeg running.
                StopIfRunning(process);
            }
        }
    }

    private static void StopIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and the kill.
        }
    }

    /// <summary>Reads a stream to its end, keeping only the last <paramref name="maxLength"/> characters.</summary>
    private static async Task<string> ReadTailAsync(StreamReader reader, int maxLength)
    {
        var tail = new System.Text.StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > maxLength * 2)
            {
                tail.Remove(0, tail.Length - maxLength);
            }
        }

        return tail.Length > maxLength ? tail.ToString(tail.Length - maxLength, maxLength) : tail.ToString();
    }
}

/// <summary>Caps applied to every FFmpeg run.</summary>
internal sealed record FfmpegLimits(int Threads, TimeSpan MaxMediaDuration, long MaxOutputBytes);
