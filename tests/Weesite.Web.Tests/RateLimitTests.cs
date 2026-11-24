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
    public async Task The_editors_live_preview_has_a_limit_of_its_own()
    {
        // T-0122: rendering is the costliest thing an editor can ask for.
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder => builder.UseSetting("RateLimits:PreviewPerMinute", "1"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var first = await Send(client, HttpMethod.Post, "/admin/preview", "198.51.100.8");
        using var second = await Send(client, HttpMethod.Post, "/admin/preview", "198.51.100.8");
        using var page = await Send(client, HttpMethod.Get, "/admin/sign-in", "198.51.100.8");

        await Assert.That(first.StatusCode).IsNotEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
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

    [Test]
    public async Task An_ipv6_client_counts_as_its_64_network()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder => builder.UseSetting("RateLimits:SearchPerMinute", "1"));
        using var client = factory.CreateClient();

        using var first = await Get(client, "/search?q=rendering", "2001:db8:1:2::1");
        using var sameNetwork = await Get(client, "/search?q=rendering", "2001:db8:1:2:ffff::9");
        using var otherNetwork = await Get(client, "/search?q=rendering", "2001:db8:1:3::1");

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(sameNetwork.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(otherNetwork.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments("/contact/", "RateLimits:VisitorPerMinute")]
    [Arguments("/CONTACT", "RateLimits:VisitorPerMinute")]
    [Arguments("/admin/sign-in/", "RateLimits:SignInPerMinute")]
    [Arguments("/admin/preview/", "RateLimits:PreviewPerMinute")]
    public async Task A_post_counts_whatever_the_paths_case_or_trailing_slash(string path, string setting)
    {
        // Routing matches /contact/ as /contact, so the limit does too.
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder => builder.UseSetting(setting, "1"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var first = await Send(client, HttpMethod.Post, path, "198.51.100.20");
        using var second = await Send(client, HttpMethod.Post, path.TrimEnd('/'), "198.51.100.20");

        await Assert.That(first.StatusCode).IsNotEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task Without_a_proxy_in_front_the_forwarded_headers_are_ignored()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ForwardedHeaders:Proxies", "0");
            builder.UseSetting("RateLimits:SearchPerMinute", "1");
        });
        using var client = factory.CreateClient();

        using var first = await Get(client, "/search?q=rendering", "203.0.113.50");
        using var claimsAnother = await Get(client, "/search?q=rendering", "203.0.113.51");
        using var robots = new HttpRequestMessage(HttpMethod.Get, new Uri("/robots.txt", UriKind.Relative));
        robots.Headers.Add("X-Forwarded-Proto", "https");
        using var robotsAnswer = await client.SendAsync(robots);

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(claimsAnother.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(await robotsAnswer.Content.ReadAsStringAsync()).Contains("http://localhost/sitemap.xml");
    }

    [Test]
    public async Task Behind_two_proxies_the_entry_the_first_one_added_counts()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ForwardedHeaders:Proxies", "2");
            builder.UseSetting("RateLimits:SearchPerMinute", "1");
        });
        using var client = factory.CreateClient();

        // The second proxy's own address differs per request; the client's, which the first added, doesn't.
        using var first = await Get(client, "/search?q=rendering", "203.0.113.60, 10.0.0.1");
        using var sameClient = await Get(client, "/search?q=rendering", "203.0.113.60, 10.0.0.2");
        using var otherClient = await Get(client, "/search?q=rendering", "203.0.113.61, 10.0.0.1");
        using var robots = new HttpRequestMessage(HttpMethod.Get, new Uri("/robots.txt", UriKind.Relative));
        robots.Headers.Add("X-Forwarded-Proto", "https");
        using var robotsAnswer = await client.SendAsync(robots);

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(sameClient.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(otherClient.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await robotsAnswer.Content.ReadAsStringAsync()).Contains("https://localhost/sitemap.xml");
    }

    [Test]
    public async Task A_malformed_proxy_setting_stops_the_app_at_startup()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder => builder.UseSetting("ForwardedHeaders:KnownNetworks", "not-a-network"));

        await Assert.That(() => factory.CreateClient()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Upload_addresses_have_a_limit_of_their_own()
    {
        // T-0177: files sent to upload addresses neither use up the public /mcp limit nor share it.
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimits:McpPerMinute", "1");
            builder.UseSetting("RateLimits:McpUploadPerMinute", "2");
        });
        using var client = factory.CreateClient();

        var uploads = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await Send(client, HttpMethod.Put, "/mcp/editor/upload", "192.0.2.11", upload: "not-a-token");
            uploads.Add(response.StatusCode);
        }

        using var publicCall = await Send(client, HttpMethod.Post, "/mcp", "192.0.2.11");
        using var publicAgain = await Send(client, HttpMethod.Post, "/mcp", "192.0.2.11");
        using var noHeader = await Send(client, HttpMethod.Put, "/mcp/editor/upload", "192.0.2.12");
        using var noHeaderAgain = await Send(client, HttpMethod.Put, "/mcp/editor/upload", "192.0.2.12");
        using var noHeaderThird = await Send(client, HttpMethod.Put, "/mcp/editor/upload", "192.0.2.12");
        using var publicElsewhere = await Send(client, HttpMethod.Post, "/mcp", "192.0.2.12");

        await Assert.That(RateLimits.Kind(new Microsoft.AspNetCore.Http.DefaultHttpContext { Request = { Path = "/mcp/editor/upload" } })).IsEqualTo("mcp-upload");
        await Assert.That(uploads.Take(2).All(s => s == HttpStatusCode.Unauthorized)).IsTrue();
        await Assert.That(uploads[2]).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(publicCall.StatusCode).IsNotEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(publicAgain.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);

        // Without an Authorization header, an upload still counts on the upload limit, not /mcp's.
        await Assert.That(noHeader.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(noHeaderAgain.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(noHeaderThird.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(publicElsewhere.StatusCode).IsNotEqualTo(HttpStatusCode.TooManyRequests);
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string path, string address) =>
        Send(client, HttpMethod.Get, path, address);

    // The ingress appends the client's address to X-Forwarded-For; the last entry counts, so a
    // client can't pick its own partition by sending the header itself.
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string address, string? bearer = null, string? upload = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Forwarded-For", $"10.9.9.9, {address}");
        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        if (upload is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Upload", upload);
        }

        return await client.SendAsync(request);
    }
}
