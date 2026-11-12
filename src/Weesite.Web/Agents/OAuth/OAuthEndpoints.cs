using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Weesite.Content;
using Weesite.Web.Admin;

namespace Weesite.Web.Agents.OAuth;

/// <summary>
/// Weesite's own small OAuth 2.1 authorization server for the editor MCP endpoint (ADR 0007):
/// its metadata (RFC 8414), client registration (RFC 7591), the editor's consent, the token
/// endpoint and revocation (RFC 7009). Editors sign in with the admin sign-in.
/// </summary>
internal static class OAuthEndpoints
{
    /// <summary>The CORS policy of the endpoints clients call from a browser: any origin, no cookies.</summary>
    public const string CorsPolicy = "oauth";

    private static readonly string[] ResponseTypes = ["code"];
    private static readonly string[] GrantTypes = ["authorization_code", "refresh_token"];
    private static readonly string[] ChallengeMethods = ["S256"];
    private static readonly string[] AuthMethods = ["none"];
    private static readonly string[] BearerMethods = ["header"];

    /// <summary>Adds the store, the client registry, the token service and the CORS policy.</summary>
    /// <param name="services">The services.</param>
    /// <param name="inMemory">Whether to keep clients, codes and grants in memory rather than Table Storage.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddOAuthServer(this IServiceCollection services, bool inMemory)
    {
        if (inMemory)
        {
            services.AddSingleton<IOAuthStore, MemoryOAuthStore>();
        }
        else
        {
            services.AddSingleton<IOAuthStore, TableOAuthStore>();
        }

        services.AddSingleton<ClientRegistry>();
        services.AddSingleton<OAuthTokens>();
        services.AddSingleton<AuthorizationRequests>();
        services.AddSingleton<OAuthCleanUp>();
        services.AddHostedService(provider => provider.GetRequiredService<OAuthCleanUp>());
        services.AddHttpClient(ClientRegistry.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = ClientRegistry.ConnectToPublicAddressAsync,
                UseCookies = false,
                UseProxy = false,
            });
        // The editor MCP endpoint's bearer tokens (EditorBearerHandler).
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, EditorBearerHandler>(EditorBearerHandler.SchemeName, null);
        services.AddAuthorizationBuilder().AddPolicy(EditorBearerHandler.Policy, policy => policy
            .AddAuthenticationSchemes(EditorBearerHandler.SchemeName)
            .RequireRole(nameof(EditorRole.Admin), nameof(EditorRole.Author)));
        services.AddCors(options => options.AddPolicy(CorsPolicy, new CorsPolicyBuilder()
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .WithMethods("GET", "POST")
            .Build()));
        return services;
    }

    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The app.</param>
    public static void MapOAuthServer(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext context, AuthorizationRequests requests) =>
        {
            var issuer = requests.Issuer(context.Request);
            return Results.Json(new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = issuer + "/oauth/authorize",
                ["token_endpoint"] = issuer + "/oauth/token",
                ["registration_endpoint"] = issuer + "/oauth/register",
                ["revocation_endpoint"] = issuer + "/oauth/revoke",
                ["response_types_supported"] = ResponseTypes,
                ["grant_types_supported"] = GrantTypes,
                ["code_challenge_methods_supported"] = ChallengeMethods,
                ["token_endpoint_auth_methods_supported"] = AuthMethods,
                ["revocation_endpoint_auth_methods_supported"] = AuthMethods,
                ["client_id_metadata_document_supported"] = true,
                ["authorization_response_iss_parameter_supported"] = true,
            });
        }).RequireCors(CorsPolicy);

        // The protected resource metadata of the editor MCP endpoint (RFC 9728): the 401 from
        // /mcp/editor points here, and this names the authorization server. The well-known path
        // without the resource's path answers too, for clients that look there.
        foreach (var path in new[] { "/.well-known/oauth-protected-resource" + AuthorizationRequests.ResourcePath, "/.well-known/oauth-protected-resource" })
        {
            app.MapGet(path, (HttpContext context, AuthorizationRequests requests, ContentCatalogProvider content) => Results.Json(new Dictionary<string, object>
            {
                ["resource"] = requests.Resource(context.Request),
                ["authorization_servers"] = new[] { requests.Issuer(context.Request) },
                ["bearer_methods_supported"] = BearerMethods,
                ["resource_name"] = content.Current.Site.Name + " (editor)",
            })).RequireCors(CorsPolicy);
        }

        app.MapPost("/oauth/register", async (HttpContext context, ClientRegistry clients, TimeProvider time) =>
        {
            ClientMetadata? metadata;
            try
            {
                metadata = await context.Request.ReadFromJsonAsync<ClientMetadata>(context.RequestAborted);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                metadata = null;
            }

            if (metadata is null)
            {
                return Error("invalid_client_metadata", "Send the client's metadata as JSON.");
            }

            var (client, error) = await clients.RegisterAsync(metadata, context.RequestAborted);
            return client is null
                ? Error(error!.Error, error.Description)
                : Results.Json(
                    new Dictionary<string, object>
                    {
                        ["client_id"] = client.Id,
                        ["client_id_issued_at"] = time.GetUtcNow().ToUnixTimeSeconds(),
                        ["client_name"] = client.Name,
                        ["redirect_uris"] = client.RedirectUris,
                        ["token_endpoint_auth_method"] = "none",
                        ["grant_types"] = GrantTypes,
                        ["response_types"] = ResponseTypes,
                    },
                    statusCode: StatusCodes.Status201Created);
        }).RequireCors(CorsPolicy).DisableAntiforgery();

        // The consent page's form (Components/Pages/OAuthConsent.razor) posts here: the editor's
        // decision, with the request again, checked again.
        app.MapPost("/oauth/authorize/decision", async (HttpContext context, IAntiforgery antiforgery, AuthorizationRequests requests, OAuthTokens tokens, IAuditLog audit, TimeProvider time) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var parameters = AuthorizeParameters.From(name => form[name].FirstOrDefault());
            var check = await requests.CheckAsync(parameters, context.Request, context.RequestAborted);
            if (check.ErrorRedirect is { } errorRedirect)
            {
                return Results.Redirect(errorRedirect);
            }

            if (check.Client is not { } client)
            {
                return Results.BadRequest(check.ErrorPage);
            }

            var email = context.User.FindFirstValue(ClaimTypes.Email)!;
            if (form["decision"] != "allow")
            {
                await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), email, "oauth-denied", client.Name), context.RequestAborted);
                return Results.Redirect(requests.Reply(parameters.RedirectUri!, context.Request, parameters.State, ("error", "access_denied"), ("error_description", "The editor declined.")));
            }

            var code = await tokens.IssueCodeAsync(client, parameters.RedirectUri!, parameters.CodeChallenge!, email, check.Resource, form["publish"] == "yes", context.RequestAborted);
            return Results.Redirect(requests.Reply(parameters.RedirectUri!, context.Request, parameters.State, ("code", code)));
        }).RequireAuthorization(EditorClaims.EditorPolicy).DisableAntiforgery();

        // Connected apps (Your profile, Editors): an editor revokes their own grants, an admin anyone's.
        app.MapPost("/admin/connected-apps/revoke", async ([FromForm] string grant, [FromForm] string? returnTo, HttpContext context, OAuthTokens tokens) =>
        {
            var revoked = await tokens.RevokeAsync(grant, context.User, context.RequestAborted);
            var back = returnTo == "editors" && context.User.IsInRole(nameof(EditorRole.Admin)) ? "/admin/editors" : "/admin/profile";
            return Results.Redirect($"{back}?apps={(revoked ? "revoked" : "missing")}#connected-apps");
        }).RequireAuthorization(EditorClaims.EditorPolicy);

        app.MapPost("/oauth/token", async (HttpContext context, OAuthTokens tokens) =>
        {
            if (!context.Request.HasFormContentType)
            {
                return Error("invalid_request", "Send the request as application/x-www-form-urlencoded.");
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            string? Get(string name) => form[name].FirstOrDefault();
            var (result, error) = Get("grant_type") switch
            {
                "authorization_code" => await tokens.ExchangeCodeAsync(Get("code"), Get("client_id"), Get("redirect_uri"), Get("code_verifier"), Get("resource"), context.RequestAborted),
                "refresh_token" => await tokens.RefreshAsync(Get("refresh_token"), Get("client_id"), Get("resource"), context.RequestAborted),
                _ => (null, new OAuthError("unsupported_grant_type", "Use authorization_code or refresh_token.")),
            };

            NoStore(context);
            return result is null
                ? Error(error!.Error, error.Description)
                : Results.Json(new Dictionary<string, object>
                {
                    ["access_token"] = result.AccessToken,
                    ["token_type"] = "Bearer",
                    ["expires_in"] = result.ExpiresIn,
                    ["refresh_token"] = result.RefreshToken,
                });
        }).RequireCors(CorsPolicy).DisableAntiforgery();

        app.MapPost("/oauth/revoke", async (HttpContext context, OAuthTokens tokens) =>
        {
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                await tokens.RevokeAsync(form["token"].FirstOrDefault(), form["client_id"].FirstOrDefault(), context.RequestAborted);
            }

            // RFC 7009: the same answer whether or not the token was known.
            return Results.Ok();
        }).RequireCors(CorsPolicy).DisableAntiforgery();
    }

    private static IResult Error(string error, string description) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description }, statusCode: StatusCodes.Status400BadRequest);

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        context.Response.Headers[HeaderNames.Pragma] = "no-cache";
    }
}
