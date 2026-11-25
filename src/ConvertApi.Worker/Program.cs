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
builder.Services.AddSingleton<ConversionTelemetry>();
builder.Services.AddHostedService<QueueLengthSampler>();

// One registration per conversion engine.
builder.Services.AddSingleton<IFileConverter, ImageConverter>();
builder.Services.AddSingleton<IFileConverter>(provider =>
{
    var options = provider.GetRequiredService<IOptions<ConversionOptions>>().Value;
    return new FfmpegConverter(options.FfmpegPath, options.FfmpegThreads, options.MaxMediaDuration, options.MaxResultBytes);
});
builder.Services.AddHostedService<ConversionWorker>();
builder.Services.AddScoped<ResultSweeper>();
builder.Services.AddHostedService<ResultSweepWorker>();

var host = builder.Build();

var conversion = host.Services.GetRequiredService<IOptions<ConversionOptions>>().Value;
WorkDirectory.RemoveStale(conversion.WorkRoot, conversion.MaxDuration * 2, DateTimeOffset.UtcNow);
ImageConverter.ApplyResourceLimits(conversion.MaxDuration, Path.Combine(conversion.WorkRoot, "imagemagick"));

host.Run();
