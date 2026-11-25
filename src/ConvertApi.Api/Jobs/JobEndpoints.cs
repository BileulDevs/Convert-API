using System.Security.Claims;
using ConvertApi.Api.Auth;
using ConvertApi.Api.Uploads;
using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ConvertApi.Api.Jobs;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/v1/jobs")
            .WithTags("Jobs")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

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

        return app;
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
        ClaimsPrincipal user,
        JobUploadReader uploads,
        IJobRepository jobs,
        IFileStorage storage,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
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
