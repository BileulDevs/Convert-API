using ConvertApi.Data.Jobs;
using Microsoft.Extensions.Options;

namespace ConvertApi.Worker;

/// <summary>
/// A conversion allowed to outlast the lease would have its job handed to a second worker
/// while the first is still on it: refuse such a configuration at startup.
/// </summary>
internal sealed class ConversionOptionsValidator(IOptions<JobDispatchOptions> dispatch) : IValidateOptions<ConversionOptions>
{
    public ValidateOptionsResult Validate(string? name, ConversionOptions options) =>
        options.MaxDuration < dispatch.Value.LeaseTimeout
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Conversion:MaxDuration ({options.MaxDuration}) must be shorter than Jobs:Dispatch:LeaseTimeout ({dispatch.Value.LeaseTimeout}).");
}
