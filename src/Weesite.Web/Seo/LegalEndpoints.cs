using Weesite.Content;

namespace Weesite.Web.Seo;

/// <summary>
/// Addresses that sign-in providers link to (T-0072): <c>/legal/privacy</c> and
/// <c>/legal/terms</c> redirect to the pages Site settings chose for the privacy policy and the
/// terms, so the Entra app and Google's consent screen need no page slugs; and
/// <c>/.well-known/microsoft-identity-association.json</c>, which proves to Entra that the
/// domain belongs to the Microsoft sign-in app (its publisher domain).
/// </summary>
internal static class LegalEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The app.</param>
    public static void MapLegalLinks(this IEndpointRouteBuilder app)
    {
        // Temporary redirects: the chosen page can change. Not found when /privacy or /terms has
        // nothing to show.
        app.MapGetAndHead("/legal/privacy", (ContentCatalogProvider content) => ToPage(content.Current, content.Current.Site.PrivacyPage, "privacy"));
        app.MapGetAndHead("/legal/terms", (ContentCatalogProvider content) => ToPage(content.Current, content.Current.Site.TermsPage, "terms"));

        // Entra reads this when the publisher domain is verified in the portal. The client ID is
        // public anyway: it's in every sign-in request.
        app.MapGetAndHead("/.well-known/microsoft-identity-association.json", (IConfiguration configuration) =>
            configuration["Authentication:Microsoft:ClientId"] is { Length: > 0 } clientId
                ? Results.Json(new { associatedApplications = new[] { new { applicationId = clientId } } })
                : Results.NotFound());
    }

    // The chosen special page, or, as SpecialPage shows, a published page that kept the slug from
    // before special pages until an editor takes it away (F-0218).
    private static IResult ToPage(ContentCatalog catalog, string? path, string slug) =>
        catalog.FindSpecialPage(path) is { } page && ContentUrls.PathOf(page) is { Length: > 0 } address ? Results.Redirect(address)
        : catalog.FindPage(slug) is not null ? Results.Redirect(ContentUrls.PagePath(slug))
        : Results.NotFound();
}
