using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace ConvertApi.Api.Web;

/// <summary>
/// Serves the web front (web/, built by `nuxt generate` into Web:Root, wwwroot by default) from the
/// API's own origin, so the page calls /v1 without CORS. Without built files nothing is served.
/// </summary>
public static partial class WebFront
{
    private const string RootKey = "Web:Root";

    // Built assets carry a content hash in their name: they never change under the same URL.
    private const string ImmutableAssets = "public, max-age=31536000, immutable";

    public static WebApplication UseWebFront(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var root = app.Configuration[RootKey] ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        var index = Path.Combine(root, "index.html");
        if (!File.Exists(index))
        {
            return app;
        }

        var files = new PhysicalFileProvider(Path.GetFullPath(root));

        // One policy per page, from its own inline scripts; built files never change while running.
        var policies = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
            OnPrepareResponse = context =>
            {
                var headers = context.Context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    headers.ContentSecurityPolicy = policies.GetOrAdd(
                        context.File.PhysicalPath!, path => ContentSecurityPolicy(File.ReadAllText(path)));
                    headers.CacheControl = "no-cache";
                }
                else if (context.Context.Request.Path.StartsWithSegments("/_nuxt", StringComparison.Ordinal))
                {
                    headers.CacheControl = ImmutableAssets;
                }
            },
        });

        return app;
    }

    /// <summary>
    /// Scripts from this origin only, plus the page's own inline scripts by hash (Nuxt writes its
    /// import map and runtime config inline, and they change with every build): no 'unsafe-inline'.
    /// The page talks to this origin only, and may not be framed.
    /// </summary>
    internal static string ContentSecurityPolicy(string html)
    {
        var hashes = InlineScript().Matches(html)
            // Browsers hash the script as parsed, with line breaks normalized to \n.
            .Select(match => match.Groups["body"].Value.Replace("\r\n", "\n", StringComparison.Ordinal))
            .Select(body => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}'");

        return string.Join("; ",
            "default-src 'self'",
            $"script-src 'self' {string.Join(' ', hashes)}".TrimEnd(),
            "style-src 'self'",
            "img-src 'self' data:",
            "connect-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'");
    }

    // Executable inline scripts: no src, and not a JSON data block (which the browser never runs).
    [GeneratedRegex("""<script(?![^>]*\ssrc=)(?![^>]*type="application/json")[^>]*>(?<body>.*?)</script>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineScript();
}
