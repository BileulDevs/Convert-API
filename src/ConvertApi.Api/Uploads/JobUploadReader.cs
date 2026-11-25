using ConvertApi.Core.Formats;
using ConvertApi.Core.Storage;
using ConvertApi.Core.Webhooks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ConvertApi.Api.Uploads;

/// <summary>A file the client sent, stored and checked, ready to become a job.</summary>
public sealed record JobUpload(string SourceFileKey, string SourceFormat, string TargetFormat, Uri? CallbackUrl);

/// <summary>
/// Reads a multipart job request part by part. The file is streamed straight to storage
/// (never buffered whole in memory or on disk by ASP.NET) after its first bytes have been
/// checked; anything stored is deleted again if the request is rejected or aborted.
/// </summary>
internal sealed class JobUploadReader(IFileStorage storage, IOptions<UploadOptions> options)
{
    private const string FilePart = "file";
    private const string TargetFormatPart = "targetFormat";
    private const string CallbackUrlPart = "callbackUrl";
    private const int MaxParts = 8;
    private const int MaxFieldLength = 64;
    private const long MultipartOverheadBytes = 1024 * 1024;

    // Kestrel's default (240 B/s) would let a trickling client hold a connection for days.
    private const double MinBytesPerSecond = 16 * 1024;
    private static readonly TimeSpan MinRateGracePeriod = TimeSpan.FromSeconds(10);

    private readonly UploadOptions _options = options.Value;

    public async Task<JobUpload> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var boundary = GetBoundary(request);
        ApplyBodyLimits(request);
        var reader = new MultipartReader(boundary, request.Body) { BodyLengthLimit = _options.LargestFileBytes };

        StoredSource? source = null;
        string? targetFormat = null;
        Uri? callbackUrl = null;
        try
        {
            var parts = 0;
            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                if (++parts > MaxParts)
                {
                    throw UploadRejectedException.BadRequest($"A job request has at most {MaxParts} parts.");
                }

                var disposition = section.GetContentDispositionHeader()
                    ?? throw UploadRejectedException.BadRequest("Every part needs a Content-Disposition header.");

                switch (HeaderUtilities.RemoveQuotes(disposition.Name).Value)
                {
                    case FilePart when disposition.IsFileDisposition():
                        source = source is null
                            ? await StoreAsync(section.Body, targetFormat, cancellationToken)
                            : throw UploadRejectedException.BadRequest("Send a single file per job.");
                        break;
                    case TargetFormatPart when disposition.IsFormDisposition():
                        targetFormat = await ReadFieldAsync(section.Body, MaxFieldLength, cancellationToken);
                        break;
                    case CallbackUrlPart when disposition.IsFormDisposition():
                        callbackUrl = ParseCallbackUrl(
                            await ReadFieldAsync(section.Body, CallbackUrl.MaxLength, cancellationToken));
                        break;
                    default:
                        throw UploadRejectedException.BadRequest(
                            $"Only a '{FilePart}' file part and '{TargetFormatPart}' and '{CallbackUrlPart}' fields are accepted.");
                }
            }

