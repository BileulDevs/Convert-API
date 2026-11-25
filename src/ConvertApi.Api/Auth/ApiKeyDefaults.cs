namespace ConvertApi.Api.Auth;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    /// <summary>Claim carrying the stored key's id, which owns the jobs it creates.</summary>
    public const string KeyIdClaim = "api_key_id";
}
