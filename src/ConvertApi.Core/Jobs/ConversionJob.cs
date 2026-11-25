using ConvertApi.Core.Formats;

namespace ConvertApi.Core.Jobs;

/// <summary>
/// A request to convert one file into a target format, and its progress through
/// <see cref="JobStateMachine"/>. State only changes through the methods below, each of
/// which checks the transition first and records it; a rejected call changes nothing.
/// </summary>
/// <remarks>
/// Callers pass the current time in, so the aggregate stays free of services.
/// A duplicate queue message shows up as an <see cref="InvalidJobTransitionException"/>
/// (for example Start on a succeeded job): consumers must acknowledge it, not retry it.
/// </remarks>
public sealed class ConversionJob
{
    private readonly List<JobTransition> _transitions = [];

    private ConversionJob(Guid id, Guid ownerId, string sourceFileKey, string sourceFormat, string targetFormat, DateTimeOffset createdAt)
    {
        Id = id;
        OwnerId = ownerId;
        SourceFileKey = sourceFileKey;
        SourceFormat = sourceFormat;
        TargetFormat = targetFormat;
        CreatedAt = createdAt;
        Status = JobStatus.Queued;
    }

    public Guid Id { get; }

    /// <summary>The API key that created the job; other keys must not see it.</summary>
    public Guid OwnerId { get; }

    /// <summary>Storage key of the uploaded file.</summary>
    public string SourceFileKey { get; }

    /// <summary>Lower-case format id without a leading dot, e.g. "mp4".</summary>
    public string SourceFormat { get; }

    /// <summary>
    /// Lower-case format id without a leading dot, e.g. "mp3". It may equal the source
    /// format: re-encoding or resizing (PNG to PNG at another size) is a valid conversion.
    /// </summary>
    public string TargetFormat { get; }

    public JobStatus Status { get; private set; }

    /// <summary>How many times a worker started the conversion (more than one after a crash).</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>When the latest attempt started.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>When the job succeeded, failed or was canceled.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>When the result will be deleted; set once the job succeeds.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>Where the job's owner wants to be notified when it ends; always passes <see cref="CallbackUrl"/>'s checks.</summary>
    public Uri? CallbackUrl { get; private set; }

    /// <summary>Longest W3C traceparent kept; today's format is 55 characters.</summary>
    public const int MaxTraceParentLength = 128;

    /// <summary>
    /// The W3C traceparent of the request that created the job. The worker starts its own trace from
    /// it, so one trace follows a job from the upload to the conversion, however long it waited.
    /// </summary>
    public string? TraceParent { get; private set; }

    /// <summary>
    /// When the callback should next be called: set when a job with a callback ends, pushed back
    /// while a delivery is in flight or waiting for a retry, cleared once delivered or given up.
    /// </summary>
    public DateTimeOffset? NotificationDueAt { get; private set; }

    /// <summary>How many times delivering the callback was attempted.</summary>
    public int NotificationAttempts { get; private set; }

    public IReadOnlyList<JobTransition> Transitions => _transitions.AsReadOnly();

    public bool IsTerminal => JobStateMachine.IsTerminal(Status);

    public static ConversionJob Create(
        Guid ownerId,
        string sourceFileKey,
        string sourceFormat,
        string targetFormat,
        DateTimeOffset now,
        Uri? callbackUrl = null,
        string? traceParent = null)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("A job must belong to an API key.", nameof(ownerId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileKey);
        if (callbackUrl is not null && !Webhooks.CallbackUrl.IsAllowed(callbackUrl))
        {
            throw new ArgumentException("A callback URL must be HTTPS to a public host.", nameof(callbackUrl));
        }

        var utcNow = now.ToUniversalTime();
        return new ConversionJob(
            Guid.CreateVersion7(utcNow),
            ownerId,
            sourceFileKey,
            NormalizeFormat(sourceFormat, nameof(sourceFormat)),
            NormalizeFormat(targetFormat, nameof(targetFormat)),
            utcNow)
        {
            CallbackUrl = callbackUrl,

            // Diagnostics only: an unexpected value is dropped rather than failing the upload.
            TraceParent = traceParent is { Length: <= MaxTraceParentLength } ? traceParent : null,
        };
    }

    /// <summary>A worker begins (or, after a crash, resumes) the conversion.</summary>
    public void Start(DateTimeOffset now)
    {
        StartedAt = MoveTo(JobStatus.Processing, now);
        Attempts++;
    }

    /// <summary>The result is stored and will be kept for <paramref name="retention"/>.</summary>
    public void Succeed(TimeSpan retention, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        var utcNow = MoveTo(JobStatus.Succeeded, now);
        CompletedAt = utcNow;
        ExpiresAt = utcNow + retention;
        ScheduleNotification(utcNow);
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        CompletedAt = MoveTo(JobStatus.Failed, now);
        FailureReason = reason;
        ScheduleNotification(CompletedAt.Value);
    }

    public void Cancel(DateTimeOffset now)
    {
        CompletedAt = MoveTo(JobStatus.Canceled, now);
        ScheduleNotification(CompletedAt.Value);
    }

    /// <summary>A sender takes the due notification; it comes back at <paramref name="leaseUntil"/> if the sender dies.</summary>
    public void ClaimNotification(DateTimeOffset leaseUntil)
    {
        if (NotificationDueAt is null)
        {
            throw new InvalidOperationException($"Job {Id} has no notification to send.");
        }

        NotificationAttempts++;
        NotificationDueAt = leaseUntil.ToUniversalTime();
    }

    public void RetryNotification(DateTimeOffset at) => NotificationDueAt = at.ToUniversalTime();

    /// <summary>The callback was delivered, or delivering it was given up.</summary>
    public void CompleteNotification() => NotificationDueAt = null;

    private void ScheduleNotification(DateTimeOffset utcNow)
    {
        if (CallbackUrl is not null)
        {
            NotificationDueAt = utcNow;
        }
    }

    /// <summary>
    /// The result has been deleted: at <see cref="ExpiresAt"/>, or earlier on the owner's request.
    /// </summary>
    public void Expire(DateTimeOffset now)
    {
        MoveTo(JobStatus.Expired, now);
    }

    /// <returns>The time of the move, in UTC: every stored time is UTC.</returns>
    private DateTimeOffset MoveTo(JobStatus target, DateTimeOffset now)
    {
        if (!JobStateMachine.CanTransition(Status, target))
        {
            throw new InvalidJobTransitionException(Id, Status, target);
        }

        var utcNow = now.ToUniversalTime();
        _transitions.Add(new JobTransition(Status, target, utcNow));
        Status = target;
        return utcNow;
    }

    private static string NormalizeFormat(string format, string paramName)
    {
        ArgumentNullException.ThrowIfNull(format, paramName);

        return FormatId.TryNormalize(format, out var normalized)
            ? normalized
            : throw new ArgumentException(
                $"A format is 1 to {FormatId.MaxLength} letters or digits, optionally after one dot.", paramName);
    }
}
