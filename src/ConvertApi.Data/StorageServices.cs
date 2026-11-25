using ConvertApi.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ConvertApi.Data;

public static class StorageServices
{
    /// <summary>Configuration key of the storage root, shared by the API and the worker.</summary>
    public const string RootKey = "Storage:Root";

    /// <summary>Registers the file storage. Local disk for now; Azure Blob Storage comes with the deployment.</summary>
    public static TBuilder AddConvertApiStorage<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var root = builder.Configuration[RootKey];
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Path.GetTempPath(), "convertapi");
        }

        builder.Services.AddSingleton<IFileStorage>(new LocalFileStorage(root));
        return builder;
    }
}
