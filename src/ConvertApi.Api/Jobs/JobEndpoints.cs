using System.Security.Claims;
using ConvertApi.Api.Auth;
using ConvertApi.Api.RateLimiting;
using ConvertApi.Api.Uploads;
using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConvertApi.Api.Jobs;

public static class JobEndpoints
{
    /// <summary>When a quota slot frees up depends on the conversions; this is a reasonable time to look again.</summary>
    private const string QuotaRetryAfterSeconds = "30";

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/v1/jobs")
            .WithTags("Jobs")
            .RequireAuthorization()
            .RequireRateLimiting(ApiKeyRateLimiting.PolicyName)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        jobs.MapPost("/", CreateJobAsync)
            .WithName("CreateJob")
            .WithSummary("Upload a file and queue its conversion")
            .Accepts<CreateJobForm>("multipart/form-data")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        jobs.MapGet("/{id:guid}", GetJobAsync)
            .WithName("GetJob")
            .WithSummary("Read the state of a job");

        jobs.MapGet("/{id:guid}/result", DownloadResultAsync)
            .WithName("GetJobResult")
            .WithSummary("Download the converted file (supports Range requests)")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone);

        jobs.MapDelete("/{id:guid}", DeleteJobAsync)
            .WithName("DeleteJob")
            .WithSummary("Cancel a job that has not finished, or delete its result")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>
    /// A queued or running job is canceled (its worker notices and stops); a succeeded job has its
    /// result deleted and expires. A finished job is left as it is, so a repeated call still answers 204.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteJobAsync(
        Guid id,
        ClaimsPrincipal user,
        IJobRepository jobs,
        IFileStorage storage,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(id, user.GetApiKeyId(), cancellationToken);
        if (job is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found");
        }

        if (job.IsTerminal)
        {
            return TypedResults.NoContent();
        }

        var resultKey = StorageKeys.ResultOf(job.Id);
        if (job.Status == JobStatus.Succeeded)
        {
            // File first: the sweep only looks at succeeded jobs, so an expired job must not keep a file.
            await storage.DeleteAsync(resultKey, cancellationToken);
            job.Expire(clock.GetUtcNow());
            return await TrySaveAsync(job, jobs, cancellationToken);
        }

        // Cancel first: a worker must not see its source vanish and fail the job before the cancel lands.
        job.Cancel(clock.GetUtcNow());
        var saved = await TrySaveAsync(job, jobs, cancellationToken);
        if (saved.Result is NoContent)
        {
            // A running worker may still write a result after this; it deletes it once it sees the cancel.
            // shortcut: a failure here leaves the upload behind for good; add an orphan sweep if it shows up in logs.
            await storage.DeleteAsync(job.SourceFileKey, cancellationToken);
            await storage.DeleteAsync(resultKey, cancellationToken);
        }

        return saved;
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> TrySaveAsync(
        ConversionJob job, IJobRepository jobs, CancellationToken cancellationToken)
    {
        try
        {
            await jobs.SaveAsync(job, cancellationToken);
            return TypedResults.NoContent();
        }
        catch (DbUpdateConcurrencyException)
        {
            // A worker claimed or finished the job in the meantime: its new state decides what a retry does.
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Job changed",
                detail: "The job changed while it was being deleted; send the request again.");
        }
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> DownloadResultAsync(
        Guid id,
        ClaimsPrincipal user,
        HttpResponse response,
        IJobRepository jobs,
        IFileStorage storage,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(id, user.GetApiKeyId(), cancellationToken);
        if (job is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found");
        }

        if (job.Status != JobStatus.Succeeded)
        {
            return ResultUnavailable(job);
        }

        if (job.ExpiresAt <= clock.GetUtcNow())
        {
            return ResultGone();
        }

        Stream result;
        try
        {
            result = await storage.OpenReadAsync(StorageKeys.ResultOf(job.Id), cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ResultGone();
        }

        // Always a download, never rendered: an HTML result must not run in the client's origin.
        // Results are private to their key, so no shared cache may keep them.
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = "private, no-store";
        response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        return TypedResults.Stream(
            result,
            MediaTypes.Of(job.TargetFormat),
            fileDownloadName: $"{job.Id}.{job.TargetFormat}",
            enableRangeProcessing: true);
    }

    private static ProblemHttpResult ResultUnavailable(ConversionJob job) => job.Status switch
    {
        JobStatus.Expired => ResultGone(),
        JobStatus.Failed => TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "No result", detail: $"The job failed: {job.FailureReason}"),
        JobStatus.Canceled => TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "No result", detail: "The job was canceled."),
        _ => TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "Result not ready", detail: "The job has not finished yet."),
    };

    private static ProblemHttpResult ResultGone() => TypedResults.Problem(
        statusCode: StatusCodes.Status410Gone,
        title: "Result expired",
        detail: "The result was deleted after its retention period.");

    private static async Task<Results<Accepted<JobResponse>, ProblemHttpResult>> CreateJobAsync(
        HttpRequest request,
        HttpResponse response,
        ClaimsPrincipal user,
        JobUploadReader uploads,
        IJobRepository jobs,
        IFileStorage storage,
        IOptions<RateLimitOptions> limits,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // Checked before reading the upload, so a refused request costs no disk or bandwidth.
        // shortcut: two uploads racing past the check can exceed the quota by one each; lock per key if that matters.
        var maxActiveJobs = limits.Value.MaxActiveJobs;
        if (await jobs.CountActiveAsync(user.GetApiKeyId(), cancellationToken) >= maxActiveJobs)
        {
            response.Headers.RetryAfter = QuotaRetryAfterSeconds;
            return TypedResults.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many active jobs",
                detail: $"This API key already has {maxActiveJobs} jobs queued or running; wait for one to finish.",
                type: "https://tools.ietf.org/html/rfc6585#section-4");
        }

        JobUpload upload;
        try
        {
            upload = await uploads.ReadAsync(request, cancellationToken);
        }
        catch (UploadRejectedException rejected)
        {
            return TypedResults.Problem(statusCode: rejected.StatusCode, title: rejected.Title, detail: rejected.Message);
        }

        var job = ConversionJob.Create(
            user.GetApiKeyId(), upload.SourceFileKey, upload.SourceFormat, upload.TargetFormat, clock.GetUtcNow());
        try
        {
            await jobs.AddAsync(job, cancellationToken);
        }
        catch
        {
            // Without a job, nothing would ever clean the stored file up.
            await storage.DeleteAsync(upload.SourceFileKey, CancellationToken.None);
            throw;
        }

        return TypedResults.Accepted($"/v1/jobs/{job.Id}", JobResponse.From(job));
    }

    private static async Task<Results<Ok<JobResponse>, ProblemHttpResult>> GetJobAsync(
        Guid id, ClaimsPrincipal user, IJobRepository jobs, CancellationToken cancellationToken)
    {
        // A job of another key answers 404, not 403: its existence is not revealed.
        var job = await jobs.FindAsync(id, user.GetApiKeyId(), cancellationToken);
        return job is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found")
            : TypedResults.Ok(JobResponse.From(job));
    }

    /// <summary>Documents the multipart body for OpenAPI; the endpoint reads the stream itself.</summary>
    internal sealed record CreateJobForm(IFormFile File, string TargetFormat);
}
