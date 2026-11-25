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

        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            return await CopyWithLimitAsync(content, file, maxBytes, cancellationToken);
        }
        catch
        {
            // Too large, canceled, or the client dropped mid-upload: never keep a partial file.
            File.Delete(path);
            throw;
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        File.Delete(ResolvePath(key));
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
