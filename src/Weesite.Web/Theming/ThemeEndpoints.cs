using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Weesite.Content;

namespace Weesite.Web.Theming;

/// <summary>The visitor's System/Light/Dark choice, kept in a functional cookie.</summary>
internal static class ThemePreference
{
    /// <summary>The cookie's name.</summary>
    public const string Cookie = "theme";

    /// <summary>Reads the choice: <c>light</c>, <c>dark</c>, or null for the system's.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The mode, or null.</returns>
    public static string? Read(HttpContext? context) =>
        context?.Request.Cookies[Cookie] is "light" or "dark" ? context.Request.Cookies[Cookie] : null;
}

/// <summary>Maps <c>POST /theme</c> (the switcher's form) and <c>/theme.{hash}.css</c>.</summary>
internal static class ThemeEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The app.</param>
    public static void MapTheme(this IEndpointRouteBuilder app)
    {
        // The switcher posts here and comes back to the same page: the choice works without
        // JavaScript, and the server renders it from the first byte. No antiforgery check: a
        // forged post can only change a colour preference, and a token on every page would make
        // every public page uncacheable (F-0032).
        app.MapPost("/theme", ([FromForm] string mode, [FromForm] string? returnUrl, HttpContext context) =>
        {
            if (mode is "light" or "dark")
            {
                context.Response.Cookies.Append(ThemePreference.Cookie, mode, new CookieOptions
                {
                    Path = "/",
                    MaxAge = TimeSpan.FromDays(365),
                    SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                    HttpOnly = true,
                    IsEssential = true,
                });
            }
            else
            {
                context.Response.Cookies.Delete(ThemePreference.Cookie, new CookieOptions { Path = "/" });
            }

            // ws-theme-switch asks for JSON: the cookie is set, there's nothing to go back to.
            if (context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NoContent();
            }

            // Only paths on this site: "/x", never "//host" or "/\\host".
            var target = returnUrl is { Length: > 0 } && returnUrl[0] == '/' && (returnUrl.Length == 1 || returnUrl[1] is not '/' and not '\\')
                ? returnUrl
                : "/";
            return Results.LocalRedirect(target);
        }).DisableAntiforgery();

        app.MapGet("/theme.{hash}.css", (string hash, ContentCatalogProvider content, HttpContext context) =>
        {
            if (ThemeStylesheet.Build(content.Current.Site) is not { } theme)
            {
                return Results.NotFound();
            }

            if (!string.Equals(hash, theme.Hash, StringComparison.Ordinal))
            {
                return Results.Redirect(theme.Url);
            }

            context.Response.Headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
            context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            return Results.Text(theme.Css, "text/css; charset=utf-8");
        });
    }
}
