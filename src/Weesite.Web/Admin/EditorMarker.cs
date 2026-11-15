namespace Weesite.Web.Admin;

/// <summary>
/// A cookie that marks a browser an editor signs in with, for a month after their last visit to
/// <c>/admin</c>: its page views on the public site aren't counted, even when signed out, and the
/// header shows a link to the admin (editor-link.js). It grants nothing: the admin still asks for
/// a sign-in, so anyone may set it to see the link.
/// </summary>
internal static class EditorMarker
{
    /// <summary>The cookie's name. Not HttpOnly: the header's script reads it.</summary>
    public const string CookieName = "weesite.editor";

    /// <summary>How long it lasts after the last visit to the admin.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>Gets whether a request comes from a marked browser.</summary>
    /// <param name="request">The request.</param>
    /// <returns>True when the cookie is there.</returns>
    public static bool IsMarked(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies.ContainsKey(CookieName);
    }

    /// <summary>Sets the cookie, or renews it for another <see cref="Lifetime"/>.</summary>
    /// <param name="context">The response's request.</param>
    public static void Mark(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Cookies.Append(CookieName, "1", new CookieOptions
        {
            Path = "/",
            MaxAge = Lifetime,
            HttpOnly = false,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
        });
    }

    /// <summary>
    /// Renews the cookie on every <c>/admin</c> response to a signed-in editor, once the response
    /// knows who the user is (authentication runs after this middleware).
    /// </summary>
    /// <param name="app">The app.</param>
    public static void UseEditorMarker(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.OnStarting(() =>
                {
                    if (context.User.IsInRole(nameof(EditorRole.Admin)) || context.User.IsInRole(nameof(EditorRole.Author)))
                    {
                        Mark(context);
                    }

                    return Task.CompletedTask;
                });
            }

            await next(context);
        });
}
