namespace Weesite.Web.Seo;

/// <summary>
/// Absolute URLs for canonical links, Open Graph, feeds and the sitemap. The origin is
/// <c>Site:BaseUrl</c> when it's set (a deployment's canonical domain), otherwise the request's
/// own scheme and host (the scheme from <c>X-Forwarded-Proto</c> behind the ingress), which is
/// right only where the app answers nothing but its own host names: set <c>Site:BaseUrl</c> outside
/// local runs.
/// </summary>
internal sealed class SiteUrls
{
    private readonly string? _baseUrl;

    /// <summary>Initializes a new instance of the <see cref="SiteUrls"/> class.</summary>
    /// <param name="configuration">The configuration (<c>Site:BaseUrl</c>).</param>
    public SiteUrls(IConfiguration configuration)
    {
        var value = configuration["Site:BaseUrl"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"Site:BaseUrl must be an absolute http(s) URL without a query, such as https://example.com; got '{value}'.");
        }

        _baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (uri.HostNameType == UriHostNameType.Dns && !uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            WwwHost = "www." + uri.IdnHost;
        }
    }

    /// <summary>
    /// Gets <c>www.</c> and the canonical domain's host, such as <c>www.example.com</c>, whose
    /// requests redirect to the domain (<see cref="WwwRedirect"/>); <see langword="null"/> without
    /// <c>Site:BaseUrl</c> or when its host starts with <c>www.</c> itself.
    /// </summary>
    public string? WwwHost { get; }

    /// <summary>
    /// Gets <c>Site:BaseUrl</c> without a trailing slash, or null when it isn't set: the origin
    /// outside a request, and in what the site sends to others, such as the digests' links, which
    /// must not follow the Host a visitor's request named.
    /// </summary>
    public string? BaseUrl => _baseUrl;

    /// <summary>Gets the origin (and base path) without a trailing slash.</summary>
    /// <param name="request">The current request.</param>
    /// <returns>Such as <c>https://example.com</c>.</returns>
    public string Origin(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _baseUrl ?? $"{request.Scheme}://{request.Host}{request.PathBase}";
    }

    /// <summary>Gets the absolute URL of a path on this site.</summary>
    /// <param name="request">The current request.</param>
    /// <param name="path">A path starting with <c>/</c>.</param>
    /// <returns>The absolute URL.</returns>
    public string Absolute(HttpRequest request, string path) => Origin(request) + path;
}
