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

        return app;
    }

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
