namespace ConvertApi.Worker;

/// <summary>The scratch directories conversions run in, under <see cref="ConversionOptions.WorkRoot"/>.</summary>
public static class WorkDirectory
{
    /// <summary>
    /// Deletes scratch directories left behind by a worker that was killed mid-conversion (its own
    /// cleanup never ran). Only those older than <paramref name="olderThan"/> go: a conversion
    /// cannot run that long, so no live worker sharing the root is still using them.
    /// </summary>
    /// <returns>How many directories were removed.</returns>
    public static int RemoveStale(string root, TimeSpan olderThan, DateTimeOffset now)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var removed = 0;
        // Conversion directories are named "{job}-{attempt}"; ImageMagick's shared temp directory is left alone.
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories("*-*"))
        {
            if (now.UtcDateTime - directory.LastWriteTimeUtc > olderThan)
            {
                directory.Delete(recursive: true);
                removed++;
            }
        }

        return removed;
    }
}
