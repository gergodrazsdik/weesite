using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Weesite.Web.Tests;

/// <summary>Per-address rate limits on search, /mcp and sign-in (T-0053).</summary>
internal sealed class RateLimitTests
{
    [Test]
    public async Task Search_is_limited_per_address_and_says_when_to_retry()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder => builder.UseSetting("RateLimits:SearchPerMinute", "2"));
        using var client = factory.CreateClient();

        using var first = await Get(client, "/search?q=rendering", "203.0.113.1");
        using var second = await Get(client, "/search?q=static", "203.0.113.1");
        using var third = await Get(client, "/search?q=again", "203.0.113.1");
        using var otherAddress = await Get(client, "/search?q=rendering", "203.0.113.2");
        using var otherPage = await Get(client, "/blog", "203.0.113.1");

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(third.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(third.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0).IsGreaterThan(0);
        await Assert.That(await third.Content.ReadAsStringAsync()).Contains("Too many requests");
        await Assert.That(otherAddress.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(otherPage.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Sign_in_posts_and_callbacks_share_a_limit_but_the_sign_in_page_does_not()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder => builder.UseSetting("RateLimits:SignInPerMinute", "1"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var post = await Send(client, HttpMethod.Post, "/admin/sign-in", "198.51.100.7");
        using var callback = await Send(client, HttpMethod.Get, "/signin-google", "198.51.100.7");
        using var page = await Send(client, HttpMethod.Get, "/admin/sign-in", "198.51.100.7");

        await Assert.That(post.StatusCode).IsNotEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Mcp_is_limited_separately_from_search()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimits:McpPerMinute", "1");
            builder.UseSetting("RateLimits:SearchPerMinute", "1");
        });
        using var client = factory.CreateClient();

        using var search = await Get(client, "/search?q=rendering", "192.0.2.9");
        using var mcp = await Send(client, HttpMethod.Post, "/mcp", "192.0.2.9");
        using var mcpAgain = await Send(client, HttpMethod.Post, "/mcp", "192.0.2.9");

        await Assert.That(search.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(mcp.StatusCode).IsNotEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(mcpAgain.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task The_editor_endpoint_with_a_token_has_its_own_higher_limit()
    {
        // F-0068: an app working for an editor easily makes 60 calls a minute.
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimits:McpPerMinute", "1");
            builder.UseSetting("RateLimits:McpEditorPerMinute", "3");
        });
        using var client = factory.CreateClient();

        var withToken = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await Send(client, HttpMethod.Post, "/mcp/editor", "192.0.2.10", bearer: "some-token");
            withToken.Add(response.StatusCode);
        }

        using var publicCall = await Send(client, HttpMethod.Post, "/mcp", "192.0.2.10");
        using var noToken = await Send(client, HttpMethod.Post, "/mcp/editor", "192.0.2.10");

        await Assert.That(withToken.Take(3).All(s => s != HttpStatusCode.TooManyRequests)).IsTrue();
        await Assert.That(withToken[3]).IsEqualTo(HttpStatusCode.TooManyRequests);
        // Without a token it counts against the public limit, which the first call used up.
        await Assert.That(publicCall.StatusCode).IsNotEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(noToken.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string path, string address) =>
        Send(client, HttpMethod.Get, path, address);

    // The ingress appends the client's address to X-Forwarded-For; the last entry counts, so a
    // client can't pick its own partition by sending the header itself.
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string address, string? bearer = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Forwarded-For", $"10.9.9.9, {address}");
        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request);
    }
}
