var builder = DistributedApplication.CreateBuilder(args);

var database = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("convertapi");

var api = builder.AddDotnetProject("api", "../ConvertApi.Api/ConvertApi.Api.csproj")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health/ready")
    .WithExternalHttpEndpoints();

// The API applies the migrations: the worker starts once the API is ready.
// Both run on this machine and share the default storage folder.
builder.AddDotnetProject("worker", "../ConvertApi.Worker/ConvertApi.Worker.csproj")
    .WithReference(database)
    .WaitFor(api);

builder.Build().Run();
