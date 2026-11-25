using System.Collections.Frozen;
using ConvertApi.Core.ApiKeys;
using Microsoft.Extensions.Options;

namespace ConvertApi.Api.Auth;

/// <summary>
/// Serves the keys declared in configuration. Temporary: keys move to the database
/// with the admin endpoints that create and revoke them.
/// </summary>
internal sealed class ConfiguredApiKeyStore : IApiKeyStore
{
    private readonly FrozenDictionary<string, ApiKey> _keys;

    // Duplicates and malformed entries are rejected at startup by the options validator.
    public ConfiguredApiKeyStore(IOptions<ApiKeyOptions> options)
    {
        _keys = options.Value.Keys.ToFrozenDictionary(
            key => key.KeyId,
            key => new ApiKey(key.Id, key.KeyId, key.Name, key.SecretHash, key.Revoked),
            StringComparer.Ordinal);
    }

    public Task<ApiKey?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken) =>
        Task.FromResult(_keys.GetValueOrDefault(keyId));
}
