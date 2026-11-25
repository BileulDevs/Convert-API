using System.ComponentModel.DataAnnotations;
using ConvertApi.Core.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConvertApi.Api.Webhooks;

/// <summary>Bound from "Webhooks".</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>How long an idle sender waits before looking for due notifications again.</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:01:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Delivers due job notifications, at least once: a notification whose sender dies mid-way is sent
/// again once its lease runs out, so receivers should ignore a job id and status they already handled.
/// A failed delivery is retried after a growing delay, up to <see cref="RetryDelays"/>.Length + 1 attempts.
/// Runs in the API, not the worker, so conversion workers can stay without internet access.
/// </summary>
public sealed partial class WebhookDelivery(
    IServiceScopeFactory scopes,
    IOptions<WebhookOptions> options,
    TimeProvider clock,
    ILogger<WebhookDelivery> logger) : BackgroundService
{
    public const int BatchSize = 10;

    /// <summary>Longer than a batch can take to send (each request times out first), so a live sender never loses its claim.</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)];

    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DeliverBatchAsync(stoppingToken) == 0)
                {
                    await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A failing batch must not stop deliveries; its jobs come back once their lease runs out.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogBatchFailed(logger, exception);
                await Task.Delay(ErrorBackoff, clock, stoppingToken);
            }
        }
    }

    /// <returns>How many notifications were attempted.</returns>
    private async Task<int> DeliverBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var due = await jobs.ClaimDueNotificationsAsync(clock.GetUtcNow(), Lease, BatchSize, cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        // Sent side by side, so one slow receiver cannot hold up every other client's
        // notifications; the jobs are then saved one by one, as the DbContext is not thread-safe.
        var sender = scope.ServiceProvider.GetRequiredService<WebhookSender>();
        var outcomes = await Task.WhenAll(due.Select(job => TrySendAsync(sender, job, cancellationToken)));
        for (var i = 0; i < due.Count; i++)
        {
            var (job, delivered) = (due[i], outcomes[i]);
            if (delivered || job.NotificationAttempts > RetryDelays.Length)
            {
                job.CompleteNotification();
                LogDone(logger, job.Id, delivered, job.NotificationAttempts);
            }
            else
            {
                job.RetryNotification(clock.GetUtcNow() + RetryDelays[job.NotificationAttempts - 1]);
            }

            try
            {
                await jobs.SaveAsync(job, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The job changed meanwhile (its result was deleted, say): it comes back once its
                // lease runs out, so a delivered notification may be sent once more.
                LogLostRace(logger, job.Id);
            }
        }

        return due.Count;
    }

    private async Task<bool> TrySendAsync(WebhookSender sender, ConversionJob job, CancellationToken cancellationToken)
    {
        try
        {
            return await sender.SendAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // One job's unexpected error must not lose the others' outcomes; it is retried like a refusal.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogSendFailed(logger, job.Id, exception);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId}: notification delivered: {Delivered}, after {Attempts} attempts")]
    private static partial void LogDone(ILogger logger, Guid jobId, bool delivered, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: sending its notification failed unexpectedly; it will be retried")]
    private static partial void LogSendFailed(ILogger logger, Guid jobId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} changed while its notification was sent; it comes back after its lease")]
    private static partial void LogLostRace(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivering webhooks failed; trying again shortly")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);
}
