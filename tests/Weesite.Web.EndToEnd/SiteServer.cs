using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc.Testing;
using TUnit.Core.Interfaces;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// The real app on a free Kestrel port, shared by every test in the session. Later it gets the
/// seed content, Azurite and fakes for embeddings and sign-in (see the brief's testing section).
/// </summary>
[SuppressMessage("Performance", "CA1812", Justification = "Created by TUnit's ClassDataSource.")]
internal sealed class SiteServer : IAsyncInitializer, IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();

    /// <summary>Gets the address the app listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();
        BaseAddress = _factory.ClientOptions.BaseAddress;
        return Task.CompletedTask;
    }

    /// <summary>Gets the absolute address of a path on the site, as Playwright takes it.</summary>
    /// <param name="path">A path such as <c>/about</c>.</param>
    /// <returns>The absolute address.</returns>
    public string At(string path) => new Uri(BaseAddress, path).ToString();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
