using ConvertApi.Core.Conversion;
using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;
using Microsoft.Extensions.Options;

namespace ConvertApi.Worker;

/// <summary>
/// Runs one claimed job: copies its source to a scratch directory, hands it to the matching
/// converter within <see cref="ConversionOptions.MaxDuration"/>, stores the result and records
/// the outcome. A converter's refusal or a timeout fails the job; any other error bubbles up,
/// so the job stays Processing and is retried once its lease runs out.
/// </summary>
public sealed partial class JobProcessor(
    IFileStorage storage,
    IJobDispatcher dispatcher,
    IEnumerable<IFileConverter> converters,
    IOptions<ConversionOptions> options,
    TimeProvider clock,
    ILogger<JobProcessor> logger)
{
    private readonly ConversionOptions _options = options.Value;

    public async Task ProcessAsync(ConversionJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // The dispatcher already failed a job that used all its attempts: only clean up.
        if (job.Status == JobStatus.Processing)
        {
            await ConvertAsync(job, cancellationToken);

            // Throws if another worker took the job over: its outcome wins and it owns the files.
            await dispatcher.SaveAsync(job, cancellationToken);
        }

        await CleanUpAsync(job);
        LogFinished(logger, job.Id, job.Status, job.FailureReason);
    }

    private async Task ConvertAsync(ConversionJob job, CancellationToken cancellationToken)
    {
        var converter = converters.FirstOrDefault(c => c.CanConvert(job.SourceFormat, job.TargetFormat));
        if (converter is null)
        {
            job.Fail($"No converter can turn {job.SourceFormat} into {job.TargetFormat} yet.", clock.GetUtcNow());
            return;
        }

        // Unique per attempt: a worker taking a job over never touches another one's files.
        var workDirectory = Path.Combine(_options.WorkRoot, $"{job.Id:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.MaxDuration);
        try
        {
            var request = new ConversionRequest(
                Path.Combine(workDirectory, $"input.{job.SourceFormat}"),
                Path.Combine(workDirectory, $"output.{job.TargetFormat}"),
                job.SourceFormat,
                job.TargetFormat);

            await DownloadSourceAsync(job.SourceFileKey, request.InputPath, timeout.Token);
            await converter.ConvertAsync(request, timeout.Token);
            await StoreResultAsync(job.Id, request.OutputPath, timeout.Token);
            job.Succeed(_options.ResultRetention, clock.GetUtcNow());
        }
        catch (ConversionFailedException rejected)
        {
            LogRejected(logger, job.Id, rejected.InnerException ?? rejected);
            job.Fail(rejected.Message, clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            job.Fail($"The conversion took longer than {_options.MaxDuration.TotalMinutes:0.##} minutes.", clock.GetUtcNow());
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private async Task DownloadSourceAsync(string key, string path, CancellationToken cancellationToken)
    {
        Stream source;
        try
        {
            source = await storage.OpenReadAsync(key, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            throw new ConversionFailedException("The uploaded file is no longer available.");
        }

        await using (source)
        await using (var file = File.Create(path))
        {
            await source.CopyToAsync(file, cancellationToken);
        }
    }

    private async Task StoreResultAsync(Guid jobId, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new ConversionFailedException("The conversion produced no output.");
        }

        // Saving replaces any result an earlier attempt left, in one step.
        await using var result = File.OpenRead(path);
        try
        {
            await storage.SaveAsync(StorageKeys.ResultOf(jobId), result, _options.MaxResultBytes, cancellationToken);
        }
        catch (FileTooLargeException)
        {
            throw new ConversionFailedException("The converted file is larger than the service allows.");
        }
    }

    /// <summary>The job is final: drop its source, and any result unless it succeeded.</summary>
    private async Task CleanUpAsync(ConversionJob job)
    {
        try
        {
            await storage.DeleteAsync(job.SourceFileKey, CancellationToken.None);
            if (job.Status != JobStatus.Succeeded)
            {
                await storage.DeleteAsync(StorageKeys.ResultOf(job.Id), CancellationToken.None);
            }
        }
        catch (IOException exception)
        {
            // The job's outcome is already saved; a leftover file is not worth failing it.
            LogCleanupFailed(logger, job.Id, exception);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException exception)
        {
            LogCleanupFailed(logger, Guid.Empty, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            LogCleanupFailed(logger, Guid.Empty, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} finished as {Status} {FailureReason}")]
    private static partial void LogFinished(ILogger logger, Guid jobId, JobStatus status, string? failureReason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId}: the converter refused the input")]
    private static partial void LogRejected(ILogger logger, Guid jobId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove files of job {JobId}")]
    private static partial void LogCleanupFailed(ILogger logger, Guid jobId, Exception exception);
}
