using System.Text.Json.Serialization;
using ConvertApi.Api.Uploads;
using ConvertApi.Core.Storage;
using Microsoft.Extensions.Options;

namespace ConvertApi.Api.Jobs;

public static class JobServices
{
    public static IServiceCollection AddConversionJobs(this IServiceCollection services)
    {
        services.AddOptions<UploadOptions>()
            .BindConfiguration(UploadOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFileStorage>(provider =>
            new LocalFileStorage(provider.GetRequiredService<IOptions<UploadOptions>>().Value.StorageRoot));
        services.AddScoped<JobUploadReader>();

        services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        return services;
    }
}
