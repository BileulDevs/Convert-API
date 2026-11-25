using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ConvertApi.IntegrationTests;

/// <summary>Runs the API with its own storage directory and a small image limit.</summary>
public sealed class ConvertApiFactory : WebApplicationFactory<Program>
{
    public const long ImageLimitBytes = 1024;

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "convertapi-it", Guid.NewGuid().ToString("N"));

    public string[] StoredFiles() =>
        Directory.Exists(StorageRoot) ? Directory.GetFiles(StorageRoot, "*", SearchOption.AllDirectories) : [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Uploads:StorageRoot", StorageRoot);
        builder.UseSetting("Uploads:MaxImageBytes", ImageLimitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }
}
