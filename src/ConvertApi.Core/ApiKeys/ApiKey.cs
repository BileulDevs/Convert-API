namespace ConvertApi.Core.ApiKeys;

/// <summary>A client's API key as stored: never the key itself, only its hash.</summary>
public sealed record ApiKey(Guid Id, string KeyId, string Name, string SecretHash, bool Revoked);
