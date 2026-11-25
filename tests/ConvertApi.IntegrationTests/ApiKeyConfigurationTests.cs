using ConvertApi.Core.ApiKeys;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace ConvertApi.IntegrationTests;

/// <summary>Bad key configuration must stop the API at startup, not fail on the first request.</summary>
public sealed class ApiKeyConfigurationTests
{
    private const string DevKeyHash = "321f24ce1a7b94d6256ff51fc5ab9ff998ea84350040b96d9048b85ee2cdca22";

    [Fact]
    public void TheWellKnownDevelopmentKey_IsRefusedOutsideDevelopment()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            DeclareKey(builder, 0, "devlocal", DevKeyHash);
        });

        var error = Assert.ThrowsAny<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("development key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADuplicateKeyId_IsRefusedAtStartup()
    {
        var key = ApiKeyCredential.Generate();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            DeclareKey(builder, 0, key.KeyId, key.SecretHash);
            DeclareKey(builder, 1, key.KeyId, key.SecretHash);
        });

        var error = Assert.ThrowsAny<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains(key.KeyId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedHash_IsRefusedAtStartup()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            DeclareKey(builder, 0, "abcd1234", DevKeyHash + "\n");
        });

        Assert.ThrowsAny<OptionsValidationException>(() => factory.CreateClient());
    }

    private static void DeclareKey(IWebHostBuilder builder, int index, string keyId, string hash)
    {
        var prefix = $"ApiKeys:Keys:{index}:";
        builder.UseSetting(prefix + "Id", Guid.CreateVersion7().ToString());
        builder.UseSetting(prefix + "KeyId", keyId);
        builder.UseSetting(prefix + "Name", "Test");
        builder.UseSetting(prefix + "SecretHash", hash);
    }
}
