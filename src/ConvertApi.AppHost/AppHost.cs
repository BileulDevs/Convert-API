var builder = DistributedApplication.CreateBuilder(args);

builder.AddDotnetProject("api", "../ConvertApi.Api/ConvertApi.Api.csproj")
    .WithHttpHealthCheck("/health/ready")
    .WithExternalHttpEndpoints();

builder.AddDotnetProject("worker", "../ConvertApi.Worker/ConvertApi.Worker.csproj");

builder.Build().Run();
