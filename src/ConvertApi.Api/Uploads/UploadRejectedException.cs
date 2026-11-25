namespace ConvertApi.Api.Uploads;

/// <summary>An upload the API refuses; the endpoint turns it into a Problem Details response.</summary>
internal sealed class UploadRejectedException(int statusCode, string title, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;

    public string Title { get; } = title;

    public static UploadRejectedException BadRequest(string detail) =>
        new(StatusCodes.Status400BadRequest, "Invalid upload", detail);
}
