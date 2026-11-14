using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>
/// The sign-in cookie's role check (F-0118): every other test signs in through a stand-in, so the
/// real cookie path had none.
/// </summary>
internal sealed class SignInRecheckTests
{
    [Test]
    public async Task A_removed_editor_s_cookie_is_rejected_and_signed_out()
    {
        var (context, auth) = Context(EditorRole.Admin, found: null);

        await SignIn.RecheckRoleAsync(context);

        await Assert.That(context.Principal).IsNull();
        await Assert.That(auth.SignedOut).IsEqualTo(EditorClaims.Scheme);
    }

    [Test]
    public async Task A_demoted_admin_s_cookie_is_replaced_with_the_author_role()
    {
        var (context, auth) = Context(EditorRole.Admin, found: EditorRole.Author);

        await SignIn.RecheckRoleAsync(context);

        await Assert.That(context.Principal!.IsInRole(nameof(EditorRole.Author))).IsTrue();
        await Assert.That(context.Principal!.IsInRole(nameof(EditorRole.Admin))).IsFalse();
        await Assert.That(context.Principal!.FindFirst(ClaimTypes.Email)?.Value).IsEqualTo("editor@example.com");
        await Assert.That(context.ShouldRenew).IsTrue();
        await Assert.That(auth.SignedOut).IsNull();
    }

    [Test]
    public async Task An_unchanged_role_keeps_the_cookie_as_it_is()
    {
        var (context, _) = Context(EditorRole.Author, found: EditorRole.Author);
        var before = context.Principal;

        await SignIn.RecheckRoleAsync(context);

        await Assert.That(context.Principal).IsSameReferenceAs(before);
        await Assert.That(context.ShouldRenew).IsFalse();
    }

    [Test]
    public async Task A_cookie_without_its_sign_in_time_is_rejected()
    {
        var (context, auth) = Context(EditorRole.Author, found: EditorRole.Author, signedIn: false);

        await SignIn.RecheckRoleAsync(context);

        await Assert.That(context.Principal).IsNull();
        await Assert.That(auth.SignedOut).IsEqualTo(EditorClaims.Scheme);
    }

    [Test]
    public async Task A_sign_in_older_than_the_session_lifetime_is_rejected()
    {
        var (context, auth) = Context(EditorRole.Author, found: EditorRole.Author, signedInAgo: SignIn.SessionLifetime + TimeSpan.FromMinutes(1));

        await SignIn.RecheckRoleAsync(context);

        await Assert.That(context.Principal).IsNull();
        await Assert.That(auth.SignedOut).IsEqualTo(EditorClaims.Scheme);
    }

    [Test]
    public async Task Ending_all_sessions_after_the_sign_in_rejects_the_cookie()
    {
        var (context, auth) = Context(EditorRole.Author, found: EditorRole.Author, signedInAgo: TimeSpan.FromHours(2), sessionsEndedAgo: TimeSpan.FromHours(1));

        await SignIn.RecheckRoleAsync(context);

        await Assert.That(context.Principal).IsNull();
        await Assert.That(auth.SignedOut).IsEqualTo(EditorClaims.Scheme);
    }

    [Test]
    public async Task A_sign_in_after_the_sessions_ended_is_kept()
    {
        var (context, auth) = Context(EditorRole.Author, found: EditorRole.Author, signedInAgo: TimeSpan.FromHours(1), sessionsEndedAgo: TimeSpan.FromHours(2));
        var before = context.Principal;

        await SignIn.RecheckRoleAsync(context);

        await Assert.That(context.Principal).IsSameReferenceAs(before);
        await Assert.That(auth.SignedOut).IsNull();
    }

    [Test]
    public async Task A_changed_role_keeps_the_sign_in_time()
    {
        var (context, _) = Context(EditorRole.Admin, found: EditorRole.Author);
        var signedIn = EditorClaims.SignedInTime(context.Principal!);

        await SignIn.RecheckRoleAsync(context);

        await Assert.That(EditorClaims.SignedInTime(context.Principal!)).IsEqualTo(signedIn);
    }

    private static (CookieValidatePrincipalContext Context, RecordingAuthentication Auth) Context(EditorRole signedInAs, EditorRole? found, bool signedIn = true, TimeSpan? signedInAgo = null, TimeSpan? sessionsEndedAgo = null)
    {
        var now = DateTimeOffset.UtcNow;
        var auth = new RecordingAuthentication();
        var services = new ServiceCollection()
            .AddMemoryCache()
            .AddSingleton<IEditorDirectory>(new Directory(found, now - sessionsEndedAgo))
            .AddSingleton<IAuthenticationService>(auth)
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var principal = EditorClaims.Principal("google", "123", "editor@example.com", "Editor", signedInAs, signedIn ? now - (signedInAgo ?? TimeSpan.FromMinutes(5)) : null);
        var ticket = new AuthenticationTicket(principal, EditorClaims.Scheme);
        var scheme = new AuthenticationScheme(EditorClaims.Scheme, null, typeof(CookieAuthenticationHandler));
        return (new CookieValidatePrincipalContext(http, scheme, new CookieAuthenticationOptions(), ticket), auth);
    }

    private sealed class Directory(EditorRole? role, DateTimeOffset? sessionsEnded) : IEditorDirectory
    {
        public Task<EditorRole?> FindRoleAsync(string email, CancellationToken cancellationToken) => Task.FromResult(role);

        public Task<DateTimeOffset?> FindSessionsEndedAsync(string email, CancellationToken cancellationToken) => Task.FromResult(sessionsEnded);
    }

    private sealed class RecordingAuthentication : IAuthenticationService
    {
        public string? SignedOut { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOut = scheme;
            return Task.CompletedTask;
        }
    }
}
