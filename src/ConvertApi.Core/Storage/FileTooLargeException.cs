namespace ConvertApi.Core.Storage;

/// <summary>Thrown when a stored file would exceed its size limit.</summary>
public sealed class FileTooLargeException : IOException
{
    public FileTooLargeException(long maxBytes)
        : base($"The file is larger than the {maxBytes}-byte limit.")
    {
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
}
