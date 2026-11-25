namespace ConvertApi.Core.ApiKeys;

public interface IApiKeyStore
{
    /// <summary>Finds a key by the id embedded in it (the part after <c>cvt_</c>).</summary>
    Task<ApiKey?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken);
}
