using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Weesite.Content;
using Weesite.Web.Email;

namespace Weesite.Web.Admin;

/// <summary>
/// Sign-in for <c>/admin</c> (brief, Editor and authentication; ADR 0004): OpenID Connect with
/// Microsoft (personal and work accounts) and Google, then a cookie. A provider is on only when
/// its client ID and secret are configured (from the environment's Key Vault in Azure). Only
/// allowlisted, verified email addresses get in.
/// </summary>
internal static partial class SignIn
{
    /// <summary>The Microsoft provider's scheme name.</summary>
    public const string Microsoft = "Microsoft";

    /// <summary>The Google provider's scheme name.</summary>
    public const string Google = "Google";

    /// <summary>The tenant of personal Microsoft accounts, whose email addresses Microsoft verifies.</summary>
    public const string ConsumerTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    /// <summary>Gets the configured providers, in the order the sign-in page shows them.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The scheme names of the providers with a client ID and secret.</returns>
    public static IReadOnlyList<string> ConfiguredProviders(IConfiguration configuration) =>
        new[] { Microsoft, Google }
            .Where(p => !string.IsNullOrWhiteSpace(configuration[$"Authentication:{p}:ClientId"])
                && !string.IsNullOrWhiteSpace(configuration[$"Authentication:{p}:ClientSecret"]))
            .ToList();

    /// <summary>Adds the cookie, the configured providers and the editor policies.</summary>
    /// <param name="builder">The app builder.</param>
    public static void AddEditorSignIn(this WebApplicationBuilder builder)
    {
        var configuration = (IConfiguration)builder.Configuration;
        builder.Services.AddSingleton<ConfiguredAdmins>();
        builder.Services.AddSingleton<IEditorDirectory, EditorDirectory>();
        builder.Services.AddSingleton<EditorChanges>();
        builder.Services.AddSingleton<EditorInvitations>();
        builder.Services.AddSingleton<MediaReplacer>();
        builder.Services.AddEmail(builder.Configuration);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddMemoryCache();

        var auth = builder.Services.AddAuthentication(EditorClaims.Scheme)
            .AddCookie(EditorClaims.Scheme, options =>
            {
                options.Cookie.Name = "weesite.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.LoginPath = "/admin/sign-in";
                options.AccessDeniedPath = "/admin/forbidden";
                options.ReturnUrlParameter = "returnUrl";

                // A removed editor is signed out, and a changed role applies, within RoleRecheck.
                options.Events.OnValidatePrincipal = RecheckRoleAsync;
            });

        foreach (var provider in ConfiguredProviders(configuration))
        {
            auth.AddOpenIdConnect(provider, provider, options => Configure(options, provider, configuration));
        }

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(EditorClaims.EditorPolicy, p => p.RequireRole(nameof(EditorRole.Admin), nameof(EditorRole.Author)))
            .AddPolicy(EditorClaims.AdminPolicy, p => p.RequireRole(nameof(EditorRole.Admin)));
    }

