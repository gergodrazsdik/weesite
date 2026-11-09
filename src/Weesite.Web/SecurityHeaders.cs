using Microsoft.Net.Http.Headers;

namespace Weesite.Web;

/// <summary>
/// The security headers every response gets (brief, Security hardening; T-0053): a strict
/// Content Security Policy with no inline scripts or styles, <c>nosniff</c>, a referrer policy,
/// a minimal permissions policy, and no framing. HSTS comes from <c>UseHsts</c> on HTTPS.
/// </summary>
internal static class SecurityHeaders
{
    /// <summary>The policy of every page: everything from this site, images also as <c>data:</c> URLs.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; " +
        "base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// The sign-in page's policy: its form posts here and is redirected to the identity providers,
    /// and browsers check <c>form-action</c> against that redirect too.
    /// </summary>
    public const string SignInContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; " +
        "base-uri 'self'; form-action 'self' https://login.microsoftonline.com https://accounts.google.com; frame-ancestors 'none'";

    /// <summary>Features no page uses.</summary>
    public const string PermissionsPolicy =
        "camera=(), microphone=(), geolocation=(), payment=(), usb=(), browsing-topics=()";

    /// <summary>Adds the headers to every response.</summary>
    /// <param name="app">The app.</param>
    public static void UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers[HeaderNames.ContentSecurityPolicy] = context.Request.Path.Equals("/admin/sign-in", StringComparison.OrdinalIgnoreCase)
                ? SignInContentSecurityPolicy
                : ContentSecurityPolicy;
            headers[HeaderNames.XContentTypeOptions] = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = PermissionsPolicy;
            await next(context);
        });
}
