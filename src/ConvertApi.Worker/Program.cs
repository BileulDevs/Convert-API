using ConvertApi.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHostedService<ConversionWorker>();

var host = builder.Build();
host.Run();
