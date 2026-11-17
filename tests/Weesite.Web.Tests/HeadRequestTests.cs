using System.Net;
using System.Text.RegularExpressions;

namespace Weesite.Web.Tests;

/// <summary>
/// HEAD answers like GET on the endpoints the app maps itself (F-0171). Kestrel leaves out the
/// body of a HEAD response; the test server doesn't, so these check the status and the headers.
/// </summary>
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed partial class HeadRequestTests(SharedSite shared)
{
    [Test]
    [Arguments("/media/b368a21f9e93-sample.png")]
    [Arguments("/feed.xml")]
    [Arguments("/feed.json")]
    [Arguments("/blog/tags/notes/feed.xml")]
    [Arguments("/blog/tags/notes/feed.json")]
    [Arguments("/sitemap.xml")]
    [Arguments("/robots.txt")]
    [Arguments("/llms.txt")]
    [Arguments("/llms-full.txt")]
    [Arguments("/blog/hello-world.md")]
    public async Task Head_answers_like_get(string path) =>
        await CheckAsync(path);

    [Test]
    public async Task Head_answers_on_an_open_graph_card()
    {
        // A card's address carries a hash, so it comes from a post.
        using var client = shared.Factory.CreateClient();
        var html = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var card = CardLink().Match(html);

        await Assert.That(card.Success).IsTrue();
        await CheckAsync(new Uri(card.Groups[1].Value).PathAndQuery);
    }

    [Test]
    public async Task A_head_request_doesn_t_leave_an_empty_page_in_the_cache()
    {
        // Whatever the output cache keeps for a HEAD, a GET after it still gets the whole feed.
        using var client = shared.Factory.CreateClient();
        using var head = new HttpRequestMessage(HttpMethod.Head, new Uri("/feed.json", UriKind.Relative));
        using var headResponse = await client.SendAsync(head);

        var body = await client.GetStringAsync(new Uri("/feed.json", UriKind.Relative));

        await Assert.That(headResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body).Contains("\"items\"");
    }

    private async Task CheckAsync(string path)
    {
        using var client = shared.Factory.CreateClient();
        using var get = await client.GetAsync(new Uri(path, UriKind.Relative));
        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(path, UriKind.Relative));
        using var head = await client.SendAsync(request);

        await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(head.Content.Headers.ContentType?.MediaType).IsEqualTo(get.Content.Headers.ContentType?.MediaType);
    }

    [GeneratedRegex("property=\"og:image\" content=\"([^\"]+)\"")]
    private static partial Regex CardLink();
}
