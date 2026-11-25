var builder = DistributedApplication.CreateBuilder(args);

var database = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("convertapi");

builder.AddDotnetProject("api", "../ConvertApi.Api/ConvertApi.Api.csproj")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health/ready")
    .WithExternalHttpEndpoints();

builder.AddDotnetProject("worker", "../ConvertApi.Worker/ConvertApi.Worker.csproj");

builder.Build().Run();
