using ConvertApi.Core.Conversion;
using ConvertApi.Data;
using ConvertApi.Worker;
using ConvertApi.Worker.Converters;
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

// One registration per conversion engine.
builder.Services.AddSingleton<IFileConverter, ImageConverter>();
builder.Services.AddHostedService<ConversionWorker>();

var host = builder.Build();

var conversion = host.Services.GetRequiredService<IOptions<ConversionOptions>>().Value;
ImageConverter.ApplyResourceLimits(conversion.MaxDuration, Path.Combine(conversion.WorkRoot, "imagemagick"));

host.Run();
