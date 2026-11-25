using ConvertApi.Core.Conversion;
using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;
using Microsoft.EntityFrameworkCore;
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
        var canceled = false;
        if (job.Status == JobStatus.Processing)
        {
            // Canceled during the conversion, or just as it ended (the save then finds the cancel).
            canceled = await ConvertAsync(job, cancellationToken);
            if (!canceled)
            {
                canceled = !await TrySaveAsync(job, cancellationToken);
            }
        }

        await CleanUpAsync(job, keepResult: !canceled && job.Status == JobStatus.Succeeded);
        if (canceled)
        {
            LogCanceled(logger, job.Id);
        }
        else
        {
            LogFinished(logger, job.Id, job.Status, job.FailureReason);
        }
    }

    /// <returns>False when the owner canceled the job meanwhile: the store keeps Canceled.</returns>
    private async Task<bool> TrySaveAsync(ConversionJob job, CancellationToken cancellationToken)
    {
        try
        {
            await dispatcher.SaveAsync(job, cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otherwise another worker took the job over: its outcome wins and it owns the files.
            if (!await dispatcher.IsCanceledAsync(job.Id, cancellationToken))
            {
                throw;
            }

            return false;
        }
    }

    /// <returns>True when the owner canceled the job during the conversion, which was then stopped.</returns>
    private async Task<bool> ConvertAsync(ConversionJob job, CancellationToken cancellationToken)
    {
        var converter = converters.FirstOrDefault(c => c.CanConvert(job.SourceFormat, job.TargetFormat));
        if (converter is null)
        {
            job.Fail($"No converter can turn {job.SourceFormat} into {job.TargetFormat} yet.", clock.GetUtcNow());
            return false;
        }

        // Unique per attempt: a worker taking a job over never touches another one's files.
        var workDirectory = Path.Combine(_options.WorkRoot, $"{job.Id:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.MaxDuration);
        using var conversion = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var watch = CancelOnOwnerRequestAsync(job.Id, conversion);
        try
        {
            var request = new ConversionRequest(
                Path.Combine(workDirectory, $"input.{job.SourceFormat}"),
                Path.Combine(workDirectory, $"output.{job.TargetFormat}"),
                job.SourceFormat,
                job.TargetFormat);

            await DownloadSourceAsync(job.SourceFileKey, request.InputPath, conversion.Token);
            await converter.ConvertAsync(request, conversion.Token);
            await StoreResultAsync(job.Id, request.OutputPath, conversion.Token);
            job.Succeed(_options.ResultRetention, clock.GetUtcNow());
        }
        catch (ConversionFailedException rejected)
        {
            LogRejected(logger, job.Id, rejected.InnerException ?? rejected);
            job.Fail(rejected.Message, clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (conversion.IsCancellationRequested && !timeout.IsCancellationRequested)
        {
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            job.Fail($"The conversion took longer than {_options.MaxDuration.TotalMinutes:0.##} minutes.", clock.GetUtcNow());
        }
        finally
        {
            // The watch must be over before the job is saved: both use the same DbContext.
            await conversion.CancelAsync();
            await watch;
            TryDeleteDirectory(workDirectory);
        }

        return false;
    }

    /// <summary>Polls the job until <paramref name="conversion"/> ends, and cancels it if the owner canceled the job.</summary>
    private async Task CancelOnOwnerRequestAsync(Guid jobId, CancellationTokenSource conversion)
    {
        while (!conversion.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CancellationCheckInterval, clock, conversion.Token);
                if (await dispatcher.IsCanceledAsync(jobId, conversion.Token))
                {
                    await conversion.CancelAsync();
                }
            }
            catch (OperationCanceledException) when (conversion.IsCancellationRequested)
            {
                // The conversion ended (or was stopped): nothing left to watch.
            }
#pragma warning disable CA1031 // A failed check must not fail the conversion: log it and check again next time.
            catch (Exception exception) when (!conversion.IsCancellationRequested)
#pragma warning restore CA1031
            {
                LogCancellationCheckFailed(logger, jobId, exception);
            }
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

    /// <summary>The job is final: drop its source, and its result unless it is kept for download.</summary>
    private async Task CleanUpAsync(ConversionJob job, bool keepResult)
    {
        try
        {
            await storage.DeleteAsync(job.SourceFileKey, CancellationToken.None);
            if (!keepResult)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} was canceled by its owner; its files are deleted")]
    private static partial void LogCanceled(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: could not check for a cancel; the conversion goes on")]
    private static partial void LogCancellationCheckFailed(ILogger logger, Guid jobId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId}: the converter refused the input")]
    private static partial void LogRejected(ILogger logger, Guid jobId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove files of job {JobId}")]
    private static partial void LogCleanupFailed(ILogger logger, Guid jobId, Exception exception);
}
