namespace ConvertApi.Api.Webhooks;

public static class WebhookServices
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddWebhooks(this IServiceCollection services)
    {
        services.AddOptions<WebhookOptions>()
            .BindConfiguration(WebhookOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // RemoveAllResilienceHandlers (below) is marked experimental; it only undoes the defaults
        // ServiceDefaults adds to every client, which would retry webhooks on the spot.
#pragma warning disable EXTEXP0001
        services.AddHttpClient<WebhookSender>(http =>
            {
                http.Timeout = RequestTimeout;

                // HTTP/3 runs over QUIC, which would bypass the connect callback below: never negotiate it.
                http.DefaultRequestVersion = System.Net.HttpVersion.Version11;
                http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ConvertApi-Webhooks/1.0");
            })
            // The callback host is the client's choice: connect only to the public address checked
            // here, never through a proxy (which would hide the target), and follow no redirect.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectCallback = PublicOnlyConnector.ConnectAsync,
                ConnectTimeout = ConnectTimeout,
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            // Retries are scheduled per job by WebhookDelivery, not repeated on the spot.
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        services.AddHostedService<WebhookDelivery>();
        return services;
    }
}
