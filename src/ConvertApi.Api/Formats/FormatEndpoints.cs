using ConvertApi.Core.Formats;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ConvertApi.Api.Formats;

/// <summary>One input format and what it converts to.</summary>
public sealed record FormatResponse(string Source, FormatFamily Family, IReadOnlyList<string> Targets);

public sealed record FormatsResponse(IReadOnlyList<FormatResponse> Formats);

public static class FormatEndpoints
{
    /// <summary>The catalog only changes with a deployment: an hour of caching costs nothing.</summary>
    private const string CacheControl = "public, max-age=3600";

    private static readonly FormatsResponse Catalog = new(
        [.. FormatCatalog.AcceptedSources.Select(source => new FormatResponse(
            source, FormatCatalog.SourceFamilyOf(source)!.Value, FormatCatalog.TargetsFor(source)))]);

    public static IEndpointRouteBuilder MapFormatEndpoints(this IEndpointRouteBuilder app)
    {
        // Public and outside the per-key rate limit: it reveals nothing about anyone's jobs, and
        // clients (the web front among them) show it before an API key is entered.
        app.MapGet("/v1/formats", GetFormats)
            .WithTags("Formats")
            .WithName("GetFormats")
            .WithSummary("List the formats the service accepts and what each converts to (FR-10)")
            .AllowAnonymous();

        return app;
    }

    private static Ok<FormatsResponse> GetFormats(HttpResponse response)
    {
        response.Headers.CacheControl = CacheControl;
        return TypedResults.Ok(Catalog);
    }
}
