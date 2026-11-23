using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Primitives;
using Weesite.Web.Agents;

namespace Weesite.Web.Tests;

/// <summary>llms.txt, llms-full.txt, .md URLs and Markdown negotiation.</summary>
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed class AgentTests(SharedSite shared)
{
    [Test]
    public async Task Llms_txt_lists_posts_and_pages_linked_to_their_markdown()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/llms.txt", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/plain");
        await Assert.That(text).StartsWith("# Example Site\n\n> A sample Weesite\n\n");
        await Assert.That(text).Contains("## Posts\n\n- [Moving from Bicep to OpenTofu](http://localhost/blog/moving-from-bicep-to-opentofu.md): ");
        await Assert.That(text).Contains("\n- [Notes on static rendering](http://localhost/blog/notes-on-static-rendering.md): Why every page is plain server-rendered HTML.\n");
        await Assert.That(text).Contains("\n- [Hello, world](http://localhost/blog/hello-world.md): The first post of the sample site.\n");
        await Assert.That(text).Contains("## Pages\n\n- [About](http://localhost/about.md)");
        // The special pages Site settings place, at their addresses (F-0233).
        await Assert.That(text).Contains("\n- [Privacy policy](http://localhost/privacy.md): ");
        await Assert.That(text).Contains("\n- [Terms of use](http://localhost/terms.md): ");
        await Assert.That(text).Contains("## Optional\n\n- [Everything in one file](http://localhost/llms-full.txt)");
    }

    [Test]
    public async Task Llms_full_txt_has_every_post_and_page_in_full()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var text = await client.GetStringAsync(new Uri("/llms-full.txt", UriKind.Relative));

        await Assert.That(text).Contains("# Hello, world\n\nThis is the sample site");
        await Assert.That(text).Contains("# Notes on static rendering\n");
        await Assert.That(text).Contains("# About\n");
        await Assert.That(text).Contains("url: \"http://localhost/privacy\"\n");
        await Assert.That(text).Contains("# Terms of use\n");
        await Assert.That(text.IndexOf("# Notes on static rendering", StringComparison.Ordinal))
            .IsLessThan(text.IndexOf("# Hello, world", StringComparison.Ordinal));
    }

    [Test]
    public async Task Md_url_returns_the_post_as_markdown_with_front_matter()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/blog/notes-on-static-rendering.md", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/markdown");
        await Assert.That(response.Headers.GetValues("X-Content-Type-Options")).Contains("nosniff");
        await Assert.That(text).StartsWith(
            "---\ntitle: Notes on static rendering\nurl: \"http://localhost/blog/notes-on-static-rendering\"\ndate: 2026-02-03\nauthor: Example Author\ntags: [web, performance]\nsummary: Why every page is plain server-rendered HTML.\nlanguage: en\n---\n\n# Notes on static rendering\n\n");
        await Assert.That(text).Contains("](http://localhost/media/");
        await Assert.That(text).DoesNotContain("](/");
    }

    [Test]
    [Arguments("/about.md", "# About")]
    [Arguments("/blog/hello-world.md", "# Hello, world")]
    [Arguments("/privacy.md", "# Privacy policy")]
    [Arguments("/terms.md", "# Terms of use")]
    [Arguments("/contact.md", "# Contact")]
    public async Task Md_urls_work_for_posts_and_pages(string path, string heading)
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        var text = await client.GetStringAsync(new Uri(path, UriKind.Relative));

        await Assert.That(text).Contains($"\n{heading}\n");
    }

    [Test]
    [Arguments("/no-such-page.md")]
    [Arguments("/blog/no-such-post.md")]
    [Arguments("/blog/about.md")]
    [Arguments("/hello-world.md")]
    [Arguments("/index.md")]
    public async Task Unknown_md_urls_are_not_found(string path)
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("/blog/hello-world", "# Hello, world")]
    [Arguments("/about", "# About")]
    [Arguments("/privacy", "# Privacy policy")]
    public async Task Accept_text_markdown_gets_markdown_and_varies_by_accept(string path, string heading)
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("Accept", "text/markdown, text/html;q=0.9, */*;q=0.8");

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/markdown");
        await Assert.That(response.Headers.Vary).Contains("Accept");
        await Assert.That(text).Contains($"\n{heading}\n");
    }

    [Test]
    public async Task Browsers_get_html_that_also_varies_by_accept()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/blog/hello-world", UriKind.Relative));
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        await Assert.That(response.Headers.Vary).Contains("Accept");
        await Assert.That(html).Contains("<link rel=\"alternate\" type=\"text/markdown\" title=\"Markdown\" href=\"http://localhost/blog/hello-world.md\" />");
    }

    [Test]
    public async Task The_home_pages_page_is_at_index_md_and_by_negotiation_on_the_sites_address()
    {
        // F-0233: its Markdown link was /.md, which nothing answers.
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(Path.Combine(copy.Root, "content", "pages", "welcome.md"), "---\nkind: page\ntitle: Welcome\n---\n\nHello from the home page.\n");
        var settings = Path.Combine(copy.Root, "settings", "site.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(settings))!.AsObject();
        json["home"] = new JsonObject { ["show"] = "page", ["page"] = "pages/welcome.md" };
        await File.WriteAllTextAsync(settings, json.ToJsonString());
        await using var factory = new SiteFactory(copy.Root);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.Add("Accept", "text/markdown");

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        var file = await client.GetStringAsync(new Uri("/index.md", UriKind.Relative));
        var llms = await client.GetStringAsync(new Uri("/llms.txt", UriKind.Relative));
        using var negotiated = await client.SendAsync(request);

        await Assert.That(html).Contains("<link rel=\"alternate\" type=\"text/markdown\" title=\"Markdown\" href=\"http://localhost/index.md\" />");
        await Assert.That(file).Contains("url: \"http://localhost/\"\n").And.Contains("\n# Welcome\n\nHello from the home page.");
        await Assert.That(llms).Contains("\n- [Welcome](http://localhost/index.md)\n");
        await Assert.That(negotiated.Content.Headers.ContentType?.MediaType).IsEqualTo("text/markdown");
        await Assert.That(negotiated.Headers.Vary).Contains("Accept");
        await Assert.That(await negotiated.Content.ReadAsStringAsync()).Contains("\n# Welcome\n");
    }

    [Test]
    public async Task Old_slugs_redirect_before_negotiation()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/blog/welcome", UriKind.Relative));
        request.Headers.Add("Accept", "text/markdown");

        using var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MovedPermanently);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo("/blog/hello-world");
    }

    [Test]
    [Arguments("text/markdown", true)]
    [Arguments("text/markdown, text/html", true)]
    [Arguments("text/html;q=0.9, text/markdown", true)]
    [Arguments("text/html, text/markdown;q=0.5", false)]
    [Arguments("text/markdown;q=0", false)]
    [Arguments("*/*", false)]
    [Arguments("text/*", false)]
    [Arguments("", false)]
    public async Task Markdown_is_chosen_only_when_asked_for_explicitly(string accept, bool expected) =>
        await Assert.That(AgentEndpoints.PrefersMarkdown(new StringValues(accept))).IsEqualTo(expected);

    [Test]
    public async Task Front_matter_quotes_what_yaml_would_misread_and_links_stay_on_their_hosts()
    {
        // F-0116: tags and titles such as 2024 or true went out unquoted, and "](//host" became
        // the site's own address.
        var item = new Weesite.Content.ContentItem(Weesite.Content.ContentKind.Post, "a", "2024", "en", "See [x](//cdn.example/x) and [y](/about).", "posts/a.md")
        {
            Date = new DateOnly(2026, 1, 1),
            Tags = ["true", "web"],
            Summary = "- a list?",
        };

        var markdown = AgentText.Markdown(item, Weesite.Content.SiteSettings.Empty, "https://example.com");

        await Assert.That(markdown).Contains("title: \"2024\"\n");
        await Assert.That(markdown).Contains("tags: [\"true\", web]\n");
        await Assert.That(markdown).Contains("summary: \"- a list?\"\n");
        await Assert.That(markdown).Contains("[x](//cdn.example/x)");
        await Assert.That(markdown).Contains("[y](https://example.com/about)");
    }
}
