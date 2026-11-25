using ConvertApi.Data;
using ConvertApi.Worker;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddConvertApiData();
builder.AddConvertApiStorage();

builder.Services.AddOptions<ConversionOptions>()
    .BindConfiguration(ConversionOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ConversionOptions>, ConversionOptionsValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<JobProcessor>();
builder.Services.AddHostedService<ConversionWorker>();

var host = builder.Build();
host.Run();
