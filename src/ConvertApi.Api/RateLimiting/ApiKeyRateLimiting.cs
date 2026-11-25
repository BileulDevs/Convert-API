using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ConvertApi.Api.Auth;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ConvertApi.Api.RateLimiting;

/// <summary>Bound from "RateLimit": how many requests one API key may send per window.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    [Range(1, 100_000)]
    public int PermitLimit { get; set; } = 100;

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How many queued or running jobs one key may have; past it, new uploads get a 429.</summary>
    [Range(1, 10_000)]
    public int MaxActiveJobs { get; set; } = 10;
}

/// <summary>
/// Limits each API key to <see cref="RateLimitOptions.PermitLimit"/> requests per window, answering
/// 429 with Retry-After beyond it. Requests without a valid key never get here: they are refused
/// with a 401 first.
/// </summary>
/// <remarks>
/// shortcut: counters live in each API instance's memory, so N instances allow N times the limit;
/// move them to a shared store (PostgreSQL or Redis) when the API scales out.
/// </remarks>
public static class ApiKeyRateLimiting
{
    public const string PolicyName = "per-api-key";

    public static IServiceCollection AddApiKeyRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitOptions>()
            .BindConfiguration(RateLimitOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(PolicyName, context =>
            {
                // Fails closed: should an anonymous endpoint ever join the group, it shares one bucket.
                var partition = context.User.FindFirstValue(ApiKeyDefaults.KeyIdClaim) ?? "anonymous";
                var options = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = options.Window,
                    QueueLimit = 0,
                });
            });

            limiter.OnRejected = RejectAsync;
        });

        return services;
    }

    private static async ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        var detail = "Too many requests for this API key.";
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            detail += $" Try again in {seconds} s.";
        }

        // The type is set here: this response bypasses the endpoint pipeline that fills it for other problems.
        await TypedResults.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Rate limit exceeded",
                detail: detail,
                type: "https://tools.ietf.org/html/rfc6585#section-4")
            .ExecuteAsync(context.HttpContext);
    }
}
