using System.Buffers;
using System.Text.RegularExpressions;

namespace ConvertApi.Core.Storage;

/// <summary>Stores files on the local disk, under one root directory. Used for local runs.</summary>
public sealed partial class LocalFileStorage : IFileStorage
{
    private const int BufferSize = 81_920;

    private readonly string _root;

    public LocalFileStorage(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public async Task<long> SaveAsync(string key, Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write next to the target, then rename in one step: readers never see a partial
        // file, and a failed save only removes its own temporary file. Keys contain no dot,
        // so a temporary name can never be a key.
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            long size;
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                size = await CopyWithLimitAsync(content, file, maxBytes, cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
            return size;
        }
        catch
        {
            // Too large, canceled, or the client dropped mid-upload: never keep a partial file.
            File.Delete(temporaryPath);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Nothing is stored under '{key}'.");
        }

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);

        // File.Delete ignores a missing file but throws for a missing folder.
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static async Task<long> CopyWithLimitAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new FileTooLargeException(maxBytes);
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string ResolvePath(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException($"'{key}' is not a valid storage key.", nameof(key));
        }

        var path = Path.GetFullPath(Path.Combine(_root, key));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{key}' points outside the storage root.", nameof(key));
        }

        return path;
    }

    // Segments of lower-case letters, digits and '-', separated by single slashes: no dots, so no "..".
    [GeneratedRegex(@"^[a-z0-9-]+(/[a-z0-9-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
