using ConvertApi.Api.Auth;
using ConvertApi.Api.Jobs;
using ConvertApi.Api.RateLimiting;
using ConvertApi.Data;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddConvertApiData();
builder.AddConvertApiStorage();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<ApiKeySecuritySchemeTransformer>());
builder.Services.AddApiKeyAuthentication();
builder.Services.AddConversionJobs();
builder.Services.AddApiKeyRateLimiting();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// After authorization: requests without a valid key get a 401 and use up no one's limit.
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.MapDefaultEndpoints();
app.MapJobEndpoints();

app.Run();
