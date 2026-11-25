using System.Security.Claims;
using System.Text.Encodings.Web;
using ConvertApi.Core.ApiKeys;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ConvertApi.Api.Auth;

/// <summary>
/// Authenticates a request from the key in the <c>X-Api-Key</c> header. Missing, malformed,
/// unknown, revoked and wrong keys all get the same 401, so a caller learns nothing about
/// which keys exist.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyStore keys,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly string UnknownKeyHash = new('0', 64);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        var presented = values.ToString();
        if (!ApiKeyCredential.TryParse(presented, out var keyId))
        {
            return AuthenticateResult.Fail("Malformed API key.");
        }

        var key = await keys.FindByKeyIdAsync(keyId, Context.RequestAborted);

        // Always hash, even for an unknown key, so timing does not tell which key ids exist.
        var secretMatches = ApiKeyCredential.Matches(presented, key?.SecretHash ?? UnknownKeyHash);
        if (key is null || key.Revoked || !secretMatches)
        {
            return AuthenticateResult.Fail("Unknown, revoked or wrong API key.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ApiKeyDefaults.KeyIdClaim, key.Id.ToString()), new Claim(ClaimTypes.Name, key.Name)],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{ApiKeyDefaults.Scheme} header=\"{ApiKeyDefaults.HeaderName}\"";
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails =
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Missing or invalid API key",
                Detail = $"Send a valid API key in the {ApiKeyDefaults.HeaderName} header.",
            },
        });
    }
}
