using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ConvertApi.Core.ApiKeys;

/// <summary>A freshly generated key: <see cref="Key"/> is shown once, only the hash is stored.</summary>
public sealed record GeneratedApiKey(string Key, string KeyId, string SecretHash);

/// <summary>
/// API keys look like <c>cvt_&lt;key id&gt;_&lt;secret&gt;</c>: the 8-character key id finds the
/// stored record, and the whole key is checked against its SHA-256 hash. Keys are random and
/// long (about 190 bits), so a fast hash is enough; a password hash would only add latency.
/// </summary>
public static partial class ApiKeyCredential
{
    private const string Prefix = "cvt_";
    private const int KeyIdLength = 8;
    private const int SecretLength = 32;
    private const string KeyIdAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const string SecretAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static GeneratedApiKey Generate()
    {
        var keyId = RandomNumberGenerator.GetString(KeyIdAlphabet, KeyIdLength);
        var key = $"{Prefix}{keyId}_{RandomNumberGenerator.GetString(SecretAlphabet, SecretLength)}";
        return new GeneratedApiKey(key, keyId, Hash(key));
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out string? keyId)
    {
        var match = value is null ? Match.Empty : KeyPattern().Match(value);
        keyId = match.Success ? match.Groups["id"].Value : null;
        return match.Success;
    }

    /// <returns>The lower-case hex SHA-256 of the key, as stored.</returns>
    public static string Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>Compares in constant time, so response timing reveals nothing about the hash.</summary>
    public static bool Matches(string key, string storedHash)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (storedHash is null || !HashPattern().IsMatch(storedHash))
        {
            return false;
        }

        var expected = Convert.FromHexString(storedHash);
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    [GeneratedRegex(@"^cvt_(?<id>[a-z0-9]{8})_[A-Za-z0-9]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
