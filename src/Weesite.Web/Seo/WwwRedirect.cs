namespace Weesite.Web.Seo;

/// <summary>
/// Sends requests for <c>www.</c> and the canonical domain to the domain itself (T-0057), with the
/// path and query: <c>https://www.example.com/blog?page=2</c> to
/// <c>https://example.com/blog?page=2</c>. Permanent, so search engines keep one address; GET and
/// HEAD get a 301, other methods a 308, which keeps the method and body. Other hosts, such as the
/// Container Apps hostname, are served as they are.
/// </summary>
internal static class WwwRedirect
{
    /// <summary>Adds the redirect when <c>Site:BaseUrl</c> is set.</summary>
    /// <param name="app">The application.</param>
    /// <returns>The application, for chaining.</returns>
    public static IApplicationBuilder UseWwwRedirect(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var urls = app.ApplicationServices.GetRequiredService<SiteUrls>();
        if (urls.WwwHost is not { } www)
        {
            return app;
        }

        return app.Use((context, next) =>
        {
            var request = context.Request;
            if (!string.Equals(request.Host.Host, www, StringComparison.OrdinalIgnoreCase))
            {
                return next(context);
            }

            var target = urls.Absolute(request, request.Path.ToUriComponent() + request.QueryString.ToUriComponent());
            var keepMethod = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);
            context.Response.Redirect(target, permanent: true, preserveMethod: keepMethod);
            return Task.CompletedTask;
        });
    }
}
