using ConvertApi.Api.Auth;
using ConvertApi.Api.Jobs;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<ApiKeySecuritySchemeTransformer>());
builder.Services.AddApiKeyAuthentication();
builder.Services.AddConversionJobs();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.MapDefaultEndpoints();
app.MapJobEndpoints();

app.Run();