            return Validate(source, targetFormat) with { CallbackUrl = callbackUrl };
        }
        catch (Exception exception)
        {
            if (source is not null)
            {
                await storage.DeleteAsync(source.Key, CancellationToken.None);
            }

            if (exception is InvalidDataException)
            {
                throw UploadRejectedException.BadRequest("The multipart body is malformed or exceeds a limit.");
            }

            if (exception is Microsoft.AspNetCore.Http.BadHttpRequestException badRequest)
            {
                throw new UploadRejectedException(
                    badRequest.StatusCode, "Invalid request body", "The request body could not be read.");
            }

            throw;
        }
    }

    private static string GetBoundary(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new UploadRejectedException(
                StatusCodes.Status415UnsupportedMediaType, "Unsupported media type", "Send the job as multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        return string.IsNullOrWhiteSpace(boundary)
            ? throw UploadRejectedException.BadRequest("The multipart boundary is missing.")
            : boundary;
    }

    /// <summary>
    /// Rejects a body declared too large up front, raises Kestrel's 30 MB default to the
    /// largest family limit for this request only, and requires a minimum upload rate.
    /// </summary>
    private void ApplyBodyLimits(HttpRequest request)
    {
        var maxBodyBytes = _options.LargestFileBytes + MultipartOverheadBytes;
        if (request.ContentLength > maxBodyBytes)
        {
            throw new UploadRejectedException(
                StatusCodes.Status413PayloadTooLarge, "Request too large", $"Requests are limited to {maxBodyBytes} bytes.");
        }

        var features = request.HttpContext.Features;
        if (features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = maxBodyBytes;
        }

        if (features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rateLimit)
        {
            rateLimit.MinDataRate = new MinDataRate(MinBytesPerSecond, MinRateGracePeriod);
        }
    }

    private async Task<StoredSource> StoreAsync(Stream body, string? targetFormat, CancellationToken cancellationToken)
    {
        var header = new byte[FileSignatureDetector.HeaderLength];
        var headerLength = await body.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);

        var format = FileSignatureDetector.Detect(header.AsSpan(0, headerLength));
        if (format is null || FormatCatalog.SourceFamilyOf(format) is not { } family)
        {
            throw new UploadRejectedException(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported file type",
                "The file content does not match any supported format. Formats are recognized from the content, not the file name.");
        }

        // When the target came first, refuse an impossible conversion before writing anything.
        if (targetFormat is not null)
        {
            EnsureConvertible(format, targetFormat);
        }

        var key = StorageKeys.NewUpload();
        var content = new PrefixedReadStream(header.AsMemory(0, headerLength), body);
        await using (content.ConfigureAwait(false))
        {
            try
            {
                await storage.SaveAsync(key, content, _options.MaxBytesFor(family), cancellationToken);
            }
            catch (FileTooLargeException tooLarge)
            {
                throw new UploadRejectedException(
                    StatusCodes.Status413PayloadTooLarge,
                    "File too large",
                    $"{family} files are limited to {tooLarge.MaxBytes} bytes.");
            }
        }

        return new StoredSource(key, format);
    }

    private static async Task<string> ReadFieldAsync(Stream body, int maxLength, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(body);
        var buffer = new char[maxLength + 1];
        var length = await reader.ReadBlockAsync(buffer, cancellationToken);
        return length > maxLength
            ? throw UploadRejectedException.BadRequest($"This form field is limited to {maxLength} characters.")
            : new string(buffer, 0, length);
    }

    private static JobUpload Validate(StoredSource? source, string? targetFormat)
    {
        if (source is null)
        {
            throw UploadRejectedException.BadRequest($"A '{FilePart}' part with the file to convert is required.");
        }

        if (string.IsNullOrWhiteSpace(targetFormat))
        {
            throw UploadRejectedException.BadRequest($"A '{TargetFormatPart}' field is required.");
        }

        return new JobUpload(source.Key, source.Format, EnsureConvertible(source.Format, targetFormat), CallbackUrl: null);
    }

    private static Uri ParseCallbackUrl(string value) =>
        CallbackUrl.TryParse(value.Trim(), out var url, out var error)
            ? url
            : throw UploadRejectedException.BadRequest(error);

    /// <returns>The normalized target format.</returns>
    private static string EnsureConvertible(string sourceFormat, string targetFormat)
    {
        if (FormatId.TryNormalize(targetFormat, out var target) && FormatCatalog.IsSupported(sourceFormat, target))
        {
            return target;
        }

        throw new UploadRejectedException(
            StatusCodes.Status422UnprocessableEntity,
            "Unsupported conversion",
            $"A {sourceFormat} file can be converted to: {string.Join(", ", FormatCatalog.TargetsFor(sourceFormat))}.");
    }

    private sealed record StoredSource(string Key, string Format);
}