    /// <summary>
    /// Gets the email address a provider vouches for: Google's when <c>email_verified</c> is true;
    /// Microsoft's for personal accounts, and for work accounts only from trusted tenants
    /// (<c>Authentication:Microsoft:TrustedTenants</c>), because any tenant can put any address
    /// in its users' email claim.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="user">The user from the ID token.</param>
    /// <param name="trustedTenants">Microsoft tenants whose email claims are trusted.</param>
    /// <returns>The verified address, or null.</returns>
    public static string? VerifiedEmail(string provider, ClaimsPrincipal user, IReadOnlySet<string> trustedTenants)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(trustedTenants);
        var email = user.FindFirst("email")?.Value;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        return provider switch
        {
            Google when string.Equals(user.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase) => email,
            Microsoft when user.FindFirst("tid")?.Value is { } tenant
                && (tenant == ConsumerTenant || trustedTenants.Contains(tenant)) => email,
            _ => null,
        };
    }

    /// <summary>
    /// Validates the issuer of a token from Microsoft's multi-tenant endpoint: it must name the
    /// tenant in the token's own <c>tid</c> claim.
    /// </summary>
    /// <param name="issuer">The token's issuer.</param>
    /// <param name="token">The token.</param>
    /// <returns>The issuer.</returns>
    public static string ValidateMicrosoftIssuer(string issuer, SecurityToken token)
    {
        var tenant = (token as JsonWebToken)?.TryGetPayloadValue<string>("tid", out var tid) == true ? tid : null;
        if (tenant is not null && string.Equals(issuer, $"https://login.microsoftonline.com/{tenant}/v2.0", StringComparison.Ordinal))
        {
            return issuer;
        }

        throw new SecurityTokenInvalidIssuerException($"Issuer '{issuer}' doesn't match the token's tenant.");
    }

    private static void Configure(OpenIdConnectOptions options, string provider, IConfiguration configuration)
    {
        options.SignInScheme = EditorClaims.Scheme;
        options.ClientId = configuration[$"Authentication:{provider}:ClientId"];
        options.ClientSecret = configuration[$"Authentication:{provider}:ClientSecret"];
        options.ResponseType = "code";
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");
        options.CallbackPath = provider == Microsoft ? "/signin-microsoft" : "/signin-google";
        options.AccessDeniedPath = "/admin/sign-in";

        // The provider's account chooser every time: otherwise a browser signed in to one account
        // goes straight through, and switching to another one means signing out of the provider.
        options.Prompt = "select_account";

        if (provider == Microsoft)
        {
            options.Authority = "https://login.microsoftonline.com/common/v2.0";
            options.TokenValidationParameters.IssuerValidator = (issuer, token, _) => ValidateMicrosoftIssuer(issuer, token);
        }
        else
        {
            options.Authority = "https://accounts.google.com";
        }

        var trustedTenants = (configuration["Authentication:Microsoft:TrustedTenants"] ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        options.Events.OnTokenValidated = async context =>
        {
            var services = context.HttpContext.RequestServices;
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Weesite.SignIn");
            var user = context.Principal!;
            var subject = user.FindFirst("oid")?.Value ?? user.FindFirst("sub")?.Value ?? "";
            var email = VerifiedEmail(provider, user, trustedTenants);
            var role = email is null ? null : await services.GetRequiredService<IEditorDirectory>().FindRoleAsync(email, context.HttpContext.RequestAborted);
            var audit = services.GetRequiredService<IAuditLog>();
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            if (email is null || role is null || subject.Length == 0)
            {
                // Not an editor: the address isn't recorded, only that someone tried.
                LogDenied(logger, provider);
                await audit.WriteAsync(new AuditEntry(now, "(not an editor)", "sign-in-refused", provider), context.HttpContext.RequestAborted);
                context.HandleResponse();
                context.Response.Redirect("/admin/denied");
                return;
            }

            LogSignedIn(logger, provider, role.Value);
            await audit.WriteAsync(new AuditEntry(now, email, "sign-in", provider), context.HttpContext.RequestAborted);
            await EnsureProfileAsync(services.GetRequiredService<AuthorProfiles>(), email, user.FindFirst("name")?.Value, context.HttpContext.RequestAborted);
            context.Principal = EditorClaims.Principal(provider, subject, email, user.FindFirst("name")?.Value, role.Value);
        };

        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.Redirect("/admin/sign-in?error=failed");
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Gives an editor without a profile one at their first sign-in, named as the provider names
    /// them (F-0088): their posts then have a byline straight away, as the profile page says. A
    /// profile they saved is never changed; without a name from the provider, nothing is created.
    /// </summary>
    /// <param name="profiles">The profiles.</param>
    /// <param name="email">The editor's address.</param>
    /// <param name="name">The name the provider gave, if any.</param>
    /// <param name="cancellationToken">Stops the save.</param>
    /// <returns>A task.</returns>
    internal static async Task EnsureProfileAsync(AuthorProfiles profiles, string email, string? name, CancellationToken cancellationToken)
    {
        var id = EditorRights.AuthorId(email);
        name = name?.Trim();

        // A name that's an email address would put the address in a public byline (F-0092).
        if (string.IsNullOrEmpty(name) || name.Contains('@', StringComparison.Ordinal) || await profiles.FindAsync(id, cancellationToken) is not null)
        {
            return;
        }

        await profiles.SaveAsync(new Author { Id = id, Name = name.Length > ProfileForm.MaxName ? name[..ProfileForm.MaxName].TrimEnd() : name }, cancellationToken);
    }

    /// <summary>How long a signed-in editor's role is trusted before it's looked up again.</summary>
    internal static readonly TimeSpan RoleRecheck = TimeSpan.FromSeconds(30);

    /// <summary>The cache key of an editor's current role.</summary>
    /// <param name="email">The address.</param>
    /// <returns>The key.</returns>
    internal static string RoleCacheKey(string email) => "editor-role:" + ConfiguredAdmins.Normalize(email);

    // Looks the editor up again (cached for RoleRecheck): signs them out when they're no longer
    // on the list, and updates the cookie when their role changed.
    internal static async Task RecheckRoleAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var email = principal?.FindFirst(ClaimTypes.Email)?.Value;
        if (principal is null || email is null)
        {
            context.RejectPrincipal();
            return;
        }

        var services = context.HttpContext.RequestServices;
        var role = await services.GetRequiredService<IMemoryCache>().GetOrCreateAsync(RoleCacheKey(email), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = RoleRecheck;
            return services.GetRequiredService<IEditorDirectory>().FindRoleAsync(email, context.HttpContext.RequestAborted);
        });

        if (role is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(EditorClaims.Scheme);
        }
        else if (!principal.IsInRole(role.Value.ToString()))
        {
            var id = principal.FindFirst(ClaimTypes.NameIdentifier)!.Value;
            var separator = id.IndexOf(':', StringComparison.Ordinal);
            context.ReplacePrincipal(EditorClaims.Principal(
                principal.FindFirst(EditorClaims.Provider)?.Value ?? id[..separator],
                id[(separator + 1)..],
                email,
                principal.Identity?.Name,
                role.Value));
            context.ShouldRenew = true;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in with {Provider} refused: not a verified, allowlisted address")]
    private static partial void LogDenied(ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed in with {Provider} as {Role}")]
    private static partial void LogSignedIn(ILogger logger, string provider, EditorRole role);
}
