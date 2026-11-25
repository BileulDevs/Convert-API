namespace ConvertApi.Core.Storage;

/// <summary>The storage layout, in one place: uploads and results never share a key.</summary>
public static class StorageKeys
{
    public static string NewUpload() => $"uploads/{Guid.CreateVersion7():N}";

    public static string ResultOf(Guid jobId) => $"results/{jobId:N}";
}
