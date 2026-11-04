namespace LeaseReminder.Api.Security;

/// <summary>
/// Sets the response headers required by section 4.F.
///
/// <para>
/// These are set for the API rather than only for the frontend because the API
/// serves JSON that a browser can be tricked into navigating to directly. A
/// JSON response rendered as a document, with no nosniff and no CSP, has been
/// a reliable source of content-sniffing and clickjacking problems for years.
/// </para>
///
/// <para>
/// The policy here is deliberately severe. The API has no UI: it needs no
/// scripts, no styles, no frames, and no images. So it declares that it needs
/// none of them, which means a response that somehow contained markup could
/// not execute anything. The frontend ships its own, necessarily looser, CSP.
/// </para>
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>
    /// default-src 'none' is the correct baseline for a pure JSON API: deny
    /// everything, then grant nothing back. frame-ancestors 'none' is the
    /// modern replacement for X-Frame-Options and prevents the API being
    /// framed as part of a clickjacking attack.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // OnStarting, so the headers are present even on a response written by
        // an exception handler or a short-circuiting middleware further in.
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers["Content-Security-Policy"] = ContentSecurityPolicy;

            // Stops a browser guessing a content type and treating a JSON
            // response as HTML or script.
            headers["X-Content-Type-Options"] = "nosniff";

            // Kept alongside frame-ancestors for older browsers.
            headers["X-Frame-Options"] = "DENY";

            // No referrer at all. The API's URLs contain resource identifiers,
            // and there is no third party that needs to learn them.
            headers["Referrer-Policy"] = "no-referrer";

            // The API needs none of these device capabilities. Declining them
            // explicitly costs nothing and removes them from any embedding
            // context.
            headers["Permissions-Policy"] =
                "accelerometer=(), camera=(), geolocation=(), gyroscope=(), " +
                "magnetometer=(), microphone=(), payment=(), usb=()";

            // Opt out of cross-origin resource sharing of the response body by
            // default, so another origin cannot read it even via a side
            // channel.
            headers["Cross-Origin-Resource-Policy"] = "same-origin";

            // Lease and tenant data should not be cached by an intermediary or
            // left in a browser's disk cache.
            headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            headers["Pragma"] = "no-cache";

            // Server and framework banners tell an attacker what to target and
            // tell a legitimate client nothing (section 4.F).
            headers.Remove("Server");
            headers.Remove("X-Powered-By");
            headers.Remove("X-AspNet-Version");

            return Task.CompletedTask;
        });

        await next(context).ConfigureAwait(false);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
