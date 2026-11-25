using ConvertApi.Core.ApiKeys;

namespace ConvertApi.UnitTests.ApiKeys;

public sealed class ApiKeyCredentialTests
{
    private const string DevKey = "cvt_devlocal_DevelopmentOnlyKeyDoNotUseInProd";

    [Fact]
    public void Generate_ProducesAKeyThatParsesAndMatchesItsHash()
    {
        var generated = ApiKeyCredential.Generate();

        Assert.True(ApiKeyCredential.TryParse(generated.Key, out var keyId));
        Assert.Equal(generated.KeyId, keyId);
        Assert.True(ApiKeyCredential.Matches(generated.Key, generated.SecretHash));
    }

    [Fact]
    public void Generate_ProducesDifferentKeysEachTime()
    {
        var first = ApiKeyCredential.Generate();
        var second = ApiKeyCredential.Generate();

        Assert.NotEqual(first.Key, second.Key);
        Assert.NotEqual(first.KeyId, second.KeyId);
    }

    [Fact]
    public void TryParse_ExtractsTheKeyId()
    {
        Assert.True(ApiKeyCredential.TryParse(DevKey, out var keyId));
        Assert.Equal("devlocal", keyId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cvt_devlocal")]
    [InlineData("cvt_devlocal_tooShort")]
    [InlineData("abc_devlocal_DevelopmentOnlyKeyDoNotUseInProd")]
    [InlineData("cvt_DEVLOCAL_DevelopmentOnlyKeyDoNotUseInProd")]
    [InlineData("cvt_devlocal_DevelopmentOnlyKeyDoNotUseInProd\n")]
    [InlineData(" cvt_devlocal_DevelopmentOnlyKeyDoNotUseInProd")]
    [InlineData("cvt_devlocal_DevelopmentOnlyKeyDoNotUseInPro!")]
    public void TryParse_RejectsAnythingNotShapedLikeAKey(string? value)
    {
        Assert.False(ApiKeyCredential.TryParse(value, out _));
    }

    [Fact]
    public void Hash_IsTheLowerCaseHexSha256OfTheKey()
    {
        Assert.Matches("^[0-9a-f]{64}$", ApiKeyCredential.Hash(DevKey));
        Assert.Equal(ApiKeyCredential.Hash(DevKey), ApiKeyCredential.Hash(DevKey));
    }

    [Fact]
    public void Matches_RejectsAnotherKey()
    {
        var hash = ApiKeyCredential.Hash(DevKey);

        Assert.False(ApiKeyCredential.Matches("cvt_devlocal_DevelopmentOnlyKeyDoNotUseInProX", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("ABCDEF")]
    public void Matches_ReturnsFalseForAMalformedStoredHash(string storedHash)
    {
        Assert.False(ApiKeyCredential.Matches(DevKey, storedHash));
    }
}
