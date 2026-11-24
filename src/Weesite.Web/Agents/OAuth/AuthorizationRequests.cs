using Microsoft.AspNetCore.WebUtilities;
using Weesite.Web.Seo;

namespace Weesite.Web.Agents.OAuth;

/// <summary>An authorization request's parameters (RFC 6749 section 4.1.1, RFC 7636, RFC 8707).</summary>
/// <param name="ClientId">The client ID.</param>
/// <param name="RedirectUri">Where the code goes.</param>
/// <param name="ResponseType">Must be <c>code</c>.</param>
/// <param name="State">The client's state, sent back as it came.</param>
/// <param name="CodeChallenge">The PKCE challenge.</param>
/// <param name="CodeChallengeMethod">Must be <c>S256</c>.</param>
/// <param name="Resource">The resource the tokens are for; the editor MCP endpoint when absent.</param>
internal sealed record AuthorizeParameters(
    string? ClientId,
    string? RedirectUri,
    string? ResponseType,
    string? State,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    string? Resource)
{
    /// <summary>Reads the parameters from a query string or form.</summary>
    /// <param name="get">Gets one parameter by name.</param>
    /// <returns>The parameters.</returns>
    public static AuthorizeParameters From(Func<string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        return new(get("client_id"), get("redirect_uri"), get("response_type"), get("state"), get("code_challenge"), get("code_challenge_method"), get("resource"));
    }
}

/// <summary>The outcome of checking an authorization request.</summary>
/// <param name="Client">The client, when the request may proceed to consent.</param>
/// <param name="Resource">The resource the tokens will be for.</param>
/// <param name="ErrorPage">An error to show here; errors are never sent to the client's redirect URI.</param>
internal sealed record AuthorizeCheck(OAuthClient? Client, string Resource, string? ErrorPage);

/// <summary>
/// Checks authorization requests (ADR 0007) and builds the replies to the client's redirect URI.
/// Errors in a request are shown on the page, never sent: only the editor's decision on a valid
/// request goes back to the client.
/// </summary>
internal sealed class AuthorizationRequests(ClientRegistry clients, SiteUrls urls)
{
    /// <summary>The path of the editor MCP endpoint, the one resource tokens are for.</summary>
    public const string ResourcePath = "/mcp/editor";

    /// <summary>Gets the issuer: the site's origin.</summary>
    /// <param name="request">The current request.</param>
    /// <returns>Such as <c>https://example.com</c>.</returns>
    public string Issuer(HttpRequest request) => urls.Origin(request);

    /// <summary>Gets the resource tokens are for: the editor MCP endpoint's URL.</summary>
    /// <param name="request">The current request.</param>
    /// <returns>Such as <c>https://example.com/mcp/editor</c>.</returns>
    public string Resource(HttpRequest request) => urls.Origin(request) + ResourcePath;

    /// <summary>Checks a request.</summary>
    /// <param name="parameters">Its parameters.</param>
    /// <param name="request">The HTTP request (for the issuer and resource).</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The outcome.</returns>
    public async Task<AuthorizeCheck> CheckAsync(AuthorizeParameters parameters, HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(request);
        var resource = Resource(request);

        // Each parameter at most once (RFC 6749 §3.1): with two, which one counts is a guess.
        if (Repeated(request))
        {
            return new(null, resource, "The app's request repeats a parameter, so it can't be read safely.");
        }

        var client = await clients.FindAsync(parameters.ClientId, cancellationToken);
        if (client is null)
        {
            return new(null, resource, "The app that sent you here isn't registered, or its description couldn't be read.");
        }

        if (!ClientRegistry.IsRegisteredRedirect(client, parameters.RedirectUri))
        {
            return new(null, resource, "The app asked to send you back to an address it didn't register.");
        }

        // Errors show here, for every app, and are never sent to its redirect URI (RFC 6749
        // §4.1.2.1 allows either). Anyone can register an app with any redirect URI, or publish a
        // metadata document naming one, so a malformed request redirected there would make the
        // site an open redirector (RFC 9700 §4.11.2, T-0122, T-0177). Only a valid request, after
        // the editor's decision on the consent page, goes back to the app.
        AuthorizeCheck Error(string description) => new(null, resource, $"The app's request can't be used: {description}");

        if (parameters.ResponseType != "code")
        {
            return Error("Only response_type=code is supported.");
        }

        if (parameters.CodeChallengeMethod != "S256" || !OAuthTokens.IsChallenge(parameters.CodeChallenge))
        {
            return Error("PKCE is required: code_challenge with code_challenge_method=S256.");
        }

        if (parameters.Resource is { Length: > 0 } asked && asked.TrimEnd('/') != resource)
        {
            return Error($"Tokens are only issued for {resource}.");
        }

        return new(client, resource, null);
    }

    private static readonly string[] ParameterNames = ["client_id", "redirect_uri", "response_type", "state", "code_challenge", "code_challenge_method", "resource"];

    private static bool Repeated(HttpRequest request) =>
        ParameterNames.Any(name => request.Query[name].Count > 1 || (request.HasFormContentType && request.Form[name].Count > 1));

    /// <summary>Builds a reply to the client: its redirect URI with the parameters, the state and the issuer (RFC 9207).</summary>
    /// <param name="redirectUri">The redirect URI.</param>
    /// <param name="request">The current request.</param>
    /// <param name="state">The client's state.</param>
    /// <param name="parameters">The code, or the error.</param>
    /// <returns>The URL to redirect to.</returns>
    public string Reply(string redirectUri, HttpRequest request, string? state, params (string Name, string Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var query = new List<KeyValuePair<string, string?>>(parameters.Select(p => new KeyValuePair<string, string?>(p.Name, p.Value)));
        if (state is not null)
        {
            query.Add(new("state", state));
        }

        query.Add(new("iss", Issuer(request)));
        return QueryHelpers.AddQueryString(redirectUri, query);
    }

    /// <summary>
    /// Gets the consent page's <c>form-action</c> source for the redirect URI: browsers check the
    /// redirect after the form post against it.
    /// </summary>
    /// <param name="redirectUri">The redirect URI.</param>
    /// <returns>An origin such as <c>http://127.0.0.1:33418</c>, or a scheme such as <c>cursor:</c>.</returns>
    public static string FormActionSource(string redirectUri)
    {
        var uri = new Uri(redirectUri);
        return uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : uri.Scheme + ":";
    }
}
