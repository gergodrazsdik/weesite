using Weesite.Content;

namespace Weesite.Web;

/// <summary>
/// Sends earlier slugs (<c>redirect_from</c> in front matter) to the current URL with a 301, so
/// renaming a post or page never breaks a link. Runs before the pages, only for GET and HEAD,
/// and only when no current item uses the slug.
/// </summary>
internal static class ContentRedirects
{
    /// <summary>Adds the redirects.</summary>
    /// <param name="app">The app.</param>
    public static void UseContentRedirects(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) && Target(request.Path, context) is { } target)
            {
                context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                context.Response.Headers.Location = target;
                return;
            }

            await next(context);
        });

    private static string? Target(PathString path, HttpContext context)
    {
        var segments = (path.Value ?? "").Trim('/').Split('/');
        var catalog = context.RequestServices.GetRequiredService<ContentCatalogProvider>().Current;
        return segments switch
        {
            // Percent-encoded: a Location header must be ASCII, and a slug may be in any script (F-0113).
            ["blog", var slug] when catalog.FindPost(slug) is null && catalog.FindPostRedirect(slug) is { } to => $"/blog/{Uri.EscapeDataString(to)}",
            [var slug] when slug.Length > 0 && catalog.FindPage(slug) is null && catalog.FindPageRedirect(slug) is { } to => $"/{Uri.EscapeDataString(to)}",
            _ => null,
        };
    }
}
