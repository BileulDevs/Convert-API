using System.Globalization;
using ConvertApi.Core.ApiKeys;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ConvertApi.IntegrationTests;

/// <summary>
/// Runs the API against its own PostgreSQL database, with its own storage directory,
/// a small image limit and three test keys (two active clients and a revoked one),
/// declared after the development key.
/// </summary>
public sealed class ConvertApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _connectionString = "";

    public const long ImageLimitBytes = 1024;
    public const string ApiKeyHeader = "X-Api-Key";

    public GeneratedApiKey ClientA { get; } = ApiKeyCredential.Generate();

    public GeneratedApiKey ClientB { get; } = ApiKeyCredential.Generate();

    public GeneratedApiKey RevokedClient { get; } = ApiKeyCredential.Generate();

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "convertapi-it", Guid.NewGuid().ToString("N"));

    public string[] StoredFiles() =>
        Directory.Exists(StorageRoot) ? Directory.GetFiles(StorageRoot, "*", SearchOption.AllDirectories) : [];

    /// <summary>A client that sends <paramref name="key"/>, or client A's key by default.</summary>
    public HttpClient CreateClientWithKey(string? key = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyHeader, key ?? ClientA.Key);
        return client;
    }

    public async ValueTask InitializeAsync()
    {
        _connectionString = await PostgresContainer.NewDatabaseConnectionStringAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:convertapi", _connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Uploads:StorageRoot", StorageRoot);
        builder.UseSetting("Uploads:MaxImageBytes", ImageLimitBytes.ToString(CultureInfo.InvariantCulture));
        DeclareKey(builder, 1, ClientA, revoked: false);
        DeclareKey(builder, 2, ClientB, revoked: false);
        DeclareKey(builder, 3, RevokedClient, revoked: true);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }

    // Index 0 is the development key from appsettings.Development.json.
    private static void DeclareKey(IWebHostBuilder builder, int index, GeneratedApiKey key, bool revoked)
    {
        var prefix = $"ApiKeys:Keys:{index}:";
        builder.UseSetting(prefix + "Id", Guid.CreateVersion7().ToString());
        builder.UseSetting(prefix + "KeyId", key.KeyId);
        builder.UseSetting(prefix + "Name", $"Test client {index}");
        builder.UseSetting(prefix + "SecretHash", key.SecretHash);
        builder.UseSetting(prefix + "Revoked", revoked ? "true" : "false");
    }
}
