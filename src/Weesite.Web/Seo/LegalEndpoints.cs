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
        // Temporary redirects: the chosen page can change. Not found when none is chosen, or the
        // chosen one isn't published.
        app.MapGet("/legal/privacy", (ContentCatalogProvider content) => ToPage(content.Current, content.Current.Site.PrivacyPage));
        app.MapGet("/legal/terms", (ContentCatalogProvider content) => ToPage(content.Current, content.Current.Site.TermsPage));

        // Entra reads this when the publisher domain is verified in the portal. The client ID is
        // public anyway: it's in every sign-in request.
        app.MapGet("/.well-known/microsoft-identity-association.json", (IConfiguration configuration) =>
            configuration["Authentication:Microsoft:ClientId"] is { Length: > 0 } clientId
                ? Results.Json(new { associatedApplications = new[] { new { applicationId = clientId } } })
                : Results.NotFound());
    }

    private static IResult ToPage(ContentCatalog catalog, string? slug) =>
        slug is not null && catalog.FindPage(slug) is { } page ? Results.Redirect("/" + page.Slug) : Results.NotFound();
}
