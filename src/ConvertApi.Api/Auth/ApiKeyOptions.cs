using System.ComponentModel.DataAnnotations;

namespace ConvertApi.Api.Auth;

/// <summary>The keys clients may use, bound from the "ApiKeys" section. Holds hashes, never keys.</summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "ApiKeys";

    public List<ConfiguredApiKey> Keys { get; init; } = [];
}

public sealed class ConfiguredApiKey
{
    /// <summary>Owner id stamped on the jobs this key creates.</summary>
    [Required]
    public Guid Id { get; set; }

    /// <summary>The 8 characters after <c>cvt_</c> in the key.</summary>
    [Required]
    [RegularExpression(@"^[a-z0-9]{8}\z")]
    public string KeyId { get; set; } = "";

    [Required]
    public string Name { get; set; } = "";

    /// <summary>Lower-case hex SHA-256 of the whole key.</summary>
    [Required]
    [RegularExpression(@"^[0-9a-f]{64}\z")]
    public string SecretHash { get; set; } = "";

    public bool Revoked { get; set; }

    /// <summary>
    /// Signs this key's webhooks; shared with its owner only, so one client cannot forge another's.
    /// Unlike the key, it is kept in clear: signing needs it. Supply it from a secret store, never a committed file.
    /// </summary>
    [Required]
    [MinLength(32)]
    public string WebhookSecret { get; set; } = "";
}
