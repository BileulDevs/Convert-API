using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text.Json;
using ConvertApi.Api.Auth;
using ConvertApi.Api.Jobs;
using ConvertApi.Core.Jobs;
using ConvertApi.Core.Webhooks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace ConvertApi.Api.Webhooks;

/// <summary>
/// POSTs a finished job to its callback URL: the same JSON as GET /v1/jobs/{id}, signed with the
/// owner key's webhook secret (see <see cref="WebhookSignature"/>). Any 2xx answer counts as delivered.
/// </summary>
public sealed partial class WebhookSender
{
    public const string SignatureHeader = "X-ConvertApi-Signature";

    private readonly HttpClient _http;
    private readonly FrozenDictionary<Guid, string> _secrets;
    private readonly JsonSerializerOptions _json;
    private readonly TimeProvider _clock;
    private readonly ILogger<WebhookSender> _logger;

    public WebhookSender(
        HttpClient http,
        IOptions<ApiKeyOptions> keys,
        IOptions<JsonOptions> json,
        TimeProvider clock,
        ILogger<WebhookSender> logger)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(json);

        _http = http;
        _secrets = keys.Value.Keys.ToFrozenDictionary(key => key.Id, key => key.WebhookSecret);
        _json = json.Value.SerializerOptions;
        _clock = clock;
        _logger = logger;
    }

    /// <returns>Whether the receiver accepted the notification.</returns>
    public async Task<bool> SendAsync(ConversionJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Checked again at send time: the stored URL is data, and the rules may have tightened since.
        if (job.CallbackUrl is not { } url || !CallbackUrl.IsAllowed(url) || !_secrets.TryGetValue(job.OwnerId, out var secret))
        {
            LogNotSendable(_logger, job.Id);
            return false;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(JobResponse.From(job), _json);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        request.Headers.Add(SignatureHeader, WebhookSignature.Create(secret, _clock.GetUtcNow().ToUnixTimeSeconds(), body));

        try
        {
            // Headers are enough: the receiver's body is never read, however large.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            LogRefused(_logger, job.Id, (int)response.StatusCode);
            return false;
        }
        catch (HttpRequestException exception)
        {
            LogUnreachable(_logger, job.Id, exception.Message);
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(_logger, job.Id, "timed out");
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: its callback URL is no longer allowed or its key has no webhook secret")]
    private static partial void LogNotSendable(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId}: the callback answered {StatusCode}")]
    private static partial void LogRefused(ILogger logger, Guid jobId, int statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId}: the callback could not be reached ({Reason})")]
    private static partial void LogUnreachable(ILogger logger, Guid jobId, string reason);
}
