using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConvertApi.Api.Auth;

/// <summary>Declares the API key header in the OpenAPI document, so Scalar offers a field for it.</summary>
internal sealed class ApiKeySecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ApiKeyDefaults.Scheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyDefaults.HeaderName,
            Description = "Your API key, e.g. cvt_xxxxxxxx_…",
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(ApiKeyDefaults.Scheme, document)] = [],
        });

        return Task.CompletedTask;
    }
}
