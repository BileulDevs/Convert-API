using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ConvertApi.Core.ApiKeys;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ConvertApi.Api.Auth;

public static class ApiKeyServices
{
    /// <summary>Hash of the public development key shipped in appsettings.Development.json.</summary>
    private const string DevelopmentKeyHash = "321f24ce1a7b94d6256ff51fc5ab9ff998ea84350040b96d9048b85ee2cdca22";

    /// <summary>The development key's webhook secret, public as well: it must not sign anything in production.</summary>
    private const string DevelopmentWebhookSecret = "devlocal-webhook-secret-DoNotUseInProduction";

    public static IServiceCollection AddApiKeyAuthentication(this IServiceCollection services)
    {
        services.AddOptions<ApiKeyOptions>()
            .BindConfiguration(ApiKeyOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ApiKeyOptions>, ApiKeyOptionsValidator>();

        services.AddSingleton<IApiKeyStore, ConfiguredApiKeyStore>();
        services.AddAuthentication(ApiKeyDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, configureOptions: null);

        // Every endpoint needs a key unless it opts out with AllowAnonymous().
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }

    /// <summary>The id of the API key behind an authenticated request.</summary>
    public static Guid GetApiKeyId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Guid.Parse(user.FindFirstValue(ApiKeyDefaults.KeyIdClaim)
            ?? throw new InvalidOperationException("The request is not authenticated with an API key."));
    }

    /// <summary>Checks the declared keys at startup, so a bad configuration never serves a request.</summary>
    private sealed class ApiKeyOptionsValidator(IHostEnvironment environment) : IValidateOptions<ApiKeyOptions>
    {
        public ValidateOptionsResult Validate(string? name, ApiKeyOptions options)
        {
            var failures = new List<string>();

            foreach (var key in options.Keys)
            {
                var results = new List<ValidationResult>();
                if (key.Id == Guid.Empty || !Validator.TryValidateObject(key, new ValidationContext(key), results, validateAllProperties: true))
                {
                    failures.Add($"API key '{key.KeyId}' needs an Id, an 8-character KeyId, a Name, a 64-character lower-case hex SecretHash and a WebhookSecret of at least 32 characters.");
                }
            }

            failures.AddRange(options.Keys
                .GroupBy(key => key.KeyId, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => $"API key id '{group.Key}' is declared more than once."));

            failures.AddRange(options.Keys
                .GroupBy(key => key.Id)
                .Where(group => group.Count() > 1)
                .Select(group => $"API key Id '{group.Key}' is declared more than once."));

            if (!environment.IsDevelopment() && options.Keys.Exists(key => key.SecretHash == DevelopmentKeyHash))
            {
                failures.Add("The public development key is only allowed in the Development environment.");
            }

            if (!environment.IsDevelopment() && options.Keys.Exists(key => key.WebhookSecret == DevelopmentWebhookSecret))
            {
                failures.Add("The public development webhook secret is only allowed in the Development environment.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
