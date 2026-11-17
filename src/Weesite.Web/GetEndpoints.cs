namespace Weesite.Web;

/// <summary>
/// Endpoints that answer HEAD as well as GET (F-0171). <c>MapGet</c> matches GET only, so a HEAD
/// request, as link checkers, crawlers and some CDNs send, found no endpoint: 405 on
/// <c>/media</c>, and the not-found page on the feeds, the sitemap and <c>llms.txt</c>. For HEAD,
/// Kestrel and the file results leave out the body and keep the headers.
/// </summary>
internal static class GetEndpoints
{
    private static readonly string[] GetAndHead = [HttpMethods.Get, HttpMethods.Head];

    /// <summary>Maps a GET endpoint that answers HEAD too.</summary>
    /// <param name="app">The app.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The endpoint's builder.</returns>
    public static RouteHandlerBuilder MapGetAndHead(this IEndpointRouteBuilder app, string pattern, Delegate handler) =>
        app.MapMethods(pattern, GetAndHead, handler);
}
